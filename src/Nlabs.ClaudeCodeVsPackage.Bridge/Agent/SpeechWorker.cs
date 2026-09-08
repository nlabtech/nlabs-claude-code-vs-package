using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

/// <summary>
/// A transcriber kept loaded between dictations.
///
/// Running the speech engine once per dictation means paying for it every time: the interpreter
/// starts, the library imports, and the model is read from disk - and only then is the recording
/// transcribed, which is the short part. That wait landed entirely after the developer stopped
/// speaking, which is exactly where it is most noticeable.
///
/// So the process is started when recording begins and kept alive afterwards. The model loads while
/// the developer is still talking, and every later dictation pays for the transcription alone.
/// Anything that goes wrong here is recoverable: the caller falls back to running the engine once.
/// </summary>
public sealed class SpeechWorker : IDisposable
{
    private readonly string _fileName;
    private readonly string _arguments;
    private readonly SemaphoreSlim _turn = new SemaphoreSlim(1, 1);

    private Process? _process;
    private Task<bool>? _starting;
    private bool _disposed;
    private volatile string? _progress;
    private volatile string? _missingModel;

    public SpeechWorker(string fileName, string arguments)
    {
        _fileName = fileName;
        _arguments = arguments;
    }

    /// <summary>
    /// The engine's most recent progress line. While a model is downloading this is the library's
    /// own percentage, which is the difference between "it is working" and "it has hung".
    /// </summary>
    public string? Progress => _progress;

    /// <summary>
    /// The model the engine asked for but does not have, once it has said so. Non-null means the
    /// worker stopped on purpose and is waiting to be asked again with permission to download.
    /// </summary>
    public string? MissingModel => _missingModel;

    /// <summary>True once the engine has answered that it is loaded and waiting for work.</summary>
    public bool IsReady => _starting != null && _starting.IsCompleted && _starting.Result && Alive;

    private bool Alive
    {
        get
        {
            try { return _process != null && !_process.HasExited; }
            catch { return false; }
        }
    }

    /// <summary>
    /// Starts the engine if it is not already running, without waiting for it. Call this when
    /// recording starts so the model loads over the top of the developer speaking.
    /// </summary>
    public void BeginWarmUp(int readyTimeoutMs = 180000)
    {
        if (_disposed) return;
        if (_starting != null && (!_starting.IsCompleted || (_starting.Result && Alive))) return;

        _starting = StartAsync(readyTimeoutMs);
    }

    private async Task<bool> StartAsync(int readyTimeoutMs)
    {
        Shutdown();
        _missingModel = null;
        _progress = null;

        try
        {
            var info = new ProcessStartInfo(_fileName, _arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false),
            };

            var process = Process.Start(info);
            if (process == null) return false;
            _process = process;

            // Drain stderr so a full pipe buffer can never wedge the process - and keep the last
            // line, because that is where the download library writes its percentage. The bar uses
            // carriage returns, which ReadLine already treats as line ends, so each update arrives.
            _ = Task.Run(async () =>
            {
                try
                {
                    string? noise;
                    while ((noise = await process.StandardError.ReadLineAsync().ConfigureAwait(false)) != null)
                    {
                        noise = noise.Trim();
                        if (noise.Length > 0) _progress = noise;
                    }
                }
                catch { }
            });

            string? line = await ReadLineAsync(process, readyTimeoutMs).ConfigureAwait(false);
            while (line != null && !line.StartsWith(SpeechCommand.ReadyMarker, StringComparison.Ordinal))
            {
                if (line.StartsWith(SpeechCommand.NeedMarker, StringComparison.Ordinal))
                {
                    _missingModel = line.Substring(SpeechCommand.NeedMarker.Length).Trim();
                    return false;
                }

                if (line.StartsWith(SpeechCommand.ErrorMarker, StringComparison.Ordinal)) return false;
                line = await ReadLineAsync(process, readyTimeoutMs).ConfigureAwait(false);
            }

            return line != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Transcribes one recording, waiting for the engine to finish loading if it still is. Returns
    /// null when the worker cannot answer - the caller should run the engine the one-shot way then,
    /// rather than telling the developer dictation is broken.
    /// </summary>
    public async Task<string?> TranscribeAsync(string audioPath, int timeoutMs)
    {
        if (_disposed) return null;

        await _turn.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_starting == null) BeginWarmUp();
            bool ready = _starting != null && await _starting.ConfigureAwait(false);
            if (!ready || !Alive) return null;

            Process process = _process!;
            try
            {
                await process.StandardInput.WriteLineAsync(audioPath).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch
            {
                Shutdown();
                return null;
            }

            var text = new StringBuilder();
            while (true)
            {
                string? line = await ReadLineAsync(process, timeoutMs).ConfigureAwait(false);
                if (line == null)
                {
                    // Timed out or the engine died mid-answer; it cannot be trusted to be in step
                    // with us any more, so drop it and let the next dictation start a fresh one.
                    Shutdown();
                    return null;
                }

                if (line.StartsWith(SpeechCommand.EndMarker, StringComparison.Ordinal)) break;
                if (line.StartsWith(SpeechCommand.ErrorMarker, StringComparison.Ordinal))
                {
                    await DrainToEndAsync(process, timeoutMs).ConfigureAwait(false);
                    return null;
                }

                if (text.Length > 0) text.Append(' ');
                text.Append(line.Trim());
            }

            return text.ToString().Trim();
        }
        catch
        {
            Shutdown();
            return null;
        }
        finally
        {
            _turn.Release();
        }
    }

    // Reads the rest of an answer we are not going to use, so the next one starts at a line boundary.
    private static async Task DrainToEndAsync(Process process, int timeoutMs)
    {
        for (int i = 0; i < 100; i++)
        {
            string? line = await ReadLineAsync(process, timeoutMs).ConfigureAwait(false);
            if (line == null || line.StartsWith(SpeechCommand.EndMarker, StringComparison.Ordinal)) return;
        }
    }

    // ReadLineAsync with a deadline: the framework's has no cancellation, so race it against a timer.
    private static async Task<string?> ReadLineAsync(Process process, int timeoutMs)
    {
        Task<string?> read = process.StandardOutput.ReadLineAsync()!;
        Task done = await Task.WhenAny(read, Task.Delay(timeoutMs)).ConfigureAwait(false);
        return done == read ? await read.ConfigureAwait(false) : null;
    }

    private void Shutdown()
    {
        Process? process = _process;
        _process = null;
        if (process == null) return;

        // Closing stdin ends the script's read loop; kill anything that ignores the hint.
        try { process.StandardInput.Close(); } catch { }
        try { if (!process.WaitForExit(1500)) process.Kill(); } catch { }
        try { process.Dispose(); } catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        Shutdown();
        _turn.Dispose();
    }
}
