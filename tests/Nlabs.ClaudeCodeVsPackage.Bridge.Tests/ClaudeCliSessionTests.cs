using System.Collections.Generic;
using System.IO;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests
{
    public class ClaudeCliSessionTests
    {
        [Fact]
        public void BuildArguments_has_the_stream_json_base()
        {
            string args = ClaudeCliSession.BuildArguments(new ClaudeCliOptions { IncludePartialMessages = false });

            Assert.Contains("-p", args);
            Assert.Contains("--input-format stream-json", args);
            Assert.Contains("--output-format stream-json", args);
            Assert.DoesNotContain("--include-partial-messages", args);
        }

        [Fact]
        public void BuildArguments_adds_model_mode_and_resume()
        {
            string args = ClaudeCliSession.BuildArguments(new ClaudeCliOptions
            {
                Model = "opus",
                PermissionMode = "acceptEdits",
                Resume = "sess-9",
                IncludePartialMessages = true,
            });

            Assert.Contains("--include-partial-messages", args);
            Assert.Contains("--model opus", args);
            Assert.Contains("--permission-mode acceptEdits", args);
            Assert.Contains("--resume sess-9", args);
        }

        [Fact]
        public void BuildArguments_quotes_the_appended_system_prompt()
        {
            string args = ClaudeCliSession.BuildArguments(new ClaudeCliOptions
            {
                AppendSystemPrompt = "be terse \"ok\"",
            });

            Assert.Contains("--append-system-prompt \"be terse \\\"ok\\\"\"", args);
        }

        [Fact]
        public void Pump_raises_a_parsed_event_for_each_line()
        {
            var session = new ClaudeCliSession();
            var kinds = new List<CliEventKind>();
            session.Event += (_, e) => kinds.Add(e.Kind);

            string stream = string.Join("\n", new[]
            {
                "{\"type\":\"system\",\"subtype\":\"init\",\"model\":\"claude-opus-4-8\"}",
                "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hi\"}]}}",
                "not-json",
                "{\"type\":\"result\",\"subtype\":\"success\",\"total_cost_usd\":0.01}",
            });

            session.Pump(new StringReader(stream));

            Assert.Equal(
                new[] { CliEventKind.SystemInit, CliEventKind.Assistant, CliEventKind.Unknown, CliEventKind.Result },
                kinds);
        }
    }
}
