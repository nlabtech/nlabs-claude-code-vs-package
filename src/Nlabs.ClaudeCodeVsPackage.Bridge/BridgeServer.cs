using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge;

/// <summary>
/// The local bridge server that Claude Code connects to as a native IDE.
///
/// Security posture (all of it testable):
///  - Listens only on 127.0.0.1; not reachable from the local network.
///  - Issues a per-session token and requires it in the IDE authorization header
///    (x-claude-code-ide-authorization), compared in constant time.
///  - Rejects any request that carries an Origin header: the real CLI never sends
///    Origin, but a malicious page in a browser does. This blocks browser-driven
///    attacks against the loopback listener.
///  - Host must be loopback.
///  - Accepts a single client; a second concurrent connection gets 409.
///  - Closes the connection on any message larger than 1 MB.
///  - Never writes the token, file contents or full paths to a log (this class logs nothing).
/// </summary>
public sealed class BridgeServer : IDisposable
{
    private const int MaxMessageBytes = 1 * 1024 * 1024; // 1 MB

    private readonly HttpListener _listener = new HttpListener();
    private readonly object _clientGate = new object();
    private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

    private WebSocket? _activeSocket;
    private CancellationTokenSource? _cts;

    /// <summary>The token a client must present in the IDE authorization header.</summary>
    public string Token { get; }

    /// <summary>The port the server listens on (set after Start).</summary>
    public int Port { get; private set; }

    /// <summary>Raised when a full text message arrives.</summary>
    public event EventHandler<string>? MessageReceived;

    public BridgeServer()
    {
        // A UUID token, matching what Claude Code's IDE clients present; it is written to
        // the discovery lock file and compared in constant time on each connection.
        Token = Guid.NewGuid().ToString();
    }

    public void Start()
    {
        Port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoopAsync(_cts.Token);
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break; // listener closed
            }

            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerRequest request = context.Request;

        // Browser defense: reject if Origin is present (the real CLI never sends it).
        if (request.Headers["Origin"] != null) { Reject(context, 403); return; }

        // Host must be loopback.
        string host = request.UserHostName ?? string.Empty;
        if (!host.StartsWith("127.0.0.1", StringComparison.Ordinal) &&
            !host.StartsWith("localhost", StringComparison.OrdinalIgnoreCase))
        {
            Reject(context, 403);
            return;
        }

        // Token check (constant time). Claude Code presents the lock-file token here.
        if (!CheckToken(request.Headers["x-claude-code-ide-authorization"])) { Reject(context, 401); return; }

        if (!request.IsWebSocketRequest) { Reject(context, 400); return; }

        // Single client: a second concurrent connection gets 409.
        lock (_clientGate)
        {
            if (_activeSocket != null) { Reject(context, 409); return; }
        }

        // Handshake: if the client requests a WebSocket subprotocol via
        // Sec-WebSocket-Protocol, echo the first one back on accept. Claude Code does not
        // require a fixed subprotocol, but a client that asks for one drops the handshake
        // (looking like a timeout) if the server does not confirm it.
        string? requestedSubprotocol = FirstSubprotocol(request.Headers["Sec-WebSocket-Protocol"]);

        HttpListenerWebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: requestedSubprotocol).ConfigureAwait(false);
        }
        catch
        {
            Reject(context, 500);
            return;
        }

        WebSocket socket = wsContext.WebSocket;
        lock (_clientGate) { _activeSocket = socket; }

        try
        {
            await ReceiveLoopAsync(socket, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_clientGate) { if (_activeSocket == socket) { _activeSocket = null; } }
            socket.Dispose();
        }
    }

    private async Task ReceiveLoopAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var builder = new StringBuilder();

        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            builder.Clear();
            int total = 0;
            WebSocketReceiveResult result;

            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct).ConfigureAwait(false);
                    return;
                }

                total += result.Count;
                if (total > MaxMessageBytes)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "message too large", ct).ConfigureAwait(false);
                    return;
                }

                builder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            }
            while (!result.EndOfMessage);

            MessageReceived?.Invoke(this, builder.ToString());
        }
    }

    /// <summary>Sends a text message to the connected client (concurrent sends are serialized).</summary>
    public async Task SendAsync(string message)
    {
        WebSocket? socket;
        lock (_clientGate) { socket = _activeSocket; }
        if (socket == null || socket.State != WebSocketState.Open) { return; }

        byte[] bytes = Encoding.UTF8.GetBytes(message);
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None)
                .ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private bool CheckToken(string? presentedToken)
    {
        if (string.IsNullOrEmpty(presentedToken)) { return false; }
        return FixedTimeEquals(presentedToken!, Token);
    }

    private static void Reject(HttpListenerContext context, int status)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.Close();
        }
        catch
        {
            // the response may already be closed
        }
    }

    // --- helpers ---

    /// <summary>
    /// Returns the first subprotocol from a comma-separated Sec-WebSocket-Protocol
    /// header, or null if none. The value must be echoed back on accept, otherwise a
    /// client that asked for one drops the handshake.
    /// </summary>
    private static string? FirstSubprotocol(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) { return null; }
        foreach (string part in header!.Split(','))
        {
            string trimmed = part.Trim();
            if (trimmed.Length > 0) { return trimmed; }
        }
        return null;
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>Constant-time string comparison (net472 has no built-in).</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        int diff = a.Length ^ b.Length;
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++) { diff |= a[i] ^ b[i]; }
        return diff == 0;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) { _listener.Stop(); } } catch { }
        _listener.Close();
        _sendGate.Dispose();
        _cts?.Dispose();
    }
}
