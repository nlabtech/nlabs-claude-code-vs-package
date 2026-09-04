using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

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
    public void BuildArguments_adds_the_settings_path()
    {
        string args = ClaudeCliSession.BuildArguments(new ClaudeCliOptions { SettingsPath = @"C:\tmp\s.json" });

        Assert.Contains("--settings \"C:\\tmp\\s.json\"", args);
    }

    [Fact]
    public void ComposeStart_runs_a_real_exe_directly()
    {
        var (file, args) = ClaudeCliSession.ComposeStart(@"C:\tools\claude.exe", "-p x");

        Assert.Equal(@"C:\tools\claude.exe", file);
        Assert.Equal("-p x", args);
    }

    [Fact]
    public void ComposeStart_runs_a_cmd_shim_through_cmd_exe()
    {
        var (file, args) = ClaudeCliSession.ComposeStart(@"C:\npm\claude.cmd", "-p x");

        Assert.Equal("cmd.exe", file);
        // The whole command is wrapped in one extra pair of quotes so cmd /s strips only the outer
        // pair, leaving the executable's quotes intact - otherwise claude launches mangled and dies.
        Assert.Equal("/d /s /c \"\"C:\\npm\\claude.cmd\" -p x\"", args);
    }

    [Fact]
    public void ComposeStart_keeps_inner_quotes_intact_after_the_outer_strip()
    {
        // A --settings path with its own quotes must survive cmd's first/last-quote strip.
        var (_, args) = ClaudeCliSession.ComposeStart(@"C:\npm\claude.cmd", "-p --settings \"C:\\t\\s.json\"");

        string afterOuterStrip = StripFirstAndLastQuote(args.Substring("/d /s /c ".Length));
        Assert.Equal("\"C:\\npm\\claude.cmd\" -p --settings \"C:\\t\\s.json\"", afterOuterStrip);
    }

    [Fact]
    public void ComposeStart_falls_back_to_cmd_when_unresolved()
    {
        var (file, args) = ClaudeCliSession.ComposeStart(null, "-p x");

        Assert.Equal("cmd.exe", file);
        Assert.Contains("claude -p x", args);
    }

    // Mirrors cmd.exe /s: remove exactly the first and last quote of the /c string.
    private static string StripFirstAndLastQuote(string s)
    {
        int first = s.IndexOf('"');
        int last = s.LastIndexOf('"');
        if (first < 0 || last <= first) return s;
        return s.Remove(last, 1).Remove(first, 1);
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
