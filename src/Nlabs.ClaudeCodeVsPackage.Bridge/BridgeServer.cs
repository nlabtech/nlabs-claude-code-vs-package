using System;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge
{
    /// <summary>
    /// The local bridge server.
    ///
    /// Security posture (all of it testable):
    ///  - Listens only on 127.0.0.1; not reachable from the local network.
    ///  - Generates a 32-byte crypto-RNG token per session and compares in constant time.
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
        private readonly byte[] _tokenBytes = new byte[32];
        private readonly object _clientGate = new object();
        private readonly SemaphoreSlim _sendGate = new SemaphoreSlim(1, 1);

        private WebSocket? _activeSocket;
        private CancellationTokenSource? _cts;

        /// <summary>The token a client must present (hex).</summary>
        public string Token { get; }

        /// <summary>The port the server listens on (set after Start).</summary>
        public int Port { get; private set; }

        /// <summary>Raised when a full text message arrives.</summary>
        public event EventHandler<string>? MessageReceived;

        public BridgeServer()
        {
            using (var rng = RandomNumberGenerator.Create())
            {
                rng.GetBytes(_tokenBytes);
            }
            Token = ToHex(_tokenBytes);
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

            // Token check (constant time).
            if (!CheckToken(request.Headers["Authorization"])) { Reject(context, 401); return; }

            if (!request.IsWebSocketRequest) { Reject(context, 400); return; }

            // Single client: a second concurrent connection gets 409.
            lock (_clientGate)
            {
                if (_activeSocket != null) { Reject(context, 409); return; }
            }

            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
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

        private bool CheckToken(string? authorizationHeader)
        {
            if (string.IsNullOrEmpty(authorizationHeader)) { return false; }

            const string prefix = "Bearer ";
            if (!authorizationHeader!.StartsWith(prefix, StringComparison.Ordinal)) { return false; }

            string provided = authorizationHeader.Substring(prefix.Length).Trim();
            byte[]? providedBytes = FromHex(provided);
            if (providedBytes == null) { return false; }

            return FixedTimeEquals(providedBytes, _tokenBytes);
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

        private static int FindFreePort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            return port;
        }

        /// <summary>Length-independent constant-time comparison (not built in on net472).</summary>
        private static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) { return false; }
            int diff = 0;
            for (int i = 0; i < a.Length; i++) { diff |= a[i] ^ b[i]; }
            return diff == 0;
        }

        private static string ToHex(byte[] bytes)
        {
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) { sb.Append(b.ToString("x2")); }
            return sb.ToString();
        }

        private static byte[]? FromHex(string hex)
        {
            if (hex.Length == 0 || (hex.Length % 2) != 0) { return null; }
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
            {
                if (!byte.TryParse(hex.Substring(i * 2, 2),
                        System.Globalization.NumberStyles.HexNumber, null, out bytes[i]))
                {
                    return null;
                }
            }
            return bytes;
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
}
