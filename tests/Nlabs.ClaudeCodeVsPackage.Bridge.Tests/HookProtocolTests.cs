using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class HookProtocolTests
{
    [Fact]
    public void ParseRequest_reads_the_tool_name_and_input()
    {
        var r = HookProtocol.ParseRequest(
            "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"dotnet test\"}}");

        Assert.Equal("Bash", r.ToolName);
        Assert.Contains("dotnet test", r.InputPreview);
    }

    [Fact]
    public void ParseRequest_tolerates_garbage()
    {
        var r = HookProtocol.ParseRequest("not json");

        Assert.Equal(string.Empty, r.ToolName);
    }

    [Fact]
    public void Allow_and_Deny_use_the_hook_specific_shape()
    {
        var allow = JObject.Parse(HookProtocol.Allow());
        Assert.Equal("PreToolUse", (string?)allow["hookSpecificOutput"]!["hookEventName"]);
        Assert.Equal("allow", (string?)allow["hookSpecificOutput"]!["permissionDecision"]);

        var deny = JObject.Parse(HookProtocol.Deny("blocked by you"));
        Assert.Equal("deny", (string?)deny["hookSpecificOutput"]!["permissionDecision"]);
        Assert.Equal("blocked by you", (string?)deny["hookSpecificOutput"]!["permissionDecisionReason"]);
    }
}
