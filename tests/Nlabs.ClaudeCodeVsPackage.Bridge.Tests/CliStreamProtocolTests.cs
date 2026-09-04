using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class CliStreamProtocolTests
{
    [Fact]
    public void UserMessage_is_a_stream_json_user_turn()
    {
        var m = JObject.Parse(CliStreamProtocol.UserMessage("merhaba"));

        Assert.Equal("user", (string?)m["type"]);
        Assert.Equal("user", (string?)m["message"]!["role"]);
        var block = (JArray)m["message"]!["content"]!;
        Assert.Equal("text", (string?)block[0]!["type"]);
        Assert.Equal("merhaba", (string?)block[0]!["text"]);
    }

    [Fact]
    public void Parse_system_init_reads_model_and_session()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"s-1\",\"model\":\"claude-opus-4-8\"}");

        Assert.Equal(CliEventKind.SystemInit, e.Kind);
        Assert.Equal("s-1", e.SessionId);
        Assert.Equal("claude-opus-4-8", e.Model);
    }

    [Fact]
    public void Parse_assistant_joins_the_text_blocks()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"Hel\"},{\"type\":\"text\",\"text\":\"lo\"}]}}");

        Assert.Equal(CliEventKind.Assistant, e.Kind);
        Assert.Equal("Hello", e.Text);
    }

    [Fact]
    public void Parse_assistant_extracts_a_todo_list_from_a_TodoWrite_call()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[{\"content\":\"read tests\",\"status\":\"completed\"},{\"content\":\"fix bug\",\"status\":\"in_progress\"}]}}]}}");

        Assert.NotNull(e.Todos);
        Assert.Equal(2, e.Todos!.Count);
        Assert.Equal("read tests", e.Todos[0].Content);
        Assert.Equal("completed", e.Todos[0].Status);
        Assert.Equal("in_progress", e.Todos[1].Status);
    }

    [Fact]
    public void Parse_assistant_without_todos_leaves_the_list_null()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");

        Assert.Null(e.Todos);
    }

    [Fact]
    public void Parse_stream_event_extracts_the_delta_text()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"xyz\"}}}");

        Assert.Equal(CliEventKind.StreamDelta, e.Kind);
        Assert.Equal("xyz", e.Text);
    }

    [Fact]
    public void Parse_result_reads_cost_and_success()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":0.0123,\"is_error\":false}");

        Assert.Equal(CliEventKind.Result, e.Kind);
        Assert.Equal(0.0123, e.TotalCostUsd!.Value, 4);
        Assert.False(e.IsError);
    }

    [Fact]
    public void Parse_result_without_success_is_flagged_as_error()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"result\",\"subtype\":\"error_max_turns\",\"total_cost_usd\":0.5}");

        Assert.Equal(CliEventKind.Result, e.Kind);
        Assert.True(e.IsError);
    }

    [Fact]
    public void Parse_garbage_is_unknown_and_does_not_throw()
    {
        var e = CliStreamProtocol.Parse("this is not json");

        Assert.Equal(CliEventKind.Unknown, e.Kind);
        Assert.Equal("this is not json", e.Raw);
    }
}
