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
    /// <summary>A subscription usage update (rate_limit_event).</summary>
    RateLimit,
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
    /// <summary>The CLI's id for this call; later lines reference it as their parent.</summary>
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;

    /// <summary>For a Task call, the subagent it delegates to; null for every other tool.</summary>
    public string? Subagent { get; set; }
}

/// <summary>One subscription usage window (5-hour, 7-day, ...) and how full it is.</summary>
public sealed class RateLimitWindow
{
    /// <summary>The CLI's key: <c>five_hour</c>, <c>seven_day</c>, <c>seven_day_opus</c>, ...</summary>
    public string Name { get; set; } = string.Empty;
    /// <summary>Fill fraction, 0..1.</summary>
    public double Utilization { get; set; }
    /// <summary>When the window resets; null when the CLI did not say.</summary>
    public DateTimeOffset? ResetsAt { get; set; }
}

/// <summary>
/// Subscription usage, from the CLI's <c>rate_limit_event</c>. The turn's own
/// <c>result.total_cost_usd</c> only says what this turn spent - it never answers "how much of my
/// limit is left", which otherwise means dropping to a terminal to check. Windows are read
/// generically: whatever sits under <c>unifiedWindows</c> is shown, so a new window the CLI adds
/// later still surfaces (its key stands in when there is no friendly name).
/// </summary>
public sealed class RateLimitStatus
{
    /// <summary><c>allowed</c>, <c>allowed_warning</c> or <c>rejected</c>.</summary>
    public string Status { get; set; } = string.Empty;
    public System.Collections.Generic.IReadOnlyList<RateLimitWindow> Windows { get; set; }
        = System.Array.Empty<RateLimitWindow>();

    /// <summary>True when usage is near or past a limit (anything other than plain <c>allowed</c>).</summary>
    public bool Warning => !string.Equals(Status, "allowed", StringComparison.OrdinalIgnoreCase);

    /// <summary>The fullest window - the one a single-line summary is built from.</summary>
    public RateLimitWindow? MostFull
    {
        get
        {
            RateLimitWindow? top = null;
            foreach (RateLimitWindow w in Windows)
            {
                if (top == null || w.Utilization > top.Utilization) top = w;
            }
            return top;
        }
    }
}

/// <summary>
/// What one turn consumed, as the CLI reports it when the turn ends.
///
/// Cached input is kept apart from fresh input on purpose: on a long conversation it is most of the
/// traffic and a fraction of the price, so folding it into one "tokens" figure would make every turn
/// look far more expensive than it was.
/// </summary>
public sealed class TurnUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CacheReadTokens { get; set; }
    public int CacheWriteTokens { get; set; }
    /// <summary>Of the output tokens, how many were reasoning.</summary>
    public int ThinkingTokens { get; set; }
    /// <summary>Wall-clock time the turn took, in milliseconds; 0 when the CLI did not say.</summary>
    public int DurationMs { get; set; }

    /// <summary>Everything billed as output plus everything billed as fresh input.</summary>
    public int BilledTokens => InputTokens + OutputTokens + CacheWriteTokens;
}

/// <summary>One parsed event from the CLI's stream-json output.</summary>
public sealed class CliEvent
{
    public CliEventKind Kind { get; set; }
    public string? Text { get; set; }

    /// <summary>
    /// Claude's reasoning, when the effort setting produced any. Kept apart from <see cref="Text"/>
    /// because it is not the answer: it is how the answer was reached, and showing the two as one
    /// paragraph would put working-out into a reply the developer is meant to act on.
    /// </summary>
    public string? Thinking { get; set; }

    public string? SessionId { get; set; }
    public string? Model { get; set; }
    public double? TotalCostUsd { get; set; }
    public bool IsError { get; set; }
    public string Raw { get; set; } = string.Empty;

    /// <summary>Running output-token count from a streaming message_delta; null when the line carries none.</summary>
    public int? OutputTokens { get; set; }

    /// <summary>Running estimate of reasoning tokens spent so far in this turn; null on other lines.</summary>
    public int? ThinkingTokens { get; set; }

    /// <summary>What the finished turn consumed; null except on a result line.</summary>
    public TurnUsage? Usage { get; set; }

    /// <summary>The agent's to-do list when this assistant turn wrote one; null otherwise.</summary>
    public System.Collections.Generic.IReadOnlyList<TodoItem>? Todos { get; set; }

    /// <summary>The tools this assistant turn invoked, in order; null when it called none.</summary>
    public System.Collections.Generic.IReadOnlyList<ToolCall>? Tools { get; set; }

    /// <summary>Subscription usage when this line was a rate_limit_event; null otherwise.</summary>
    public RateLimitStatus? RateLimit { get; set; }

    /// <summary>
    /// The Task call this line belongs to, when it came from a subagent rather than the main turn.
    /// Null for the main turn - which is what lets the panel keep a delegated turn's work visibly
    /// separate instead of interleaving it with the answer the developer asked for.
    /// </summary>
    public string? ParentToolUseId { get; set; }
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
                // While Claude reasons, the CLI reports a running estimate of what that reasoning is
                // costing. It is the only token figure available before the turn ends, and without it
                // a long think is spend the developer cannot see happening.
                if ((string?)obj["subtype"] == "thinking_tokens")
                {
                    return new CliEvent
                    {
                        Kind = CliEventKind.SystemInit,
                        ThinkingTokens = (int?)obj["estimated_tokens"],
                        SessionId = (string?)obj["session_id"],
                        Raw = line,
                    };
                }

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
                    Thinking = JoinThinkingBlocks(assistantContent),
                    Todos = ExtractTodos(assistantContent),
                    Tools = ExtractTools(assistantContent),
                    SessionId = (string?)obj["session_id"],
                    ParentToolUseId = (string?)obj["parent_tool_use_id"],
                    Raw = line,
                };

            case "user":
                return new CliEvent { Kind = CliEventKind.UserEcho, Raw = line };

            case "stream_event":
                return new CliEvent
                {
                    Kind = CliEventKind.StreamDelta,
                    Text = DeltaText(obj["event"]),
                    Thinking = DeltaThinking(obj["event"]),
                    OutputTokens = (int?)obj["event"]?["usage"]?["output_tokens"],
                    ParentToolUseId = (string?)obj["parent_tool_use_id"],
                    Raw = line,
                };

            case "result":
                return new CliEvent
                {
                    Kind = CliEventKind.Result,
                    TotalCostUsd = (double?)obj["total_cost_usd"],
                    IsError = (bool?)obj["is_error"] ?? !string.Equals((string?)obj["subtype"], "success", StringComparison.Ordinal),
                    Text = (string?)obj["result"],
                    Usage = ParseTurnUsage(obj),
                    Raw = line,
                };

            case "rate_limit_event":
                return new CliEvent
                {
                    Kind = CliEventKind.RateLimit,
                    RateLimit = ParseRateLimit(obj["rate_limit_info"] as JObject),
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
                Id = (string?)block["id"] ?? string.Empty,
                Name = name,
                // A Task call names the subagent it is handing work to. Everything the CLI reports
                // under this call's id afterwards belongs to that subagent, which is how the panel
                // can say whose work it is showing rather than blurring it into the main turn.
                Subagent = name == "Task" ? (string?)block["input"]?["subagent_type"] : null,
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

    // Reads a rate_limit_info body into a RateLimitStatus. Windows are read generically from
    // unifiedWindows so an unknown/new window still shows; an unrecognised shape yields null.
    private static RateLimitStatus? ParseRateLimit(JObject? info)
    {
        if (info == null) return null;

        var windows = new System.Collections.Generic.List<RateLimitWindow>();
        if (info["unifiedWindows"] is JObject unified)
        {
            foreach (JProperty prop in unified.Properties())
            {
                if (!(prop.Value is JObject w)) continue;
                double? util = (double?)w["utilization"];
                if (util == null) continue;
                windows.Add(new RateLimitWindow
                {
                    Name = prop.Name,
                    Utilization = util.Value,
                    ResetsAt = ParseReset(w["resetsAt"]),
                });
            }
        }
        if (windows.Count == 0 && info["status"] == null) return null;

        return new RateLimitStatus
        {
            Status = (string?)info["status"] ?? string.Empty,
            Windows = windows,
        };
    }

    // resetsAt may arrive as an ISO-8601 string or an epoch number (seconds or milliseconds).
    private static DateTimeOffset? ParseReset(JToken? token)
    {
        if (token == null || token.Type == JTokenType.Null) return null;
        try
        {
            if (token.Type == JTokenType.Date) return (DateTimeOffset)token;
            if (token.Type == JTokenType.Integer || token.Type == JTokenType.Float)
            {
                double n = (double)token;
                // Heuristic: > 1e12 is milliseconds, otherwise seconds.
                return n > 1e12
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)n)
                    : DateTimeOffset.FromUnixTimeSeconds((long)n);
            }
            string? s = (string?)token;
            if (!string.IsNullOrEmpty(s) && DateTimeOffset.TryParse(s, out DateTimeOffset dto)) return dto;
        }
        catch { /* unrecognised time - leave null */ }
        return null;
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

    // The usage block of a result line. Absent or partial input is normal - an errored turn reports
    // less - so every field falls back to zero rather than making the whole reading unavailable.
    private static TurnUsage? ParseTurnUsage(JObject obj)
    {
        var usage = obj["usage"] as JObject;
        if (usage == null) return null;

        return new TurnUsage
        {
            InputTokens = (int?)usage["input_tokens"] ?? 0,
            OutputTokens = (int?)usage["output_tokens"] ?? 0,
            CacheReadTokens = (int?)usage["cache_read_input_tokens"] ?? 0,
            CacheWriteTokens = (int?)usage["cache_creation_input_tokens"] ?? 0,
            ThinkingTokens = (int?)usage["output_tokens_details"]?["thinking_tokens"] ?? 0,
            DurationMs = (int?)obj["duration_ms"] ?? (int?)obj["duration_api_ms"] ?? 0,
        };
    }

    // Reasoning arrives on its own channel: a thinking_delta puts it at event.delta.thinking, never
    // at .text. Reading only .text is why it used to vanish - correctly kept out of the reply, but
    // then thrown away instead of shown as what it is.
    private static string? DeltaThinking(JToken? evt)
    {
        if (evt == null) return null;
        return (string?)evt["delta"]?["thinking"];
    }

    // The completed reasoning blocks of an assistant message, which arrive after their deltas and
    // are authoritative.
    private static string? JoinThinkingBlocks(JArray? content)
    {
        if (content == null) return null;
        StringBuilder? sb = null;
        foreach (var block in content)
        {
            if ((string?)block["type"] != "thinking") continue;
            (sb ??= new StringBuilder()).Append((string?)block["thinking"]);
        }
        return sb?.ToString();
    }
}
