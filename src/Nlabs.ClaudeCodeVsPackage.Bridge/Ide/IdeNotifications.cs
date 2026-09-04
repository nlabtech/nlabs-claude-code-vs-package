using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Ide;

/// <summary>
/// Builds the unsolicited JSON-RPC notifications the IDE pushes to Claude Code so the model
/// follows what the developer is doing without being asked.
///
/// These are notifications (no id, no reply): the CLI consumes them to keep its picture of the
/// editor current. Two matter for a first-class connection - <c>selection_changed</c> (the
/// developer moved or changed the selection) and <c>at_mentioned</c> (the developer sent the
/// current selection to Claude). Positions are ZERO-BASED, matching the protocol; the Visual
/// Studio layer converts from its one-based lines before calling in.
///
/// This core carries no Visual Studio dependency, so the exact wire shape is unit-tested. It
/// only ever describes a path, a selection and the selected text - never anything about the
/// machine, the account or the connection token.
/// </summary>
public static class IdeNotifications
{
    /// <summary>
    /// A <c>selection_changed</c> notification for the given file and zero-based selection.
    /// </summary>
    public static string SelectionChanged(
        string filePath,
        string selectedText,
        int startLine, int startCharacter,
        int endLine, int endCharacter,
        bool isEmpty)
    {
        var notification = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "selection_changed",
            ["params"] = new JObject
            {
                ["text"] = selectedText ?? string.Empty,
                ["filePath"] = filePath,
                ["fileUrl"] = FileUrl(filePath),
                ["selection"] = new JObject
                {
                    ["start"] = new JObject { ["line"] = startLine, ["character"] = startCharacter },
                    ["end"] = new JObject { ["line"] = endLine, ["character"] = endCharacter },
                    ["isEmpty"] = isEmpty,
                },
            },
        };

        return notification.ToString(Formatting.None);
    }

    /// <summary>
    /// An <c>at_mentioned</c> notification: the developer pushed a file range to Claude. Lines
    /// are zero-based; an empty selection may pass the same line for start and end.
    /// </summary>
    public static string AtMentioned(string filePath, int lineStart, int lineEnd)
    {
        var notification = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = "at_mentioned",
            ["params"] = new JObject
            {
                ["filePath"] = filePath,
                ["lineStart"] = lineStart,
                ["lineEnd"] = lineEnd,
            },
        };

        return notification.ToString(Formatting.None);
    }

    private static string FileUrl(string filePath)
    {
        if (string.IsNullOrEmpty(filePath)) return string.Empty;
        try { return new Uri(filePath).AbsoluteUri; }
        catch { return filePath; }
    }
}
