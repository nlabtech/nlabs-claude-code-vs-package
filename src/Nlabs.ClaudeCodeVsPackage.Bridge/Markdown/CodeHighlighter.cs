using System;
using System.Collections.Generic;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;

/// <summary>What a highlighted run of code is, so the renderer can colour it.</summary>
public enum CodeSpanKind
{
    Plain,
    Keyword,
    String,
    Comment,
    Number,
}

/// <summary>One coloured run of a code block.</summary>
public sealed class CodeSpan
{
    public CodeSpan(string text, CodeSpanKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }
    public CodeSpanKind Kind { get; }
}

/// <summary>
/// A small, language-aware scanner that splits a code block into coloured runs - the difference
/// between a wall of monospace text and code you can actually read at a glance.
///
/// It is deliberately modest: comments, strings, numbers and a per-language keyword set, in one
/// pass. It is NOT a parser, so it never fails on partial or invalid code - anything it does not
/// recognise stays <see cref="CodeSpanKind.Plain"/>, which is exactly the right behaviour for a chat
/// panel that receives half-finished snippets while a reply streams in. Pure and dependency-free,
/// so the rules are unit-tested without a UI.
/// </summary>
public static class CodeHighlighter
{
    // Keyword sets per language family. Deliberately short: the common words that carry the shape of
    // the code, not an exhaustive grammar.
    private static readonly string[] CSharpKeywords =
    {
        "abstract","as","async","await","base","bool","break","byte","case","catch","char","class","const",
        "continue","decimal","default","do","double","else","enum","event","explicit","extern","false",
        "finally","float","for","foreach","get","goto","if","implicit","in","int","interface","internal",
        "is","lock","long","namespace","new","null","object","operator","out","override","params","private",
        "protected","public","readonly","record","ref","return","sealed","set","short","sizeof","stackalloc",
        "static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked",
        "unsafe","ushort","using","var","virtual","void","volatile","when","where","while","yield",
    };

    private static readonly string[] JsKeywords =
    {
        "as","async","await","break","case","catch","class","const","continue","default","delete","do","else",
        "enum","export","extends","false","finally","for","from","function","get","if","implements","import",
        "in","instanceof","interface","let","new","null","of","private","protected","public","readonly",
        "return","set","static","super","switch","this","throw","true","try","type","typeof","undefined",
        "var","void","while","yield",
    };

    private static readonly string[] PythonKeywords =
    {
        "and","as","assert","async","await","break","class","continue","def","del","elif","else","except",
        "False","finally","for","from","global","if","import","in","is","lambda","None","nonlocal","not","or",
        "pass","raise","return","self","True","try","while","with","yield",
    };

    private static readonly string[] SqlKeywords =
    {
        "alter","and","as","asc","by","case","create","cross","delete","desc","distinct","drop","else","end",
        "exists","from","full","group","having","in","index","inner","insert","into","is","join","left","like",
        "limit","not","null","on","or","order","outer","right","select","set","table","then","top","union",
        "update","values","when","where","with",
    };

    private static readonly string[] JsonKeywords = { "true", "false", "null" };

    /// <summary>Splits <paramref name="code"/> into coloured runs for the given fence language.</summary>
    public static IReadOnlyList<CodeSpan> Highlight(string? code, string? language)
    {
        var spans = new List<CodeSpan>();
        if (string.IsNullOrEmpty(code)) return spans;

        HashSet<string> keywords = KeywordsFor(language);
        bool hashComments = UsesHashComments(language);
        bool slashComments = !hashComments; // C-like languages; also the safe default for unknown ones
        string text = code!;
        int i = 0;
        int plainStart = 0;

        void FlushPlain(int end)
        {
            if (end > plainStart) spans.Add(new CodeSpan(text.Substring(plainStart, end - plainStart), CodeSpanKind.Plain));
        }

        while (i < text.Length)
        {
            char c = text[i];

            // Line comment
            if ((hashComments && c == '#') ||
                (slashComments && c == '/' && i + 1 < text.Length && text[i + 1] == '/') ||
                (c == '-' && i + 1 < text.Length && text[i + 1] == '-' && IsSql(language)))
            {
                FlushPlain(i);
                int end = text.IndexOf('\n', i);
                if (end < 0) end = text.Length;
                spans.Add(new CodeSpan(text.Substring(i, end - i), CodeSpanKind.Comment));
                i = plainStart = end;
                continue;
            }

            // Block comment
            if (slashComments && c == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                FlushPlain(i);
                int end = text.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? text.Length : end + 2;
                spans.Add(new CodeSpan(text.Substring(i, end - i), CodeSpanKind.Comment));
                i = plainStart = end;
                continue;
            }

            // String literal - a newline ends it, so an unterminated quote cannot swallow the rest.
            if (c == '"' || c == '\'' || c == '`')
            {
                FlushPlain(i);
                int end = ScanString(text, i, c);
                spans.Add(new CodeSpan(text.Substring(i, end - i), CodeSpanKind.String));
                i = plainStart = end;
                continue;
            }

            // Number (not when it is part of an identifier like utf8)
            if (char.IsDigit(c) && (i == 0 || !IsWordChar(text[i - 1])))
            {
                FlushPlain(i);
                int end = i;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '.' || text[end] == '_')) end++;
                spans.Add(new CodeSpan(text.Substring(i, end - i), CodeSpanKind.Number));
                i = plainStart = end;
                continue;
            }

            // Identifier - a keyword if the set knows it
            if (IsWordStart(c))
            {
                int end = i;
                while (end < text.Length && IsWordChar(text[end])) end++;
                string word = text.Substring(i, end - i);
                if (keywords.Contains(word))
                {
                    FlushPlain(i);
                    spans.Add(new CodeSpan(word, CodeSpanKind.Keyword));
                    plainStart = end;
                }
                i = end;
                continue;
            }

            i++;
        }

        FlushPlain(text.Length);
        return spans;
    }

    // Consumes a quoted literal starting at the opening quote; handles backslash escapes and stops at
    // a newline for single/double quotes (backticks legitimately span lines).
    private static int ScanString(string text, int start, char quote)
    {
        int i = start + 1;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '\\' && i + 1 < text.Length) { i += 2; continue; }
            if (c == quote) return i + 1;
            if (c == '\n' && quote != '`') return i;
            i++;
        }
        return text.Length;
    }

    private static HashSet<string> KeywordsFor(string? language)
    {
        switch (Normalize(language))
        {
            case "csharp": return new HashSet<string>(CSharpKeywords, StringComparer.Ordinal);
            case "js": return new HashSet<string>(JsKeywords, StringComparer.Ordinal);
            case "python": return new HashSet<string>(PythonKeywords, StringComparer.Ordinal);
            case "sql": return new HashSet<string>(SqlKeywords, StringComparer.OrdinalIgnoreCase);
            case "json": return new HashSet<string>(JsonKeywords, StringComparer.Ordinal);
            default: return new HashSet<string>(StringComparer.Ordinal); // strings/numbers/comments only
        }
    }

    private static bool UsesHashComments(string? language)
    {
        string norm = Normalize(language);
        return norm == "python" || norm == "shell" || norm == "yaml";
    }

    private static bool IsSql(string? language) => Normalize(language) == "sql";

    // Folds the many fence aliases onto one family name.
    private static string Normalize(string? language)
    {
        string lang = (language ?? string.Empty).Trim().ToLowerInvariant();
        switch (lang)
        {
            case "cs": case "c#": case "csharp": return "csharp";
            case "js": case "jsx": case "javascript": case "ts": case "tsx": case "typescript": return "js";
            case "py": case "python": return "python";
            case "sql": return "sql";
            case "json": return "json";
            case "sh": case "bash": case "shell": case "zsh": case "ps1": case "powershell": return "shell";
            case "yml": case "yaml": return "yaml";
            default: return lang;
        }
    }

    private static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
}
