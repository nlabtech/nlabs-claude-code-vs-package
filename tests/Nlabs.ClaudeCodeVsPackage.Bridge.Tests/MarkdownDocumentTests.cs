using System.Collections.Generic;
using System.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class MarkdownDocumentTests
{
    [Fact]
    public void A_pipe_table_becomes_rows_of_cells()
    {
        var blocks = MarkdownDocument.Parse(
            "| Tool | Risk |\n|------|------|\n| Read | Low |\n| Bash | High |");

        MarkdownBlock table = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Table, table.Kind);
        Assert.Equal(3, table.Rows.Count);
        Assert.Equal(new[] { "Tool", "Risk" }, table.Rows[0]);
        Assert.Equal(new[] { "Bash", "High" }, table.Rows[2]);
    }

    [Fact]
    public void Alignment_colons_are_accepted_in_the_separator()
    {
        var blocks = MarkdownDocument.Parse("| a | b |\n|:--|--:|\n| 1 | 2 |");

        Assert.Equal(MarkdownBlockKind.Table, Assert.Single(blocks).Kind);
    }

    [Fact]
    public void A_line_with_a_pipe_but_no_separator_stays_a_paragraph()
    {
        // Shell commands with a pipe are far more common in these replies than tables are.
        var blocks = MarkdownDocument.Parse("run `git log | head -5` to see them");

        Assert.Equal(MarkdownBlockKind.Paragraph, Assert.Single(blocks).Kind);
    }

    [Theory]
    [InlineData("---")]
    [InlineData("***")]
    [InlineData("___")]
    [InlineData("- - -")]
    public void A_horizontal_rule_is_its_own_block(string line)
    {
        Assert.Equal(MarkdownBlockKind.Rule, Assert.Single(MarkdownDocument.Parse(line)).Kind);
    }

    [Fact]
    public void A_bullet_is_not_mistaken_for_a_rule()
    {
        MarkdownBlock block = Assert.Single(MarkdownDocument.Parse("- a real item"));

        Assert.Equal(MarkdownBlockKind.Bullet, block.Kind);
        Assert.Equal("a real item", block.Text);
    }

    [Fact]
    public void A_quote_drops_its_marker()
    {
        MarkdownBlock block = Assert.Single(MarkdownDocument.Parse("> mind the gap"));

        Assert.Equal(MarkdownBlockKind.Quote, block.Kind);
        Assert.Equal("mind the gap", block.Text);
    }

    [Theory]
    [InlineData("- [ ] not done", "\u2610", "not done")]
    [InlineData("- [x] done", "\u2611", "done")]
    [InlineData("- [X] done", "\u2611", "done")]
    public void A_task_item_keeps_its_box_as_the_marker(string line, string marker, string text)
    {
        MarkdownBlock block = Assert.Single(MarkdownDocument.Parse(line));

        Assert.Equal(MarkdownBlockKind.Bullet, block.Kind);
        Assert.Equal(marker, block.Marker);
        Assert.Equal(text, block.Text);
    }

    [Fact]
    public void Italic_is_read_but_bold_still_wins()
    {
        var runs = MarkdownDocument.ParseInline("**bold** and *slanted*");

        Assert.Equal(MarkdownInlineKind.Bold, runs[0].Kind);
        Assert.Equal("bold", runs[0].Text);
        Assert.Contains(runs, r => r.Kind == MarkdownInlineKind.Italic && r.Text == "slanted");
    }

    [Fact]
    public void Underscores_inside_a_word_are_left_alone()
    {
        // snake_case_names must not turn into italics half way through a sentence.
        var runs = MarkdownDocument.ParseInline("call some_long_name now");

        Assert.All(runs, r => Assert.Equal(MarkdownInlineKind.Text, r.Kind));
        Assert.Equal("call some_long_name now", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void An_underscore_pair_between_words_is_italic()
    {
        var runs = MarkdownDocument.ParseInline("this is _stressed_ here");

        Assert.Contains(runs, r => r.Kind == MarkdownInlineKind.Italic && r.Text == "stressed");
    }

    [Fact]
    public void A_link_carries_its_text_and_address()
    {
        var runs = MarkdownDocument.ParseInline("see [the docs](https://example.com/a) first");

        MarkdownInline link = Assert.Single(runs, r => r.Kind == MarkdownInlineKind.Link);
        Assert.Equal("the docs", link.Text);
        Assert.Equal("https://example.com/a", link.Href);
    }

    [Fact]
    public void A_bracket_without_an_address_is_plain_text()
    {
        var runs = MarkdownDocument.ParseInline("an [aside] in brackets");

        Assert.DoesNotContain(runs, r => r.Kind == MarkdownInlineKind.Link);
        Assert.Equal("an [aside] in brackets", string.Concat(runs.Select(r => r.Text)));
    }

    [Fact]
    public void Parse_separates_paragraphs_headings_and_bullets()
    {
        var blocks = MarkdownDocument.Parse("# Title\n\nHello world\n\n- one\n- two").ToList();

        Assert.Equal(MarkdownBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].HeadingLevel);
        Assert.Equal("Title", blocks[0].Text);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[1].Kind);
        Assert.Equal("Hello world", blocks[1].Text);
        Assert.Equal(MarkdownBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal("one", blocks[2].Text);
        Assert.Equal("two", blocks[3].Text);
    }

    [Fact]
    public void Parse_reads_a_numbered_list_and_keeps_the_authors_numbers()
    {
        var blocks = MarkdownDocument.Parse("5. five\n6) six\nnot a list 7. here").ToList();

        Assert.Equal(MarkdownBlockKind.Bullet, blocks[0].Kind);
        Assert.Equal("five", blocks[0].Text);
        Assert.Equal("5.", blocks[0].Marker);
        Assert.Equal("six", blocks[1].Text);
        Assert.Equal("6)", blocks[1].Marker);
        Assert.Equal(MarkdownBlockKind.Paragraph, blocks[2].Kind);
    }

    [Fact]
    public void Parse_records_how_deeply_a_list_item_is_nested()
    {
        var blocks = MarkdownDocument.Parse("- top\n  - nested\n    - deeper").ToList();

        Assert.Equal(0, blocks[0].Indent);
        Assert.Equal(1, blocks[1].Indent);
        Assert.Equal(2, blocks[2].Indent);
        Assert.All(blocks, b => Assert.Equal("\u2022", b.Marker));
    }

    [Fact]
    public void Parse_reads_a_fenced_code_block_with_a_language()
    {
        var blocks = MarkdownDocument.Parse("do this:\n\n```csharp\nvar x = 1;\nreturn x;\n```\n\ndone").ToList();

        MarkdownBlock code = blocks.Single(b => b.Kind == MarkdownBlockKind.Code);
        Assert.Equal("csharp", code.Language);
        Assert.Equal("var x = 1;\nreturn x;", code.Text);
        Assert.Contains(blocks, b => b.Kind == MarkdownBlockKind.Paragraph && b.Text == "done");
    }

    [Fact]
    public void Parse_treats_an_unclosed_fence_as_code_so_streaming_renders()
    {
        // A half-arrived reply: the closing ``` has not streamed in yet.
        var blocks = MarkdownDocument.Parse("```csharp\nvar x = 1;").ToList();

        MarkdownBlock code = Assert.Single(blocks);
        Assert.Equal(MarkdownBlockKind.Code, code.Kind);
        Assert.Equal("var x = 1;", code.Text);
    }

    [Fact]
    public void ParseInline_splits_bold_and_inline_code()
    {
        List<MarkdownInline> runs = MarkdownDocument.ParseInline("use **Build** then `dotnet test` now").ToList();

        Assert.Equal(MarkdownInlineKind.Text, runs[0].Kind);
        Assert.Equal("use ", runs[0].Text);
        Assert.Equal(MarkdownInlineKind.Bold, runs[1].Kind);
        Assert.Equal("Build", runs[1].Text);
        Assert.Equal(" then ", runs[2].Text);
        Assert.Equal(MarkdownInlineKind.Code, runs[3].Kind);
        Assert.Equal("dotnet test", runs[3].Text);
        Assert.Equal(" now", runs[4].Text);
    }

    [Fact]
    public void ParseInline_leaves_an_unterminated_marker_as_plain_text()
    {
        List<MarkdownInline> runs = MarkdownDocument.ParseInline("a **bold that never closes").ToList();

        MarkdownInline only = Assert.Single(runs);
        Assert.Equal(MarkdownInlineKind.Text, only.Kind);
        Assert.Equal("a **bold that never closes", only.Text);
    }
}
