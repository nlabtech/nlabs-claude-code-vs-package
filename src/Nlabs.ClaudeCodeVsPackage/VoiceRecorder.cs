using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// Records the microphone to a WAV file, using Windows' own MCI interface.
///
/// This is why the VSIX is still two assemblies: MCI ships with Windows, so dictation needs no audio
/// library, no native model and nothing bundled. The capture format is fixed at 16 kHz mono 16-bit -
/// what every speech engine wants, and small enough that a minute of speech is about a megabyte.
///
/// The recording is written to the temp folder and the caller owns it; nothing is uploaded here, and
/// nothing is kept after the transcript has been read.
/// </summary>
internal sealed class VoiceRecorder : IDisposable
{
    private const string Alias = "nlabsvoice";

    [DllImport("winmm.dll", CharSet = CharSet.Auto, BestFitMapping = false)]
    private static extern int mciSendString(string command, StringBuilder? buffer, int bufferSize, IntPtr callback);

    private bool _recording;

    public bool IsRecording => _recording;

    /// <summary>Opens the capture device and starts recording. False when there is no usable input.</summary>
    public bool Start()
    {
        if (_recording) return true;

        // A previous session that died without closing would hold the alias; clearing it is harmless.
        Send("close " + Alias);

        if (Send("open new type waveaudio alias " + Alias) != 0) return false;
        Send("set " + Alias + " bitspersample 16 channels 1 samplespersec 16000 bytespersec 32000 alignment 2");

        if (Send("record " + Alias) != 0)
        {
            Send("close " + Alias);
            return false;
        }

        _recording = true;
        return true;
    }

    /// <summary>Stops recording and writes the WAV; returns its path, or null if nothing was captured.</summary>
    public string? StopAndSave()
    {
        if (!_recording) return null;
        _recording = false;

        Send("stop " + Alias);
        string path = Path.Combine(Path.GetTempPath(), "nlabs_voice_" + Guid.NewGuid().ToString("n") + ".wav");
        int saved = Send("save " + Alias + " \"" + path + "\"");
        Send("close " + Alias);

        return saved == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>Stops and discards the recording.</summary>
    public void Cancel()
    {
        if (!_recording) return;
        _recording = false;
        Send("stop " + Alias);
        Send("close " + Alias);
    }

    private static int Send(string command)
    {
        try { return mciSendString(command, null, 0, IntPtr.Zero); }
        catch { return -1; }
    }

    public void Dispose() => Cancel();
}
