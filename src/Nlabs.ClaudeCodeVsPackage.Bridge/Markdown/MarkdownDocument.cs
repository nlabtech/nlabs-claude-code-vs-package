using System;
using System.Collections.Generic;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;

/// <summary>The kind of a block-level element in a parsed assistant reply.</summary>
public enum MarkdownBlockKind
{
    Paragraph,
    Heading,
    Code,
    Bullet,
    /// <summary>A pipe table; the cells are in <see cref="MarkdownBlock.Rows"/>.</summary>
    Table,
    /// <summary>A horizontal rule - a break between sections, with no text of its own.</summary>
    Rule,
    /// <summary>A block quote; the text is the quoted line with its marker removed.</summary>
    Quote,
}

/// <summary>One block-level element: a paragraph, a heading, a fenced code block or a bullet item.</summary>
public sealed class MarkdownBlock
{
    public MarkdownBlockKind Kind { get; }
    /// <summary>The block's text. For a code block it is the raw code (fences stripped).</summary>
    public string Text { get; }
    /// <summary>Heading level 1-6; 0 for non-headings.</summary>
    public int HeadingLevel { get; }
    /// <summary>Fenced code language tag ("csharp", "" if none); empty for non-code.</summary>
    public string Language { get; }

    /// <summary>
    /// What a bullet is marked with: a dot for an unordered item, or the list's own number ("3.")
    /// for an ordered one. Keeping the author's number rather than re-counting means a list that
    /// starts at 5, or is interrupted by a paragraph, still reads the way it was written.
    /// </summary>
    public string Marker { get; }

    /// <summary>Nesting depth of a bullet, in levels; 0 for a top-level item and every other block.</summary>
    public int Indent { get; }

    /// <summary>
    /// A table's cells, row by row, with the header first. Empty for every other block. Rows are not
    /// padded to a common width: a ragged table is shown as written rather than silently squared off.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    private static readonly IReadOnlyList<IReadOnlyList<string>> NoRows = new List<IReadOnlyList<string>>();

    public MarkdownBlock(MarkdownBlockKind kind, string text, int headingLevel = 0, string language = "",
        string marker = "", int indent = 0, IReadOnlyList<IReadOnlyList<string>>? rows = null)
    {
        Kind = kind;
        Text = text;
        HeadingLevel = headingLevel;
        Language = language ?? string.Empty;
        Marker = marker ?? string.Empty;
        Indent = indent;
        Rows = rows ?? NoRows;
    }
}

/// <summary>One inline run inside a paragraph/heading/bullet.</summary>
public enum MarkdownInlineKind { Text, Bold, Code, Italic, Link }

public readonly struct MarkdownInline
{
    public MarkdownInlineKind Kind { get; }
    public string Text { get; }

    /// <summary>Where a <see cref="MarkdownInlineKind.Link"/> points; empty for every other run.</summary>
    public string Href { get; }

    public MarkdownInline(MarkdownInlineKind kind, string text, string href = "")
    {
        Kind = kind;
        Text = text;
        Href = href ?? string.Empty;
    }
}

/// <summary>
/// A deliberately small Markdown reader for the agent panel. It turns a streamed assistant reply
/// into block elements (paragraph / heading / fenced code / bullet) and each non-code block into
/// inline runs (plain / bold / inline code) so the panel can render code as a monospace block and
/// emphasis inline, instead of showing raw asterisks and backticks.
///
/// It is tolerant of a still-streaming reply: an unclosed ``` fence is emitted as a code block up
/// to the current end of text, so a half-arrived code block already renders as code rather than as
/// paragraphs full of backticks. It is intentionally not a full CommonMark implementation - just the
/// handful of constructs Claude's replies use in practice - and it is pure, so it is unit-tested
/// without any UI.
/// </summary>
public static class MarkdownDocument
{
    /// <summary>Splits a reply into block-level elements.</summary>
    public static IReadOnlyList<MarkdownBlock> Parse(string? text)
    {
        var blocks = new List<MarkdownBlock>();
        if (string.IsNullOrEmpty(text)) return blocks;

        string[] lines = text!.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        var paragraph = new List<string>();
        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MarkdownBlock(MarkdownBlockKind.Paragraph, string.Join(" ", paragraph).Trim()));
            paragraph.Clear();
        }

        // How deep a list item sits: every two leading spaces (or one tab) is one level, capped so a
        // deeply indented reply cannot push its own text off the panel.
        int IndentLevel(string raw)
        {
            int spaces = 0;
            foreach (char c in raw)
            {
                if (c == ' ') spaces++;
                else if (c == '\t') spaces += 2;
                else break;
            }
            return Math.Min(spaces / 2, 4);
        }

        // Length of a "12. " or "12) " marker including its trailing space, or 0 when there is none.
        int OrderedMarkerLength(string text)
        {
            int digits = 0;
            while (digits < text.Length && text[digits] >= '0' && text[digits] <= '9') digits++;
            if (digits == 0 || digits > 3) return 0;
            if (digits + 1 >= text.Length) return 0;
            if (text[digits] != '.' && text[digits] != ')') return 0;
            if (text[digits + 1] != ' ') return 0;
            return digits + 2;
        }

        int i = 0;
        while (i < lines.Length)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();

            // Fenced code block: ``` optionally followed by a language tag.
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                string language = trimmed.Substring(3).Trim();
                var code = new List<string>();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    code.Add(lines[i]);
                    i++;
                }
                // Skip the closing fence when present; a missing one (still streaming) just ends here.
                if (i < lines.Length) i++;
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Code, string.Join("\n", code), language: language));
                continue;
            }

            // Blank line separates paragraphs.
            if (trimmed.Length == 0)
            {
                FlushParagraph();
                i++;
                continue;
            }

            // Heading: one to six leading '#' then a space.
            int hashes = 0;
            while (hashes < trimmed.Length && trimmed[hashes] == '#') hashes++;
            if (hashes >= 1 && hashes <= 6 && hashes < trimmed.Length && trimmed[hashes] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(
                    MarkdownBlockKind.Heading, trimmed.Substring(hashes + 1).Trim(), headingLevel: hashes));
                i++;
                continue;
            }

            // Horizontal rule: three or more of - * _ and nothing else. Checked before bullets, so a
            // "---" section break is not mistaken for a list that lost its text.
            if (IsRule(trimmed))
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Rule, string.Empty));
                i++;
                continue;
            }

            // Pipe table: a row of cells followed by a row of dashes. Without the separator it is
            // just a line that happens to contain a pipe, which is common in shell commands.
            if (trimmed.IndexOf('|') >= 0 && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
            {
                FlushParagraph();
                var rows = new List<IReadOnlyList<string>> { SplitRow(trimmed) };
                i += 2; // the header and the separator under it
                while (i < lines.Length && lines[i].IndexOf('|') >= 0 && lines[i].Trim().Length > 0)
                {
                    rows.Add(SplitRow(lines[i].Trim()));
                    i++;
                }
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Table, string.Empty, rows: rows));
                continue;
            }

            // Block quote: "> text".
            if (trimmed.Length >= 1 && trimmed[0] == '>')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Quote, trimmed.Substring(1).Trim()));
                i++;
                continue;
            }

            // Bullet item: "- ", "* " or "+ ". A task item ("- [ ] ", "- [x] ") keeps its box as the
            // marker, so a checklist still reads as one instead of as brackets in the text.
            if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
            {
                FlushParagraph();
                string body = trimmed.Substring(2).Trim();
                string mark = "\u2022";
                if (body.Length >= 3 && body[0] == '[' && body[2] == ']' &&
                    (body[1] == ' ' || body[1] == 'x' || body[1] == 'X'))
                {
                    mark = body[1] == ' ' ? "\u2610" : "\u2611"; // empty box / ticked box
                    body = body.Substring(3).Trim();
                }

                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, body,
                    marker: mark, indent: IndentLevel(line)));
                i++;
                continue;
            }

            // Numbered item: "1. " or "1) ". Rendered as a bullet carrying the author's own number,
            // which matters when a reply says "fix 3 first" about a list it just wrote.
            int numberEnd = OrderedMarkerLength(trimmed);
            if (numberEnd > 0)
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, trimmed.Substring(numberEnd).Trim(),
                    marker: trimmed.Substring(0, numberEnd - 1), indent: IndentLevel(line)));
                i++;
                continue;
            }

            paragraph.Add(trimmed);
            i++;
        }

        FlushParagraph();
        return blocks;
    }

    private static char Before(string s, int i) => i == 0 ? ' ' : s[i - 1];

    private static char After(string s, int i) => i + 1 >= s.Length ? ' ' : s[i + 1];

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Three or more of the same rule character, and nothing else on the line.</summary>
    private static bool IsRule(string trimmed)
    {
        if (trimmed.Length < 3) return false;
        char c = trimmed[0];
        if (c != '-' && c != '*' && c != '_') return false;

        int count = 0;
        foreach (char ch in trimmed)
        {
            if (ch == c) count++;
            else if (ch != ' ') return false;
        }
        return count >= 3;
    }

    /// <summary>
    /// The dashed row under a table's header: cells of dashes, optionally with alignment colons.
    /// This is what separates a real table from a line that merely contains a pipe - a shell command
    /// with a pipe in it is far more common in these replies than a table is.
    /// </summary>
    private static bool IsTableSeparator(string raw)
    {
        string line = raw.Trim();
        if (line.Length == 0 || line.IndexOf('|') < 0) return false;

        foreach (string cell in Cells(line))
        {
            string c = cell.Trim();
            if (c.Length == 0) return false;

            int dashes = 0;
            for (int j = 0; j < c.Length; j++)
            {
                if (c[j] == '-') dashes++;
                else if (c[j] != ':') return false;
                else if (j != 0 && j != c.Length - 1) return false; // a colon only aligns at an edge
            }
            if (dashes == 0) return false;
        }
        return true;
    }

    private static IReadOnlyList<string> SplitRow(string line)
    {
        var cells = new List<string>();
        foreach (string cell in Cells(line.Trim())) cells.Add(cell.Trim());
        return cells;
    }

    // Splits on pipes, dropping the optional leading and trailing one so "| a | b |" is two cells.
    private static List<string> Cells(string line)
    {
        string body = line;
        if (body.Length > 0 && body[0] == '|') body = body.Substring(1);
        if (body.Length > 0 && body[body.Length - 1] == '|') body = body.Substring(0, body.Length - 1);
        return new List<string>(body.Split('|'));
    }

    /// <summary>Splits one line of text into inline runs: bold (**..**) and inline code (`..`).</summary>
    public static IReadOnlyList<MarkdownInline> ParseInline(string? text)
    {
        var runs = new List<MarkdownInline>();
        if (string.IsNullOrEmpty(text)) return runs;

        string s = text!;
        var plain = new System.Text.StringBuilder();
        void FlushPlain()
        {
            if (plain.Length == 0) return;
            runs.Add(new MarkdownInline(MarkdownInlineKind.Text, plain.ToString()));
            plain.Clear();
        }

        int i = 0;
        while (i < s.Length)
        {
            // Inline code: `...` (single backtick, no nesting).
            if (s[i] == '`')
            {
                int close = s.IndexOf('`', i + 1);
                if (close > i)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Code, s.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
            }
            // Bold: **...**. Checked before italic so the opening ** is not read as one asterisk.
            else if (i + 1 < s.Length && s[i] == '*' && s[i + 1] == '*')
            {
                int close = s.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (close > i)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Bold, s.Substring(i + 2, close - i - 2)));
                    i = close + 2;
                    continue;
                }
            }
            // Italic: *...* or _..._. The underscore form only counts between non-word characters,
            // so snake_case_names are left exactly as they were written.
            else if (s[i] == '*' || (s[i] == '_' && !IsWordChar(Before(s, i))))
            {
                char mark = s[i];
                int close = s.IndexOf(mark, i + 1);
                if (close > i + 1 &&
                    (mark == '*' || !IsWordChar(After(s, close))) &&
                    s.IndexOf('\n', i, close - i) < 0)
                {
                    FlushPlain();
                    runs.Add(new MarkdownInline(MarkdownInlineKind.Italic, s.Substring(i + 1, close - i - 1)));
                    i = close + 1;
                    continue;
                }
            }
            // Link: [text](href).
            else if (s[i] == '[')
            {
                int endText = s.IndexOf(']', i + 1);
                if (endText > i && endText + 1 < s.Length && s[endText + 1] == '(')
                {
                    int endHref = s.IndexOf(')', endText + 2);
                    if (endHref > endText)
                    {
                        FlushPlain();
                        runs.Add(new MarkdownInline(
                            MarkdownInlineKind.Link,
                            s.Substring(i + 1, endText - i - 1),
                            s.Substring(endText + 2, endHref - endText - 2)));
                        i = endHref + 1;
                        continue;
                    }
                }
            }

            plain.Append(s[i]);
            i++;
        }

        FlushPlain();
        return runs;
    }
}
