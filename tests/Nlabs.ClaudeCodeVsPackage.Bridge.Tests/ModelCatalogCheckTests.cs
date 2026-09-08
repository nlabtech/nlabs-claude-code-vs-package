using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class ModelCatalogCheckTests
{
    // The shape the CLI actually prints for --model.
    private const string Help =
        "  --mcp-config <configs...>             Load MCP servers\n" +
        "  --model <model>                       Model for the current session. Provide\n" +
        "                                        an alias for the latest model (e.g.\n" +
        "                                        'fable', 'opus', or 'sonnet') or a\n" +
        "                                        model's full name (e.g.\n" +
        "                                        'claude-fable-5').\n" +
        "  --settings <file-or-json>             Path to a settings JSON\n";

    [Fact]
    public void AliasesInHelp_reads_the_tier_aliases_and_skips_full_model_ids()
    {
        var aliases = ModelCatalogCheck.AliasesInHelp(Help);

        Assert.Contains("fable", aliases);
        Assert.Contains("opus", aliases);
        Assert.Contains("sonnet", aliases);
        Assert.DoesNotContain("claude-fable-5", aliases);
    }

    [Fact]
    public void Compare_reports_an_alias_the_panel_does_not_offer()
    {
        var diff = ModelCatalogCheck.Compare(Help, new[] { "opus", "sonnet" });

        Assert.True(diff.IsStale);
        Assert.Contains("fable", diff.NewInCli);
    }

    [Fact]
    public void Compare_is_quiet_when_the_panel_is_up_to_date()
    {
        var diff = ModelCatalogCheck.Compare(Help, new[] { "opus", "sonnet", "fable" });

        Assert.False(diff.IsStale);
        Assert.Empty(diff.NewInCli);
    }

    [Fact]
    public void Compare_reports_an_alias_the_cli_no_longer_mentions()
    {
        var diff = ModelCatalogCheck.Compare(Help, new[] { "opus", "sonnet", "fable", "haiku" });

        Assert.Contains("haiku", diff.MissingFromCli);
    }

    [Fact]
    public void Compare_claims_nothing_when_the_help_is_empty_or_unreadable()
    {
        Assert.False(ModelCatalogCheck.Compare("", new[] { "opus" }).IsStale);
        Assert.False(ModelCatalogCheck.Compare(null, new[] { "opus" }).IsStale);
        Assert.False(ModelCatalogCheck.Compare("no options here", new[] { "opus" }).IsStale);
    }
}
