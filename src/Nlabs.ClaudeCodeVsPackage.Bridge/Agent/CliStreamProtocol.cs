using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Text;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>The kind of event a line of Claude Code's stream-json output represents.</summary>
public enum CliEventKind
{
    /// <summary>Session start: model, session id, available tools.</summary>
    SystemInit,
    /// <summary>A full assistant message (its text blocks joined).</summary>
    Assistant,
    /// <summary>An echoed user message or tool result.</summary>
    UserEcho,
    /// <summary>A partial-message delta (streaming text as it arrives).</summary>
    StreamDelta,
    /// <summary>Turn finished: total cost, success/failure.</summary>
    Result,
    /// <summary>A line that could not be parsed as JSON.</summary>
    Unknown,
}

/// <summary>One item of the agent's to-do list, from a TodoWrite tool call.</summary>
public sealed class TodoItem
{
    public string Content { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty; // pending | in_progress | completed
}

/// <summary>An image sent with a user turn: its media type and base64 bytes.</summary>
public sealed class ImageAttachment
{
    public string MediaType { get; set; } = "image/png";
    public string Base64Data { get; set; } = string.Empty;
}

/// <summary>A tool the agent invoked this turn: its name and a one-line summary of the call.</summary>
public sealed class ToolCall
{
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
}

/// <summary>One parsed event from the CLI's stream-json output.</summary>
public sealed class CliEvent
{
    public CliEventKind Kind { get; set; }
    public string? Text { get; set; }
    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public double? TotalCostUsd { get; set; }
    public bool IsError { get; set; }
    public string Raw { get; set; } = string.Empty;

    /// <summary>The agent's to-do list when this assistant turn wrote one; null otherwise.</summary>
    public System.Collections.Generic.IReadOnlyList<TodoItem>? Todos { get; set; }

    /// <summary>The tools this assistant turn invoked, in order; null when it called none.</summary>
    public System.Collections.Generic.IReadOnlyList<ToolCall>? Tools { get; set; }
}

/// <summary>
/// Speaks the stream-json protocol used to drive Claude Code headlessly
/// (<c>claude -p --input-format stream-json --output-format stream-json</c>): it formats the
/// user's turn for stdin and parses each stdout line into a <see cref="CliEvent"/> the panel
/// can render.
///
/// It is deliberately free of any process or Visual Studio dependency, so the whole protocol is
/// unit-tested by feeding it lines - the agentic panel wires a real process to it. Parsing is
/// tolerant: an unrecognised or non-JSON line becomes <see cref="CliEventKind.Unknown"/> rather
/// than throwing, so one odd line never breaks the stream.
/// </summary>
public static class CliStreamProtocol
{
    /// <summary>The stdin JSONL line for one user turn.</summary>
    public static string UserMessage(string text) => UserMessage(text, null);

    /// <summary>
    /// The stdin JSONL line for one user turn, with any attached images. Images ride along as
    /// base64 blocks in the same content array (the Messages API shape stream-json expects), placed
    /// before the text so the model sees them as context for the prompt.
    /// </summary>
    public static string UserMessage(string text, System.Collections.Generic.IEnumerable<ImageAttachment>? images)
    {
        var content = new JArray();
        if (images != null)
        {
            foreach (ImageAttachment img in images)
            {
                if (img == null || string.IsNullOrEmpty(img.Base64Data)) continue;
                content.Add(new JObject
                {
                    ["type"] = "image",
                    ["source"] = new JObject
                    {
                        ["type"] = "base64",
                        ["media_type"] = string.IsNullOrEmpty(img.MediaType) ? "image/png" : img.MediaType,
                        ["data"] = img.Base64Data,
                    },
                });
            }
        }
        content.Add(new JObject { ["type"] = "text", ["text"] = text ?? string.Empty });

        var message = new JObject
        {
            ["type"] = "user",
            ["message"] = new JObject { ["role"] = "user", ["content"] = content },
        };
        return message.ToString(Formatting.None);
    }

    /// <summary>Parses one stdout line into a <see cref="CliEvent"/>.</summary>
    public static CliEvent Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return new CliEvent { Kind = CliEventKind.Unknown, Raw = line ?? string.Empty };
        }

        JObject obj;
        try
        {
            obj = JObject.Parse(line);
        }
        catch
        {
            return new CliEvent { Kind = CliEventKind.Unknown, Raw = line };
        }

        var type = (string?)obj["type"];
        switch (type)
        {
            case "system":
                return new CliEvent
                {
                    Kind = CliEventKind.SystemInit,
                    SessionId = (string?)obj["session_id"],
                    Model = (string?)obj["model"] ?? (string?)obj["model_id"],
                    Raw = line,
                };

            case "assistant":
                var assistantContent = obj["message"]?["content"] as JArray;
                return new CliEvent
                {
                    Kind = CliEventKind.Assistant,
                    Text = JoinTextBlocks(assistantContent),
                    Todos = ExtractTodos(assistantContent),
                    Tools = ExtractTools(assistantContent),
                    SessionId = (string?)obj["session_id"],
                    Raw = line,
                };

            case "user":
                return new CliEvent { Kind = CliEventKind.UserEcho, Raw = line };

            case "stream_event":
                return new CliEvent
                {
                    Kind = CliEventKind.StreamDelta,
                    Text = DeltaText(obj["event"]),
                    Raw = line,
                };

            case "result":
                return new CliEvent
                {
                    Kind = CliEventKind.Result,
                    TotalCostUsd = (double?)obj["total_cost_usd"],
                    IsError = (bool?)obj["is_error"] ?? !string.Equals((string?)obj["subtype"], "success", StringComparison.Ordinal),
                    Text = (string?)obj["result"],
                    Raw = line,
                };

            default:
                return new CliEvent { Kind = CliEventKind.Unknown, Raw = line };
        }
    }

    // Pulls the to-do list out of a TodoWrite tool call, if this assistant turn made one.
    private static System.Collections.Generic.IReadOnlyList<TodoItem>? ExtractTodos(JArray? content)
    {
        if (content == null) return null;
        foreach (var block in content)
        {
            if ((string?)block["type"] != "tool_use" || (string?)block["name"] != "TodoWrite") continue;
            if (!(block["input"]?["todos"] is JArray todos)) continue;

            var list = new System.Collections.Generic.List<TodoItem>();
            foreach (var t in todos)
            {
                list.Add(new TodoItem
                {
                    Content = (string?)t["content"] ?? string.Empty,
                    Status = (string?)t["status"] ?? string.Empty,
                });
            }
            return list;
        }
        return null;
    }

    // Turns each tool_use block into a name plus a short summary, so the panel can show a one-line
    // chip per call ("Bash - git status", "Read - Program.cs"). The TodoWrite call is skipped - it
    // already surfaces as the live task strip, not as a chip.
    private static System.Collections.Generic.IReadOnlyList<ToolCall>? ExtractTools(JArray? content)
    {
        if (content == null) return null;
        System.Collections.Generic.List<ToolCall>? list = null;
        foreach (var block in content)
        {
            if ((string?)block["type"] != "tool_use") continue;
            string name = (string?)block["name"] ?? "tool";
            if (name == "TodoWrite") continue;
            (list ??= new System.Collections.Generic.List<ToolCall>()).Add(new ToolCall
            {
                Name = name,
                Summary = SummariseToolInput(name, block["input"]),
            });
        }
        return list;
    }

    // A readable one-liner for a tool call: the field that matters for the common tools, else the
    // first short string in the input. Always trimmed to a single line of reasonable length.
    private static string SummariseToolInput(string name, JToken? input)
    {
        if (input == null) return string.Empty;

        string[] preferred = { "command", "file_path", "path", "pattern", "url", "query", "description", "prompt" };
        foreach (string key in preferred)
        {
            var v = input[key];
            if (v != null && v.Type == JTokenType.String)
            {
                return Shorten((string?)v);
            }
        }

        foreach (var prop in ((input as JObject)?.Properties() ?? System.Linq.Enumerable.Empty<JProperty>()))
        {
            if (prop.Value.Type == JTokenType.String) return Shorten((string?)prop.Value);
        }
        return string.Empty;
    }

    private static string Shorten(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        s = s!.Replace('\r', ' ').Replace('\n', ' ').Trim();
        const int max = 120;
        return s.Length <= max ? s : s.Substring(0, max) + "...";
    }

    private static string JoinTextBlocks(JArray? content)
    {
        if (content == null) return string.Empty;
        var sb = new StringBuilder();
        foreach (var block in content)
        {
            if ((string?)block["type"] == "text")
            {
                sb.Append((string?)block["text"]);
            }
        }
        return sb.ToString();
    }

    // A partial-message delta carries its text at event.delta.text (Anthropic text_delta shape).
    private static string? DeltaText(JToken? evt)
    {
        if (evt == null) return null;
        return (string?)evt["delta"]?["text"];
    }
}
