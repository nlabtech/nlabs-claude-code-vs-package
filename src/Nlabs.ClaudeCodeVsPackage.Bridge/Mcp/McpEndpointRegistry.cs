namespace Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;

/// <summary>
/// The one place the panel looks to find the running HTTP MCP endpoint. The package publishes it
/// when the bridge starts and clears it when the bridge stops; the panel reads it while composing
/// its <c>claude</c> session. Set and read on the UI thread, in-process only - the token never
/// leaves this process except into the panel's own temp mcp-config file.
/// </summary>
public static class McpEndpointRegistry
{
    /// <summary>The live endpoint, or null when the bridge is not running.</summary>
    public static McpEndpoint? Current { get; set; }
}
