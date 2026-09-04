using System.ComponentModel;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace Nlabs.ClaudeCodeVsPackage.Mcp;

/// <summary>
/// The tools Claude Code sees. Each one forwards to the Visual Studio extension over the
/// local bridge and returns the JSON reply. Descriptions are the control surface: they tell
/// the agent when to reach for each tool.
/// </summary>
[McpServerToolType]
public sealed class BridgeTools
{
    private readonly BridgeClient _bridge;

    public BridgeTools(BridgeClient bridge) => _bridge = bridge;

    [McpServerTool(Name = "get_diagnostics")]
    [Description("Get the current Error List diagnostics (errors and warnings) from Visual Studio. Call this before proposing changes to see what is already broken.")]
    public async Task<string> GetDiagnostics(CancellationToken ct)
        => await ForwardAsync("get_diagnostics", null, ct);

    [McpServerTool(Name = "get_active_document")]
    [Description("Get the path of the active document in Visual Studio and the developer's current selection.")]
    public async Task<string> GetActiveDocument(CancellationToken ct)
        => await ForwardAsync("get_active_document", null, ct);

    [McpServerTool(Name = "get_debug_state")]
    [Description("Get the debugger state: whether execution is stopped in break mode and, if so, the current function.")]
    public async Task<string> GetDebugState(CancellationToken ct)
        => await ForwardAsync("get_debug_state", null, ct);

    [McpServerTool(Name = "propose_diff")]
    [Description("Propose new content for a file. Visual Studio opens it as a diff (current vs proposed); the developer stays the only one who applies the change. Never writes the file directly.")]
    public async Task<string> ProposeDiff(
        [Description("Absolute path of the file to change.")] string file,
        [Description("The full proposed content of the file.")] string proposedText,
        CancellationToken ct)
    {
        var payload = new JsonObject { ["file"] = file, ["proposedText"] = proposedText };
        return await ForwardAsync("propose_diff", payload, ct);
    }

    private async Task<string> ForwardAsync(string type, JsonObject? payload, CancellationToken ct)
    {
        JsonNode? reply = await _bridge.SendAsync(type, payload, ct);
        return reply?.ToJsonString() ?? "{}";
    }
}
