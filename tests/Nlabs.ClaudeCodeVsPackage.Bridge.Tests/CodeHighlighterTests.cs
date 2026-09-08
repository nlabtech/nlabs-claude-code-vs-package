using Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;
using System.Linq;
using Xunit;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Tests;

public class CodeHighlighterTests
{
    private static string TextOf(string code, string? lang, CodeSpanKind kind) =>
        string.Concat(CodeHighlighter.Highlight(code, lang).Where(s => s.Kind == kind).Select(s => s.Text));

    [Fact]
    public void Highlight_round_trips_the_original_text()
    {
        const string code = "public int X = 1; // note\nvar s = \"hi\";";

        string joined = string.Concat(CodeHighlighter.Highlight(code, "csharp").Select(s => s.Text));

        Assert.Equal(code, joined); // nothing is lost or duplicated
    }

    [Fact]
    public void Highlight_marks_csharp_keywords_strings_numbers_and_comments()
    {
        const string code = "public var n = 42; // trailing\nstring s = \"hi\";";

        Assert.Contains("public", TextOf(code, "csharp", CodeSpanKind.Keyword));
        Assert.Contains("var", TextOf(code, "csharp", CodeSpanKind.Keyword));
        Assert.Contains("42", TextOf(code, "csharp", CodeSpanKind.Number));
        Assert.Contains("\"hi\"", TextOf(code, "csharp", CodeSpanKind.String));
        Assert.Contains("// trailing", TextOf(code, "csharp", CodeSpanKind.Comment));
    }

    [Fact]
    public void Highlight_uses_hash_comments_for_python()
    {
        const string code = "def f():  # explain\n    return None";

        Assert.Contains("# explain", TextOf(code, "python", CodeSpanKind.Comment));
        Assert.Contains("def", TextOf(code, "python", CodeSpanKind.Keyword));
    }

    [Fact]
    public void Highlight_handles_a_block_comment_and_escaped_quotes()
    {
        const string code = "/* a\nb */ var s = \"he said \\\"hi\\\"\";";

        Assert.Contains("/* a\nb */", TextOf(code, "js", CodeSpanKind.Comment));
        Assert.Contains("\"he said \\\"hi\\\"\"", TextOf(code, "js", CodeSpanKind.String));
    }

    [Fact]
    public void Highlight_does_not_let_an_unterminated_quote_swallow_the_rest()
    {
        const string code = "var a = \"oops\nvar b = 2;";

        // The stray quote ends at the newline, so the second line still parses normally.
        Assert.Contains("2", TextOf(code, "js", CodeSpanKind.Number));
    }

    [Fact]
    public void Highlight_of_an_unknown_language_still_finds_strings_and_numbers()
    {
        const string code = "thing \"quoted\" 7";

        Assert.Contains("\"quoted\"", TextOf(code, "fancylang", CodeSpanKind.String));
        Assert.Contains("7", TextOf(code, "fancylang", CodeSpanKind.Number));
        Assert.Empty(TextOf(code, "fancylang", CodeSpanKind.Keyword));
    }

    [Fact]
    public void Highlight_of_empty_code_is_empty()
    {
        Assert.Empty(CodeHighlighter.Highlight("", "csharp"));
        Assert.Empty(CodeHighlighter.Highlight(null, null));
    }
}
