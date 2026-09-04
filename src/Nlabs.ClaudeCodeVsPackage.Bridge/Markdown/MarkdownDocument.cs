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

    public MarkdownBlock(MarkdownBlockKind kind, string text, int headingLevel = 0, string language = "")
    {
        Kind = kind;
        Text = text;
        HeadingLevel = headingLevel;
        Language = language ?? string.Empty;
    }
}

/// <summary>One inline run inside a paragraph/heading/bullet: plain text, bold, or inline code.</summary>
public enum MarkdownInlineKind { Text, Bold, Code }

public readonly struct MarkdownInline
{
    public MarkdownInlineKind Kind { get; }
    public string Text { get; }
    public MarkdownInline(MarkdownInlineKind kind, string text)
    {
        Kind = kind;
        Text = text;
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

            // Bullet item: "- ", "* " or "+ ".
            if (trimmed.Length >= 2 && (trimmed[0] == '-' || trimmed[0] == '*' || trimmed[0] == '+') && trimmed[1] == ' ')
            {
                FlushParagraph();
                blocks.Add(new MarkdownBlock(MarkdownBlockKind.Bullet, trimmed.Substring(2).Trim()));
                i++;
                continue;
            }

            paragraph.Add(trimmed);
            i++;
        }

        FlushParagraph();
        return blocks;
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
            // Bold: **...**.
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

            plain.Append(s[i]);
            i++;
        }

        FlushPlain();
        return runs;
    }
}
