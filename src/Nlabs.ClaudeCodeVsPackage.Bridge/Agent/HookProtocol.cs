using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>What a PreToolUse hook payload tells us about the tool about to run.</summary>
public sealed class HookRequest
{
    public string ToolName { get; set; } = string.Empty;
    /// <summary>The tool input as pretty JSON, ready to show in the approval card.</summary>
    public string InputPreview { get; set; } = string.Empty;
    /// <summary>The plan text, when the tool is ExitPlanMode - so the panel can show a plan card.</summary>
    public string? Plan { get; set; }
}

/// <summary>
/// The PreToolUse hook wire format. Claude Code runs the hook before a tool, writes the tool call
/// as JSON on the hook's stdin, and reads a decision back on stdout. This turns that request into a
/// <see cref="HookRequest"/> and builds the allow/deny reply. It is pure, so it is unit-tested; the
/// approval server does the I/O.
/// </summary>
public static class HookProtocol
{
    public static HookRequest ParseRequest(string json)
    {
        var req = new HookRequest();
        if (string.IsNullOrWhiteSpace(json)) return req;

        JObject obj;
        try { obj = JObject.Parse(json); }
        catch { return req; }

        req.ToolName = (string?)obj["tool_name"] ?? string.Empty;
        JToken? input = obj["tool_input"];
        req.InputPreview = input == null ? string.Empty : input.ToString(Formatting.Indented);
        // ExitPlanMode carries the proposed plan; surface it so the panel can render a plan card.
        if (req.ToolName == "ExitPlanMode") req.Plan = (string?)input?["plan"];
        return req;
    }

    /// <summary>The stdout JSON that lets the tool run.</summary>
    public static string Allow(string? reason = null) => Decision("allow", reason);

    /// <summary>The stdout JSON that blocks the tool; the reason is fed back to Claude.</summary>
    public static string Deny(string? reason = null) => Decision("deny", reason);

    private static string Decision(string decision, string? reason)
    {
        var specific = new JObject
        {
            ["hookEventName"] = "PreToolUse",
            ["permissionDecision"] = decision,
        };
        if (!string.IsNullOrEmpty(reason)) specific["permissionDecisionReason"] = reason;
        return new JObject { ["hookSpecificOutput"] = specific }.ToString(Formatting.None);
    }
}
