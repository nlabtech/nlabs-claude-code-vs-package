using Newtonsoft.Json.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class CliStreamProtocolTests
{
    [Theory]
    [InlineData("Task")]
    [InlineData("Agent")]
    public void A_delegation_call_carries_the_subagent_it_named(string tool)
    {
        // The CLI has shipped this tool under both names. Missing one loses the subagent's name, and
        // every line it produces then shows up as an anonymous "subagent" in the panel.
        string line =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t1\"," +
            "\"name\":\"" + tool + "\",\"input\":{\"subagent_type\":\"reviewer\",\"description\":\"look\"}}]}}";

        CliEvent? e = CliStreamProtocol.Parse(line);

        Assert.NotNull(e!.Tools);
        ToolCall call = Assert.Single(e.Tools!);
        Assert.Equal("t1", call.Id);
        Assert.Equal("reviewer", call.Subagent);
    }

    [Fact]
    public void An_ordinary_tool_names_no_subagent()
    {
        string line =
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"id\":\"t2\"," +
            "\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}";

        CliEvent? e = CliStreamProtocol.Parse(line);

        Assert.Null(Assert.Single(e!.Tools!).Subagent);
    }

    [Theory]
    [InlineData("Task", true)]
    [InlineData("Agent", true)]
    [InlineData("Read", false)]
    [InlineData(null, false)]
    public void Delegation_is_recognised_by_either_name(string? tool, bool expected)
    {
        Assert.Equal(expected, CliStreamProtocol.IsDelegation(tool));
    }

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
    public void UserMessage_carries_attached_images_before_the_text()
    {
        var images = new[]
        {
            new ImageAttachment { MediaType = "image/png", Base64Data = "QUJD" },
        };
        var m = JObject.Parse(CliStreamProtocol.UserMessage("bak", images));

        var content = (JArray)m["message"]!["content"]!;
        Assert.Equal("image", (string?)content[0]!["type"]);
        Assert.Equal("base64", (string?)content[0]!["source"]!["type"]);
        Assert.Equal("image/png", (string?)content[0]!["source"]!["media_type"]);
        Assert.Equal("QUJD", (string?)content[0]!["source"]!["data"]);
        Assert.Equal("text", (string?)content[1]!["type"]);
        Assert.Equal("bak", (string?)content[1]!["text"]);
    }

    [Fact]
    public void UserMessage_skips_images_with_no_data()
    {
        var images = new[] { new ImageAttachment { Base64Data = "" } };
        var m = JObject.Parse(CliStreamProtocol.UserMessage("hi", images));

        var content = (JArray)m["message"]!["content"]!;
        Assert.Single(content);
        Assert.Equal("text", (string?)content[0]!["type"]);
    }

    [Fact]
    public void Assistant_surfaces_tool_calls_with_a_summary()
    {
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[" +
                   "{\"type\":\"tool_use\",\"name\":\"Bash\",\"input\":{\"command\":\"git status\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"Program.cs\"}}," +
                   "{\"type\":\"tool_use\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[]}}]}}";

        var e = CliStreamProtocol.Parse(line);

        Assert.NotNull(e.Tools);
        Assert.Equal(2, e.Tools!.Count); // TodoWrite is skipped - it shows as the task strip
        Assert.Equal("Bash", e.Tools[0].Name);
        Assert.Equal("git status", e.Tools[0].Summary);
        Assert.Equal("Read", e.Tools[1].Name);
        Assert.Equal("Program.cs", e.Tools[1].Summary);
    }

    [Fact]
    public void A_result_reports_what_the_turn_consumed()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"result\",\"subtype\":\"success\",\"duration_ms\":8300,\"total_cost_usd\":0.02," +
            "\"usage\":{\"input_tokens\":12,\"output_tokens\":340,\"cache_read_input_tokens\":36578," +
            "\"cache_creation_input_tokens\":900,\"output_tokens_details\":{\"thinking_tokens\":153}}}");

        Assert.Equal(12, e.Usage!.InputTokens);
        Assert.Equal(340, e.Usage.OutputTokens);
        Assert.Equal(36578, e.Usage.CacheReadTokens);
        Assert.Equal(900, e.Usage.CacheWriteTokens);
        Assert.Equal(153, e.Usage.ThinkingTokens);
        Assert.Equal(8300, e.Usage.DurationMs);
        // Cached reads are traffic, not spend, and must not be folded into what was billed.
        Assert.Equal(12 + 340 + 900, e.Usage.BilledTokens);
    }

    [Fact]
    public void A_result_without_a_usage_block_reports_none()
    {
        Assert.Null(CliStreamProtocol.Parse("{\"type\":\"result\",\"subtype\":\"success\"}").Usage);
    }

    [Fact]
    public void Reasoning_spend_is_reported_while_it_is_still_happening()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"system\",\"subtype\":\"thinking_tokens\",\"estimated_tokens\":153,\"estimated_tokens_delta\":3}");

        Assert.Equal(153, e.ThinkingTokens);
    }

    [Fact]
    public void Reasoning_arrives_on_its_own_channel_and_never_as_reply_text()
    {
        var delta = CliStreamProtocol.Parse(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\"," +
            "\"delta\":{\"type\":\"thinking_delta\",\"thinking\":\"90 is not prime\"}}}");

        Assert.Equal("90 is not prime", delta.Thinking);
        Assert.Null(delta.Text); // it must not leak into the answer
    }

    [Fact]
    public void An_assistant_message_reports_its_reasoning_apart_from_its_text()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"message\":{\"content\":[" +
            "{\"type\":\"thinking\",\"thinking\":\"working it out\",\"signature\":\"x\"}," +
            "{\"type\":\"text\",\"text\":\"97\"}]}}");

        Assert.Equal("working it out", e.Thinking);
        Assert.Equal("97", e.Text);
    }

    [Fact]
    public void A_message_without_reasoning_reports_none()
    {
        var e = CliStreamProtocol.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");
        Assert.Null(e.Thinking);
    }

    [Fact]
    public void A_Task_call_names_the_subagent_it_delegates_to()
    {
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[" +
                   "{\"type\":\"tool_use\",\"id\":\"toolu_9\",\"name\":\"Task\"," +
                   "\"input\":{\"subagent_type\":\"memory-curator\",\"description\":\"save it\"}}]}}";

        var e = CliStreamProtocol.Parse(line);

        Assert.Equal("toolu_9", e.Tools![0].Id);
        Assert.Equal("memory-curator", e.Tools[0].Subagent);
    }

    [Fact]
    public void An_ordinary_tool_call_delegates_to_no_one()
    {
        var line = "{\"type\":\"assistant\",\"message\":{\"content\":[" +
                   "{\"type\":\"tool_use\",\"id\":\"toolu_1\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.cs\"}}]}}";

        Assert.Null(CliStreamProtocol.Parse(line).Tools![0].Subagent);
    }

    [Fact]
    public void Lines_from_a_subagent_carry_the_call_they_belong_to()
    {
        var assistant = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"parent_tool_use_id\":\"toolu_9\"," +
            "\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"done\"}]}}");
        var delta = CliStreamProtocol.Parse(
            "{\"type\":\"stream_event\",\"parent_tool_use_id\":\"toolu_9\",\"event\":{}}");
        var main = CliStreamProtocol.Parse(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");

        Assert.Equal("toolu_9", assistant.ParentToolUseId);
        Assert.Equal("toolu_9", delta.ParentToolUseId);
        Assert.Null(main.ParentToolUseId); // the main turn belongs to no call
    }

    [Fact]
    public void Assistant_with_only_text_has_no_tools()
    {
        var e = CliStreamProtocol.Parse("{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}");
        Assert.Null(e.Tools);
    }

    [Fact]
    public void Stream_message_delta_carries_output_tokens()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"stream_event\",\"event\":{\"type\":\"message_delta\",\"usage\":{\"output_tokens\":842}}}");

        Assert.Equal(CliEventKind.StreamDelta, e.Kind);
        Assert.Equal(842, e.OutputTokens);
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

    [Fact]
    public void Parse_rate_limit_event_reads_status_and_windows()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed_warning\"," +
            "\"unifiedWindows\":{\"five_hour\":{\"utilization\":0.02},\"seven_day\":{\"utilization\":0.4}}}}");

        Assert.Equal(CliEventKind.RateLimit, e.Kind);
        Assert.NotNull(e.RateLimit);
        Assert.True(e.RateLimit!.Warning); // allowed_warning is a warning state
        Assert.Equal(2, e.RateLimit.Windows.Count);
        Assert.Equal("seven_day", e.RateLimit.MostFull!.Name); // 0.4 > 0.02
    }

    [Fact]
    public void Parse_rate_limit_event_reads_the_reset_time_from_epoch_seconds()
    {
        var e = CliStreamProtocol.Parse(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"status\":\"allowed\"," +
            "\"unifiedWindows\":{\"five_hour\":{\"utilization\":0.1,\"resetsAt\":1756700000}}}}");

        Assert.False(e.RateLimit!.Warning); // plain allowed is not a warning
        Assert.NotNull(e.RateLimit.Windows[0].ResetsAt);
    }
}
