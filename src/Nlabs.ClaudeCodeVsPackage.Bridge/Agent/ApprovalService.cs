using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>Carries one pending tool approval to the panel.</summary>
public sealed class ApprovalRequestedEventArgs : EventArgs
{
    public string Id { get; }
    public HookRequest Request { get; }
    public ApprovalRequestedEventArgs(string id, HookRequest request)
    {
        Id = id;
        Request = request;
    }
}

/// <summary>
/// A loopback endpoint the PreToolUse hook calls to ask the developer before a tool runs. The hook
/// script POSTs the tool call here and blocks; this raises <see cref="Requested"/> so the panel can
/// show an approval card, waits for <see cref="Resolve"/>, and answers the hook with an allow/deny
/// decision. Same posture as the bridge: 127.0.0.1 only, a per-run token, nothing logged.
/// </summary>
public sealed class ApprovalService : IDisposable
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    // The one path the hook script posts to, and the only verb it uses. Anything else is not the
    // hook, whatever token it carries.
    private const string HookPath = "/permission";

    // A tool call is a handful of kilobytes. The ceiling is the bridge's own, for the same reason:
    // a body is read into memory before anything looks at it.
    private const int MaxBody = 1024 * 1024;

    // A turn that edits a dozen files asks a dozen times; a runaway loop asks forever, and each one
    // holds a slot for five minutes. Past this the answer is deny - the safe answer, and an honest one.
    private const int MaxPending = 64;

    private readonly HttpListener _listener = new HttpListener();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<(string decision, string reason)>> _pending
        = new ConcurrentDictionary<string, TaskCompletionSource<(string, string)>>();

    public string Token { get; } = Guid.NewGuid().ToString("n");
    public int Port { get; private set; }

    /// <summary>Raised when the hook asks about a tool; the panel answers with <see cref="Resolve"/>.</summary>
    public event EventHandler<ApprovalRequestedEventArgs>? Requested;

    public void Start()
    {
        Port = FindFreePort();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _ = AcceptLoopAsync();
    }

    /// <summary>Answers a pending request; call from the panel after the developer decides.</summary>
    public void Resolve(string id, bool allow, string? reason = null)
    {
        if (_pending.TryRemove(id, out var tcs))
        {
            tcs.TrySetResult((allow ? "allow" : "deny", reason ?? string.Empty));
        }
    }

    /// <summary>How many calls are still waiting for an answer, the one being shown included.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Answers everything currently waiting the same way. A turn that edits a dozen files asks a
    /// dozen times, and clicking through them one at a time is how a developer stops reading them.
    /// </summary>
    public void ResolveAll(bool allow, string? reason = null)
    {
        foreach (string id in _pending.Keys)
        {
            Resolve(id, allow, reason);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync().ConfigureAwait(false); }
            catch { break; }
            _ = HandleAsync(context);
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            HttpListenerRequest request = context.Request;
            if (request.Headers["Origin"] != null) { Close(context, 403); return; }
            if (!request.IsLocal) { Close(context, 403); return; }
            if (!FixedTimeEquals(request.Headers["x-nlabs-approval"], Token)) { Close(context, 401); return; }

            // Only after the token, so an unauthenticated caller learns nothing about what is here.
            if (!string.Equals(request.HttpMethod, "POST", StringComparison.Ordinal)) { Close(context, 405); return; }
            if (!string.Equals(request.Url?.AbsolutePath, HookPath, StringComparison.Ordinal)) { Close(context, 404); return; }
            if (request.ContentLength64 > MaxBody) { Close(context, 413); return; }

            string? body = await ReadBounded(request).ConfigureAwait(false);
            if (body == null) { Close(context, 413); return; }

            if (_pending.Count >= MaxPending)
            {
                WriteJson(context, HookProtocol.Deny("Too many approvals are already waiting."));
                return;
            }

            HookRequest hook = HookProtocol.ParseRequest(body);
            string id = Guid.NewGuid().ToString("n");
            var tcs = new TaskCompletionSource<(string, string)>();
            _pending[id] = tcs;
            Requested?.Invoke(this, new ApprovalRequestedEventArgs(id, hook));

            (string decision, string reason) = await WaitOrTimeout(id, tcs).ConfigureAwait(false);
            string reply = decision == "allow" ? HookProtocol.Allow(reason) : HookProtocol.Deny(reason);
            WriteJson(context, reply);
        }
        catch
        {
            Close(context, 500);
        }
    }

    /// <summary>
    /// The body, or null when it runs past the ceiling. Chunked encoding reports no length up front,
    /// so the count is kept while reading rather than trusted from the header.
    /// </summary>
    private static async Task<string?> ReadBounded(HttpListenerRequest request)
    {
        var buffer = new byte[8192];
        using (var sink = new MemoryStream())
        {
            int read;
            while ((read = await request.InputStream.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
            {
                if (sink.Length + read > MaxBody) return null;
                sink.Write(buffer, 0, read);
            }
            return (request.ContentEncoding ?? System.Text.Encoding.UTF8).GetString(sink.ToArray());
        }
    }

    private async Task<(string, string)> WaitOrTimeout(string id, TaskCompletionSource<(string, string)> tcs)
    {
        Task finished = await Task.WhenAny(tcs.Task, Task.Delay(Timeout)).ConfigureAwait(false);
        if (finished == tcs.Task) return tcs.Task.Result;

        _pending.TryRemove(id, out _);
        return ("deny", "No response from the panel.");
    }

    private static void WriteJson(HttpListenerContext context, string json)
    {
        try
        {
            byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json";
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
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

    private static bool FixedTimeEquals(string? a, string b)
    {
        if (string.IsNullOrEmpty(a)) return false;
        int diff = a!.Length ^ b.Length;
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }

    public void Dispose()
    {
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        foreach (var kv in _pending) kv.Value.TrySetResult(("deny", "Panel closed."));
        _pending.Clear();
    }
}
