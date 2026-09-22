namespace Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

/// <summary>
/// Where the standard-MCP endpoint is and the bearer token to reach it. The panel reads this to
/// write its own session's <c>--mcp-config</c>; a plain immutable pair, in-process only, never logged.
/// </summary>
public sealed class McpEndpoint
{
    public McpEndpoint(string url, string token)
    {
        Url = url;
        Token = token;
    }

    public string Url { get; }
    public string Token { get; }
}
