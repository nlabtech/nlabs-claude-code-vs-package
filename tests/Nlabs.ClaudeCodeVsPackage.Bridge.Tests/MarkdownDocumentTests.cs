using System.Collections.Generic;
using System.Linq;
using Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class MarkdownDocumentTests
{
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
