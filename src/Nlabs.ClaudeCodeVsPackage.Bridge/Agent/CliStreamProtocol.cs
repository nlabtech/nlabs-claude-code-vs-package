using System;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent
{
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
        public static string UserMessage(string text)
        {
            var message = new JObject
            {
                ["type"] = "user",
                ["message"] = new JObject
                {
                    ["role"] = "user",
                    ["content"] = new JArray
                    {
                        new JObject { ["type"] = "text", ["text"] = text ?? string.Empty },
                    },
                },
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
                    return new CliEvent
                    {
                        Kind = CliEventKind.Assistant,
                        Text = JoinTextBlocks(obj["message"]?["content"] as JArray),
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
}
