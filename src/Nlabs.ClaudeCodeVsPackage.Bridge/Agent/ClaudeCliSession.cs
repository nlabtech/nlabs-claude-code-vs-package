using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>Options for one headless Claude Code session.</summary>
public sealed class ClaudeCliOptions
{
    /// <summary>Model alias, e.g. "opus" or a full model id. Null keeps the CLI default.</summary>
    public string? Model { get; set; }
    /// <summary>Permission mode, e.g. "acceptEdits". Null keeps the CLI default.</summary>
    public string? PermissionMode { get; set; }
    /// <summary>Extra system-prompt text appended to Claude's own.</summary>
    public string? AppendSystemPrompt { get; set; }
    /// <summary>Continue the most recent session in this directory.</summary>
    public bool Continue { get; set; }
    /// <summary>Resume a specific session id.</summary>
    public string? Resume { get; set; }
    /// <summary>Emit partial-message deltas so text streams as it is produced.</summary>
    public bool IncludePartialMessages { get; set; } = true;

    /// <summary>Path to a settings JSON file (the permission floor), passed with --settings.</summary>
    public string? SettingsPath { get; set; }
}

/// <summary>
/// Drives Claude Code headlessly for the agentic panel: it launches
/// <c>claude -p --input-format stream-json --output-format stream-json</c>, writes each user
/// turn to stdin and turns every stdout line into a <see cref="CliEvent"/> the panel renders.
///
/// It uses the developer's own Claude subscription through the CLI - the extension never holds
/// an API key, and nothing about the machine or account is written to a log here. The two parts
/// with real logic - building the argument line and pumping the output stream - take no process,
/// so they are unit-tested; <see cref="Start"/> and <see cref="SendAsync"/> are thin wrappers
/// over a real process.
/// </summary>
public sealed class ClaudeCliSession : IDisposable
{
    private Process? _process;
    private TextWriter? _stdin;
    private bool _disposed;

    /// <summary>Raised for every parsed line of the CLI's output.</summary>
    public event EventHandler<CliEvent>? Event;

    /// <summary>Raised when the CLI process exits.</summary>
    public event EventHandler? Exited;

    /// <summary>Launches the CLI in <paramref name="workingDirectory"/> with the given options.</summary>
    public void Start(string workingDirectory, ClaudeCliOptions options)
    {
        var (fileName, arguments) = ComposeStart(ResolveExecutable(), BuildArguments(options));
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory ?? string.Empty,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.Exited += (_, __) => Exited?.Invoke(this, EventArgs.Empty);
        _process.Start();
        _stdin = _process.StandardInput;

        Process process = _process;
        _ = Task.Run(() => Pump(process.StandardOutput));
    }

    /// <summary>Sends one user turn to the running session.</summary>
    public async Task SendAsync(string text)
    {
        TextWriter? stdin = _stdin;
        if (stdin == null) return;
        await stdin.WriteLineAsync(CliStreamProtocol.UserMessage(text)).ConfigureAwait(false);
        await stdin.FlushAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the output stream line by line and raises <see cref="Event"/> for each. Exposed so a
    /// test can drive it with any reader; production passes the process's standard output.
    /// </summary>
    public void Pump(TextReader reader)
    {
        if (reader == null) return;
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            Event?.Invoke(this, CliStreamProtocol.Parse(line));
        }
    }

    /// <summary>
    /// Composes the process file name and argument line. On Windows `claude` is usually an npm
    /// shim (claude.cmd), which the raw CreateProcess path cannot launch, so a resolved .exe runs
    /// directly, a .cmd/.bat runs through cmd.exe, and an unresolved command falls back to cmd.exe
    /// (which searches PATH and knows the shim). Kept pure so the composition is unit-tested.
    /// </summary>
    public static (string fileName, string arguments) ComposeStart(string? resolvedExecutable, string cliArguments)
    {
        if (string.IsNullOrEmpty(resolvedExecutable))
        {
            return ("cmd.exe", "/d /s /c claude " + cliArguments);
        }
        if (resolvedExecutable!.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (resolvedExecutable, cliArguments);
        }
        return ("cmd.exe", "/d /s /c \"" + resolvedExecutable + "\" " + cliArguments);
    }

    // Best-effort location of the claude executable: an explicit override, then PATH, then the
    // npm global directory. Returns null to let cmd.exe resolve it from PATH.
    private static string? ResolveExecutable()
    {
        string? overridePath = Environment.GetEnvironmentVariable("NLABS_CLAUDE_PATH");
        if (!string.IsNullOrEmpty(overridePath) && File.Exists(overridePath)) return overridePath;

        string[] names = { "claude.exe", "claude.cmd", "claude.bat" };

        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (string dir in path.Split(';'))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            foreach (string name in names)
            {
                try
                {
                    string candidate = Path.Combine(dir.Trim(), name);
                    if (File.Exists(candidate)) return candidate;
                }
                catch { /* an invalid PATH entry - skip */ }
            }
        }

        string? appData = Environment.GetEnvironmentVariable("APPDATA");
        if (!string.IsNullOrEmpty(appData))
        {
            foreach (string name in names)
            {
                string candidate = Path.Combine(appData!, "npm", name);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    /// <summary>Builds the CLI argument line for the given options.</summary>
    public static string BuildArguments(ClaudeCliOptions options)
    {
        var sb = new StringBuilder("-p --input-format stream-json --output-format stream-json --verbose");

        if (options == null) return sb.ToString();

        if (options.IncludePartialMessages) sb.Append(" --include-partial-messages");
        if (!string.IsNullOrEmpty(options.Model)) sb.Append(" --model ").Append(options.Model);
        if (!string.IsNullOrEmpty(options.PermissionMode)) sb.Append(" --permission-mode ").Append(options.PermissionMode);
        if (options.Continue) sb.Append(" --continue");
        if (!string.IsNullOrEmpty(options.Resume)) sb.Append(" --resume ").Append(options.Resume);
        if (!string.IsNullOrEmpty(options.SettingsPath)) sb.Append(" --settings ").Append(Quote(options.SettingsPath!));
        if (!string.IsNullOrEmpty(options.AppendSystemPrompt))
        {
            sb.Append(" --append-system-prompt ").Append(Quote(options.AppendSystemPrompt!));
        }

        return sb.ToString();
    }

    // Wraps a value in double quotes, escaping embedded quotes and backslashes for the Windows
    // argument parser. Used only for free-text options (the system prompt).
    private static string Quote(string value)
    {
        // CommandLineToArgvW rules: a backslash is literal unless it precedes a quote, so only a
        // run of backslashes that meets a quote (or the closing quote) is doubled. Doubling every
        // backslash would corrupt Windows paths (C:\tmp -> C:\\tmp).
        var sb = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                slashes++;
            }
            else if (c == '"')
            {
                sb.Append('\\', slashes * 2 + 1);
                sb.Append('"');
                slashes = 0;
            }
            else
            {
                sb.Append('\\', slashes);
                sb.Append(c == '\n' || c == '\r' ? ' ' : c);
                slashes = 0;
            }
        }
        sb.Append('\\', slashes * 2);
        sb.Append('"');
        return sb.ToString();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stdin?.Dispose(); } catch { }
        try
        {
            if (_process != null && !_process.HasExited) _process.Kill();
        }
        catch { /* already gone */ }
        try { _process?.Dispose(); } catch { }
    }
}
