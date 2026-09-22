using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

/// <summary>
/// The socket around <see cref="McpHttpTransport"/>: a loopback HTTP endpoint that serves the same
/// tools over a standard MCP connection, so a client added with <c>claude mcp add</c> or the
/// panel's <c>--mcp-config</c> gets every tool, not the one the native <c>/ide</c> path allows.
///
/// The decisions live in the transport, which is pure and fully tested; this only moves bytes. A
/// POST carries one JSON-RPC message and gets its reply. A GET is held open as a Server-Sent-Events
/// stream, and <see cref="PushToolsListChanged"/> writes the list-changed notification to every one
/// that is open - the HTTP equal of the WebSocket path's push. Same posture as the bridge: 127.0.0.1
/// only, a bearer token, nothing logged.
/// </summary>
public sealed class McpHttpServer : IDisposable
{
    private const int MaxBodyBytes = 1 * 1024 * 1024;

    private readonly HttpListener _listener = new HttpListener();
    private readonly McpProtocol _protocol;
    private readonly McpHttpTransport _transport;
    private readonly object _streamsGate = new object();
    private readonly HashSet<Stream> _streams = new HashSet<Stream>();
    private CancellationTokenSource? _cts;

    /// <summary>The bearer token a client must present; written to the panel's mcp-config, never logged.</summary>
    public string Token { get; } = Guid.NewGuid().ToString("n");

    /// <summary>The port the server listens on, set after <see cref="Start"/>.</summary>
    public int Port { get; private set; }

    public McpHttpServer(McpProtocol protocol)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _transport = new McpHttpTransport(protocol, Token);
    }

    /// <summary>The URL a client connects to, once <see cref="Start"/> has run.</summary>
    public string Url => $"http://127.0.0.1:{Port}{McpHttpTransport.Path}";

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
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = HandleAsync(context, ct);
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken ct)
    {
        try
        {
            HttpListenerRequest req = context.Request;

            string? body = null;
            if (string.Equals(req.HttpMethod, "POST", StringComparison.Ordinal))
            {
                body = await ReadBounded(req).ConfigureAwait(false);
                if (body == null) { Close(context, 413); return; }
            }

            var reduced = new McpHttpRequest(
                req.HttpMethod,
                req.Url?.AbsolutePath ?? string.Empty,
                req.Headers["Authorization"],
                req.Headers["Origin"],
                req.IsLocal,
                body,
                req.ContentLength64);

            McpHttpResponse reply = await _transport.HandleAsync(reduced, ct).ConfigureAwait(false);

            if (reply.IsEventStream)
            {
                await HoldOpenAsStream(context, ct).ConfigureAwait(false);
                return;
            }

            WriteResponse(context, reply);
        }
        catch
        {
            Close(context, 500);
        }
    }

    /// <summary>Sends tools/list_changed to every open stream; a stream that has gone is dropped.</summary>
    public void PushToolsListChanged()
    {
        byte[] frame = Encoding.UTF8.GetBytes("event: message\ndata: " + McpProtocol.ToolsListChanged() + "\n\n");

        List<Stream> targets;
        lock (_streamsGate) { targets = new List<Stream>(_streams); }

        foreach (Stream s in targets)
        {
            try { s.Write(frame, 0, frame.Length); s.Flush(); }
            catch { lock (_streamsGate) { _streams.Remove(s); } }
        }
    }

    private async Task HoldOpenAsStream(HttpListenerContext context, CancellationToken ct)
    {
        HttpListenerResponse response = context.Response;
        response.StatusCode = 200;
        response.ContentType = "text/event-stream";
        response.Headers["Cache-Control"] = "no-cache";
        response.SendChunked = true;

        Stream output = response.OutputStream;
        lock (_streamsGate) { _streams.Add(output); }
        try
        {
            // A first comment opens the stream so the client knows it is live; then wait, letting
            // PushToolsListChanged do the writing, until the client goes away or the server stops.
            byte[] hello = Encoding.UTF8.GetBytes(": open\n\n");
            output.Write(hello, 0, hello.Length);
            output.Flush();

            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                try { output.Write(new byte[] { 0x3a, 0x0a, 0x0a }, 0, 3); output.Flush(); } // ":\n\n" keep-alive
                catch { break; }
            }
        }
        catch { /* client gone or cancelled */ }
        finally
        {
            lock (_streamsGate) { _streams.Remove(output); }
            Close(context, 200);
        }
    }

    private static async Task<string?> ReadBounded(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxBodyBytes) return null;

        var buffer = new byte[8192];
        using (var sink = new MemoryStream())
        {
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (sink.Length + read > MaxBodyBytes) return null;
                sink.Write(buffer, 0, read);
            }
            return (request.ContentEncoding ?? Encoding.UTF8).GetString(sink.ToArray());
        }
    }

    private static void WriteResponse(HttpListenerContext context, McpHttpResponse reply)
    {
        try
        {
            context.Response.StatusCode = reply.Status;
            if (reply.Body.Length > 0 && reply.ContentType != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(reply.Body);
                context.Response.ContentType = reply.ContentType;
                context.Response.OutputStream.Write(bytes, 0, bytes.Length);
            }
            context.Response.Close();
        }
        catch { /* client gone */ }
    }

    private static void Close(HttpListenerContext context, int status)
    {
        try { context.Response.StatusCode = status; context.Response.Close(); }
        catch { }
    }

    private static int FindFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        lock (_streamsGate)
        {
            foreach (Stream s in _streams)
            {
                try { s.Close(); } catch { }
            }
            _streams.Clear();
        }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
    }
}
