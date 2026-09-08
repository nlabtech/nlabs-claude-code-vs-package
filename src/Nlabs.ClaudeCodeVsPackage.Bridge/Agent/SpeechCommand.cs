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
    public const string LocalScript = @"""""""Transcribe audio with faster-whisper.

Written by the Claude Code (nLabtech) Visual Studio extension the first time dictation is
used. It is yours: edit it, point it at another model, or delete it. The extension only
writes it when it is missing.

Two modes:
  transcribe.py <audio> [model]  print the text for one file, then exit
  transcribe.py --serve [model]  load the model once, print <<<READY>>>, then read one
                                 audio path per line from stdin and answer with the text
                                 followed by <<<END>>>

The extension uses --serve so the model loads while you are still speaking instead of
after you stop, and stays loaded for the next time. Loading it is most of the wait.
""""""
import sys

READY = ""<<<READY>>>""
END = ""<<<END>>>""
ERR = ""<<<ERR>>>""
NEED = ""<<<NEED>>>""
DEFAULT_MODEL = ""large-v3-turbo""


def pick_device():
    """"""Prefer the GPU. The same model is several times faster on it, for the same words.""""""
    try:
        import ctranslate2

        if ctranslate2.get_cuda_device_count() > 0:
            return ""cuda"", ""float16""
    except Exception:
        pass
    return ""cpu"", ""int8""


def load(model_name, allow_download):
    from faster_whisper import WhisperModel

    device, compute = pick_device()
    return WhisperModel(
        model_name,
        device=device,
        compute_type=compute,
        local_files_only=not allow_download,
    )


def text_of(model, path):
    segments, _info = model.transcribe(path, vad_filter=True)
    return "" "".join(s.text.strip() for s in segments).strip()


def say(line):
    sys.stdout.write(line + ""\n"")
    sys.stdout.flush()


def serve(model_name, allow_download):
    try:
        model = load(model_name, allow_download)
    except Exception as exc:
        # Nothing cached and we were not allowed to fetch it: say what is missing rather than
        # downloading a gigabyte behind the developer's back.
        if not allow_download:
            say(NEED + "" "" + model_name)
            return 4
        say(ERR + "" "" + str(exc))
        return 3

    say(READY)
    for line in sys.stdin:
        path = line.strip()
        if not path:
            continue
        try:
            say(text_of(model, path))
        except Exception as exc:
            say(ERR + "" "" + str(exc))
        say(END)
    return 0


def main():
    try:
        sys.stdout.reconfigure(encoding=""utf-8"")
    except Exception:
        pass

    args = sys.argv[1:]
    allow_download = ""--allow-download"" in args
    args = [a for a in args if a != ""--allow-download""]

    if not args:
        sys.stderr.write(""usage: transcribe.py <audio>|--serve [model] [--allow-download]\n"")
        return 2

    model_name = args[1] if len(args) > 1 else DEFAULT_MODEL
    if args[0] == ""--serve"":
        return serve(model_name, allow_download)

    try:
        model = load(model_name, True)
    except ImportError:
        sys.stderr.write(""faster-whisper is not installed\n"")
        return 3

    sys.stdout.write(text_of(model, args[0]))
    return 0


if __name__ == ""__main__"":
    sys.exit(main())
";

    /// <summary>Printed by the script once the model is loaded and it can accept work.</summary>
    public const string ReadyMarker = "<<<READY>>>";

    /// <summary>Printed after each transcript, so the reader knows the answer is complete.</summary>
    public const string EndMarker = "<<<END>>>";

    /// <summary>Prefix of a line carrying a failure rather than a transcript.</summary>
    public const string ErrorMarker = "<<<ERR>>>";

    /// <summary>
    /// Printed instead of <see cref="ReadyMarker"/> when the model is not on the machine yet. The
    /// panel then asks before fetching it: it is about a gigabyte and a half, and a download nobody
    /// agreed to - with no sign it is happening - is indistinguishable from the feature hanging.
    /// </summary>
    public const string NeedMarker = "<<<NEED>>>";

    /// <summary>The flag that lets the script fetch a model it does not have.</summary>
    public const string AllowDownloadFlag = "--allow-download";

    /// <summary>
    /// The opening line of the one-shot-only script shipped before serve mode existed. A file that
    /// still starts with it is our own earlier output, so replacing it loses nobody's work - while
    /// anything else on disk is the developer's and is left alone.
    /// </summary>
    public const string LegacyScriptOpening = "\"\"\"Transcribe one audio file with faster-whisper and print the text.";

    /// <summary>Builds the command line for the local transcriber script.</summary>
    public static string ComposeDefault(string pythonExe, string scriptPath) =>
        "\"" + pythonExe + "\" \"" + scriptPath + "\" \"" + AudioPlaceholder + "\"";

    /// <summary>The arguments that start the local script as a warm, long-lived transcriber.</summary>
    public static string ComposeServe(string scriptPath, bool allowDownload = false) =>
        "\"" + scriptPath + "\" --serve" + (allowDownload ? " " + AllowDownloadFlag : string.Empty);

    /// <summary>Whether a script on disk speaks the serve protocol; if not, one-shot still works.</summary>
    public static bool SupportsServe(string? scriptText) =>
        scriptText != null && scriptText.IndexOf(ReadyMarker, StringComparison.Ordinal) >= 0;

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
