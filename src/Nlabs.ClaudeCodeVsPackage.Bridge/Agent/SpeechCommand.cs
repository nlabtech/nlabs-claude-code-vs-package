using System;
using System.Text;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>
/// Turns the configured speech-to-text command into something runnable, and tidies what it prints.
///
/// The extension deliberately ships NO speech engine: bundling one would drag a native model and
/// megabytes of dependencies into a VSIX that is currently two assemblies, and would pick the
/// developer's transcription stack for them. Instead the developer names a command - whisper.cpp,
/// faster-whisper, a cloud CLI, anything - with an <c>{audio}</c> placeholder for the recorded file.
///
/// Pure and dependency-free, so the composition and cleanup rules are unit-tested.
/// </summary>
public static class SpeechCommand
{
    /// <summary>The placeholder the developer puts where the recorded WAV path should go.</summary>
    public const string AudioPlaceholder = "{audio}";

    /// <summary>True when a usable command has been configured.</summary>
    public static bool IsConfigured(string? template) => !string.IsNullOrWhiteSpace(template);

    /// <summary>
    /// The transcriber the extension falls back to when nothing has been configured: a few lines of
    /// Python over faster-whisper, written to the machine on first use. Still nothing bundled - it
    /// runs only if the developer already has Python and faster-whisper, and it is plain text they
    /// can read, edit or delete. Transcription stays local; no audio leaves the machine.
    /// </summary>
    public const string LocalScript = @"""""""Transcribe one audio file with faster-whisper and print the text.

Written by the Claude Code (nLabtech) Visual Studio extension the first time dictation is
used. It is yours: edit it, point it at another model, or delete it. The extension only
writes it when it is missing.
""""""
import sys


def main():
    if len(sys.argv) < 2:
        sys.stderr.write(""usage: transcribe.py <audio> [model]\n"")
        return 2

    try:
        sys.stdout.reconfigure(encoding=""utf-8"")
    except Exception:
        pass

    try:
        from faster_whisper import WhisperModel
    except ImportError:
        sys.stderr.write(""faster-whisper is not installed\n"")
        return 3

    model_name = sys.argv[2] if len(sys.argv) > 2 else ""large-v3-turbo""
    model = WhisperModel(model_name, device=""cpu"", compute_type=""int8"")
    segments, _info = model.transcribe(sys.argv[1], vad_filter=True)
    sys.stdout.write("" "".join(s.text.strip() for s in segments).strip())
    return 0


if __name__ == ""__main__"":
    sys.exit(main())
";

    /// <summary>Builds the command line for the local transcriber script.</summary>
    public static string ComposeDefault(string pythonExe, string scriptPath) =>
        "\"" + pythonExe + "\" \"" + scriptPath + "\" \"" + AudioPlaceholder + "\"";

    /// <summary>
    /// Splits the template into an executable and its arguments, with the placeholder replaced by
    /// the recording's path. A path containing spaces is quoted unless the template already quoted
    /// the placeholder itself.
    /// </summary>
    public static (string fileName, string arguments) Compose(string? template, string audioPath)
    {
        string text = (template ?? string.Empty).Trim();
        if (text.Length == 0) return (string.Empty, string.Empty);

        // Executable: a quoted path, else everything up to the first space.
        string fileName;
        int rest;
        if (text[0] == '"')
        {
            int close = text.IndexOf('"', 1);
            if (close < 0) { fileName = text.Substring(1); rest = text.Length; }
            else { fileName = text.Substring(1, close - 1); rest = close + 1; }
        }
        else
        {
            int space = text.IndexOf(' ');
            if (space < 0) { fileName = text; rest = text.Length; }
            else { fileName = text.Substring(0, space); rest = space; }
        }

        string arguments = rest >= text.Length ? string.Empty : text.Substring(rest).Trim();
        arguments = Substitute(arguments, audioPath);
        return (fileName, arguments);
    }

    // Replaces the placeholder, quoting the path only when the template did not already do so.
    private static string Substitute(string arguments, string audioPath)
    {
        string quoted = "\"" + AudioPlaceholder + "\"";
        if (arguments.IndexOf(quoted, StringComparison.Ordinal) >= 0)
        {
            return arguments.Replace(quoted, "\"" + audioPath + "\"");
        }

        string replacement = audioPath.IndexOf(' ') >= 0 ? "\"" + audioPath + "\"" : audioPath;
        return arguments.Replace(AudioPlaceholder, replacement);
    }

    /// <summary>
    /// Reduces an engine's output to the sentence to type. Blank lines and the progress/log noise
    /// most engines print on stderr-ish lines are dropped, and what remains is joined into one line -
    /// the panel is putting this into a single-line prompt, not a transcript file.
    /// </summary>
    public static string CleanTranscript(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return string.Empty;

        var builder = new StringBuilder();
        foreach (string raw in output!.Split('\n'))
        {
            string line = raw.Trim().Trim('\r').Trim();
            if (line.Length == 0) continue;
            if (LooksLikeProgress(line)) continue;
            if (builder.Length > 0) builder.Append(' ');
            builder.Append(line);
        }
        return builder.ToString().Trim();
    }

    // Progress and timing chatter, not speech: "[00:00.000 --> 00:02.000]", percentages, log levels.
    private static bool LooksLikeProgress(string line)
    {
        if (line.StartsWith("[", StringComparison.Ordinal) && line.IndexOf("-->", StringComparison.Ordinal) > 0) return true;
        if (line.EndsWith("%", StringComparison.Ordinal)) return true;
        if (line.StartsWith("INFO", StringComparison.OrdinalIgnoreCase)) return true;
        if (line.StartsWith("WARNING", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
