using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>The shape the approval card should draw a pending call in.</summary>
public enum PreviewKind
{
    /// <summary>A shell command, shown on a prompt line.</summary>
    Command,
    /// <summary>A change to an existing file, shown as a diff.</summary>
    Diff,
    /// <summary>Whole file content, shown as one block.</summary>
    Content,
    /// <summary>A path, pattern or URL the call names, and nothing else worth drawing.</summary>
    Target,
    /// <summary>A tool we have no shape for: the raw JSON, as before.</summary>
    Raw,
}

/// <summary>What one line of a rendered diff is.</summary>
public enum DiffKind
{
    /// <summary>Unchanged, kept for context.</summary>
    Context,
    /// <summary>Only in the new text.</summary>
    Added,
    /// <summary>Only in the old text.</summary>
    Removed,
    /// <summary>A run of unchanged lines that was folded away; the text is how many.</summary>
    Gap,
}

/// <summary>One line of a rendered diff.</summary>
public sealed class DiffLine
{
    public DiffLine(DiffKind kind, string text)
    {
        Kind = kind;
        Text = text;
    }

    public DiffKind Kind { get; }
    public string Text { get; }
}

/// <summary>
/// Turns a pending tool call into something a developer can actually read before deciding.
///
/// The approval that matters most - an edit to a source file - used to arrive as pretty-printed
/// JSON, which puts the old and the new text in two quoted blobs with the newlines escaped. That is
/// the least readable form of the most consequential decision in the panel, so this rebuilds it as
/// a real line diff, and gives commands, new files and lookups their own shapes too.
///
/// Pure and dependency-free apart from the JSON parse, so every rule here is unit-tested. It never
/// masks secrets: the caller does that when it draws, because the masking preference lives with the
/// panel rather than the protocol.
/// </summary>
public sealed class ApprovalPreview
{
    /// <summary>Lines of unchanged text kept either side of a change before folding the rest away.</summary>
    private const int ContextLines = 3;

    /// <summary>
    /// Past this many cells the diff table is not worth building - the panel would stall while the
    /// developer waits to answer. Both sides are shown whole instead, which is still readable.
    /// </summary>
    private const long MaxDiffCells = 250000;

    private ApprovalPreview(PreviewKind kind, string titleKey)
    {
        Kind = kind;
        TitleKey = titleKey;
        Lines = Array.Empty<DiffLine>();
    }

    public PreviewKind Kind { get; private set; }

    /// <summary>The localisation key for what the call wants to do - the card's headline verb.</summary>
    public string TitleKey { get; private set; }

    /// <summary>The file, directory or URL the call names, when it names one.</summary>
    public string? Target { get; private set; }

    /// <summary>The command, the content, the pattern or the raw JSON, depending on the kind.</summary>
    public string Text { get; private set; } = string.Empty;

    /// <summary>A one-line note under the headline - Claude's own description of a command.</summary>
    public string? Note { get; private set; }

    /// <summary>The diff, when the kind is <see cref="PreviewKind.Diff"/>.</summary>
    public IReadOnlyList<DiffLine> Lines { get; private set; }

    public int Added { get; private set; }
    public int Removed { get; private set; }

    /// <summary>Builds the preview for one call. Never throws; anything unexpected lands on Raw.</summary>
    public static ApprovalPreview Build(string toolName, string inputJson)
    {
        JObject? obj = null;
        try { obj = JObject.Parse(inputJson); }
        catch { /* not JSON, or truncated - fall through to Raw */ }

        if (obj == null) return Raw(inputJson);

        switch (toolName)
        {
            case "Bash":
            case "BashOutput":
                return AsCommand(Str(obj, "command"), Str(obj, "description"), inputJson);

            case "Edit":
                return AsDiff(Str(obj, "file_path"), Str(obj, "old_string"), Str(obj, "new_string"), inputJson);

            case "MultiEdit":
                return AsMultiEdit(obj, inputJson);

            case "Write":
                return AsContent(Str(obj, "file_path"), Str(obj, "content"), inputJson);

            case "NotebookEdit":
                // The notebook tool sends the replacement cell, not the cell it replaces, so there is
                // nothing to diff against - the new source on its own is the honest picture.
                return AsContent(Str(obj, "notebook_path"), Str(obj, "new_source"), inputJson);

            case "Read":
                return AsTarget(Str(obj, "file_path"), null, "wantsToRead", inputJson);

            case "Glob":
            case "Grep":
                return AsTarget(Str(obj, "path"), Str(obj, "pattern"), "wantsToSearch", inputJson);

            case "WebFetch":
                return AsTarget(null, Str(obj, "url"), "wantsToFetch", inputJson);

            case "WebSearch":
                return AsTarget(null, Str(obj, "query"), "wantsToSearch", inputJson);

            case "openSolution":
                return AsTarget(Str(obj, "path"), null, "wantsToOpen", inputJson);

            default:
                return Raw(inputJson);
        }
    }

    private static ApprovalPreview Raw(string json) =>
        new ApprovalPreview(PreviewKind.Raw, "wantsToRun") { Text = json };

    private static ApprovalPreview AsCommand(string? command, string? description, string json)
    {
        if (string.IsNullOrEmpty(command)) return Raw(json);
        return new ApprovalPreview(PreviewKind.Command, "wantsToRun")
        {
            Text = command!,
            Note = string.IsNullOrWhiteSpace(description) ? null : description,
        };
    }

    private static ApprovalPreview AsContent(string? path, string? content, string json)
    {
        if (content == null) return Raw(json);
        return new ApprovalPreview(PreviewKind.Content, "wantsToCreate")
        {
            Target = path,
            Text = content,
            Added = CountLines(content),
        };
    }

    private static ApprovalPreview AsTarget(string? path, string? text, string titleKey, string json)
    {
        if (string.IsNullOrEmpty(path) && string.IsNullOrEmpty(text)) return Raw(json);
        return new ApprovalPreview(PreviewKind.Target, titleKey)
        {
            Target = path,
            Text = text ?? string.Empty,
        };
    }

    private static ApprovalPreview AsDiff(string? path, string? oldText, string? newText, string json)
    {
        // A replacement with no anchor is a write in disguise; anything with neither side is not an
        // edit we can draw.
        if (oldText == null && newText == null) return Raw(json);
        if (string.IsNullOrEmpty(oldText)) return AsContent(path, newText, json);

        List<DiffLine> lines = Fold(Compare(Split(oldText!), Split(newText ?? string.Empty)));
        return Finish(PreviewKind.Diff, "wantsToEdit", path, lines);
    }

    private static ApprovalPreview AsMultiEdit(JObject obj, string json)
    {
        if (!(obj["edits"] is JArray edits) || edits.Count == 0) return Raw(json);

        var lines = new List<DiffLine>();
        foreach (JToken edit in edits)
        {
            if (!(edit is JObject e)) continue;
            string oldText = Str(e, "old_string") ?? string.Empty;
            string newText = Str(e, "new_string") ?? string.Empty;

            // A blank line between edits, so two unrelated hunks don't read as one run of changes.
            if (lines.Count > 0) lines.Add(new DiffLine(DiffKind.Gap, "0"));
            lines.AddRange(Fold(Compare(Split(oldText), Split(newText))));
        }

        if (lines.Count == 0) return Raw(json);
        return Finish(PreviewKind.Diff, "wantsToEdit", Str(obj, "file_path"), lines);
    }

    private static ApprovalPreview Finish(PreviewKind kind, string titleKey, string? path, List<DiffLine> lines)
    {
        var preview = new ApprovalPreview(kind, titleKey) { Target = path, Lines = lines };
        foreach (DiffLine line in lines)
        {
            if (line.Kind == DiffKind.Added) preview.Added++;
            else if (line.Kind == DiffKind.Removed) preview.Removed++;
        }
        return preview;
    }

    private static string[] Split(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

    private static int CountLines(string text) =>
        text.Length == 0 ? 0 : Split(text).Length;

    private static string? Str(JObject obj, string name) => (string?)obj[name];

    /// <summary>
    /// The longest-common-subsequence diff, walked back into a line list. Classic table fill; the
    /// only twist is the size guard, because an approval card must appear now, not after a big edit
    /// finishes being diffed.
    /// </summary>
    private static List<DiffLine> Compare(string[] a, string[] b)
    {
        var lines = new List<DiffLine>();

        if ((long)(a.Length + 1) * (b.Length + 1) > MaxDiffCells)
        {
            foreach (string line in a) lines.Add(new DiffLine(DiffKind.Removed, line));
            foreach (string line in b) lines.Add(new DiffLine(DiffKind.Added, line));
            return lines;
        }

        var lcs = new int[a.Length + 1, b.Length + 1];
        for (int i = a.Length - 1; i >= 0; i--)
        {
            for (int j = b.Length - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        int x = 0, y = 0;
        while (x < a.Length && y < b.Length)
        {
            if (a[x] == b[y])
            {
                lines.Add(new DiffLine(DiffKind.Context, a[x]));
                x++;
                y++;
            }
            else if (lcs[x + 1, y] >= lcs[x, y + 1])
            {
                lines.Add(new DiffLine(DiffKind.Removed, a[x]));
                x++;
            }
            else
            {
                lines.Add(new DiffLine(DiffKind.Added, b[y]));
                y++;
            }
        }

        while (x < a.Length) lines.Add(new DiffLine(DiffKind.Removed, a[x++]));
        while (y < b.Length) lines.Add(new DiffLine(DiffKind.Added, b[y++]));
        return lines;
    }

    /// <summary>
    /// Folds long runs of unchanged lines into a single marker, so a two-line change inside a large
    /// block stays a two-line change on screen. A run of one is left alone - "1 line hidden" takes
    /// more room than the line it hides.
    /// </summary>
    private static List<DiffLine> Fold(List<DiffLine> lines)
    {
        var keep = new bool[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Kind == DiffKind.Context) continue;
            int from = Math.Max(0, i - ContextLines);
            int to = Math.Min(lines.Count - 1, i + ContextLines);
            for (int j = from; j <= to; j++) keep[j] = true;
        }

        for (int i = 0; i < keep.Length; i++)
        {
            if (keep[i]) continue;
            int run = i;
            while (run < keep.Length && !keep[run]) run++;
            if (run - i == 1) keep[i] = true;
            i = run - 1;
        }

        var folded = new List<DiffLine>();
        int hidden = 0;
        foreach (var pair in Indexed(lines))
        {
            if (!keep[pair.Key]) { hidden++; continue; }
            if (hidden > 0)
            {
                folded.Add(new DiffLine(DiffKind.Gap, hidden.ToString()));
                hidden = 0;
            }
            folded.Add(pair.Value);
        }

        if (hidden > 0) folded.Add(new DiffLine(DiffKind.Gap, hidden.ToString()));
        return folded;
    }

    private static IEnumerable<KeyValuePair<int, DiffLine>> Indexed(List<DiffLine> lines)
    {
        for (int i = 0; i < lines.Count; i++) yield return new KeyValuePair<int, DiffLine>(i, lines[i]);
    }
}
