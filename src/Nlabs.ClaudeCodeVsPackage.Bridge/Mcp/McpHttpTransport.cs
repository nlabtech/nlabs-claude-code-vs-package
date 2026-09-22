using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

/// <summary>One inbound HTTP request, reduced to what the transport decides on.</summary>
public sealed class McpHttpRequest
{
    public McpHttpRequest(string method, string path, string? authorization, string? origin, bool isLocal, string? body, long contentLength)
    {
        Method = method;
        Path = path;
        Authorization = authorization;
        Origin = origin;
        IsLocal = isLocal;
        Body = body;
        ContentLength = contentLength;
    }

    public string Method { get; }
    public string Path { get; }
    public string? Authorization { get; }
    public string? Origin { get; }
    public bool IsLocal { get; }
    public string? Body { get; }
    public long ContentLength { get; }
}

/// <summary>What to write back: a status, and for 200 a content type and body.</summary>
public readonly struct McpHttpResponse
{
    public McpHttpResponse(int status, string? contentType, string body)
    {
        Status = status;
        ContentType = contentType;
        Body = body;
    }

    public int Status { get; }
    public string? ContentType { get; }
    public string Body { get; }

    /// <summary>A GET that should be upgraded to a Server-Sent-Events stream by the socket layer.</summary>
    public bool IsEventStream => Status == 200 && ContentType == "text/event-stream";
}

/// <summary>
/// The streamable-HTTP MCP transport, kept free of any socket so every rule is unit-tested.
///
/// The native <c>/ide</c> path is a fixed vocabulary: Claude Code surfaces only the IDE tools it
/// already knows (getDiagnostics, and openDiff through the edit flow), so the Roslyn, build and
/// debugger tools this extension adds never reach the model there. A standard MCP server has no
/// such ceiling - every entry in <c>tools/list</c> becomes callable - which is why the same
/// <see cref="McpProtocol"/> is offered a second way, over HTTP, for a client that connected with
/// <c>claude mcp add</c> or the panel's own <c>--mcp-config</c>.
///
/// Same posture as the rest of the bridge: loopback only, a per-session bearer token compared in
/// constant time, no <c>Origin</c> (that is a browser), a one-megabyte ceiling, nothing logged.
/// A POST carries one JSON-RPC message and gets its reply; a notification is answered 202 with no
/// body. A GET is the server-to-client notification stream, which the socket layer turns into SSE.
/// </summary>
public sealed class McpHttpTransport
{
    public const string Path = "/mcp";
    private const int MaxBodyBytes = 1 * 1024 * 1024;

    private readonly McpProtocol _protocol;
    private readonly string _token;

    public McpHttpTransport(McpProtocol protocol, string token)
    {
        _protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        _token = token ?? throw new ArgumentNullException(nameof(token));
    }

    public async Task<McpHttpResponse> HandleAsync(McpHttpRequest request, CancellationToken cancellationToken)
    {
        // Order matters: a browser and a non-loopback caller are turned away before the token is
        // even weighed, so an outsider learns nothing about whether the token was close.
        if (request.Origin != null) return Text(403);
        if (!request.IsLocal) return Text(403);
        if (!TokenMatches(request.Authorization)) return Text(401);

        if (!string.Equals(request.Path, Path, StringComparison.Ordinal)) return Text(404);

        if (string.Equals(request.Method, "GET", StringComparison.Ordinal))
        {
            // The socket layer holds this open and pushes tools/list_changed as the solution's
            // error count moves; here it is only granted.
            return new McpHttpResponse(200, "text/event-stream", string.Empty);
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal)) return Text(405);
        if (request.ContentLength > MaxBodyBytes) return Text(413);

        string body = request.Body ?? string.Empty;
        if (body.Length == 0) return Text(400);

        string? reply = await _protocol.HandleAsync(body, cancellationToken).ConfigureAwait(false);

        // A notification takes no reply; the spec's answer is 202 with an empty body.
        if (reply == null) return new McpHttpResponse(202, null, string.Empty);

        return new McpHttpResponse(200, "application/json", reply);
    }

    private bool TokenMatches(string? authorization)
    {
        if (string.IsNullOrEmpty(authorization)) return false;

        const string prefix = "Bearer ";
        if (!authorization!.StartsWith(prefix, StringComparison.Ordinal)) return false;

        return FixedTimeEquals(authorization.Substring(prefix.Length), _token);
    }

    private static McpHttpResponse Text(int status) => new McpHttpResponse(status, null, string.Empty);

    private static bool FixedTimeEquals(string a, string b)
    {
        int diff = a.Length ^ b.Length;
        int shared = Math.Min(a.Length, b.Length);
        for (int i = 0; i < shared; i++) diff |= a[i] ^ b[i];
        return diff == 0;
    }
}
