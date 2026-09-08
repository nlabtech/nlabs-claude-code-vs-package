using Microsoft.VisualStudio.Shell;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using System.ComponentModel;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// The extension's settings, as Visual Studio's own Tools &gt; Options page.
///
/// It deliberately carries only what the panel cannot: the panel already owns the per-conversation
/// choices (model, permission mode, effort, language, accent) and persists them itself, so
/// duplicating them here would create two sources of truth. What belongs here is machine-level and
/// rarely changed - where the CLI lives, whether output is redacted, whether the IDE bridge starts.
/// </summary>
internal sealed class ExtensionOptionsPage : DialogPage
{
    private string _claudeCliPath = string.Empty;
    private string _speechToTextCommand = string.Empty;
    private bool _maskSecrets = true;
    private bool _startBridgeOnLoad = true;

    [Category("Claude Code")]
    [DisplayName("Claude CLI path")]
    [Description("Full path to the claude executable. Leave empty to find it on PATH (the usual case).")]
    public string ClaudeCliPath
    {
        get => _claudeCliPath;
        set { _claudeCliPath = value ?? string.Empty; Apply(); }
    }

    [Category("Claude Code")]
    [DisplayName("Mask secrets in the panel")]
    [Description("Redact API keys, tokens and passwords from what the panel displays. Recommended: what Claude echoes back can end up in a screenshot.")]
    public bool MaskSecrets
    {
        get => _maskSecrets;
        set { _maskSecrets = value; Apply(); }
    }

    [Category("Claude Code")]
    [DisplayName("Speech-to-text command")]
    [Description("Command that transcribes a WAV file and prints the text, with {audio} where the file path goes - for example: whisper-cli -m model.bin -f \"{audio}\". Leave empty to disable dictation. No speech engine is bundled: you choose the one you trust.")]
    public string SpeechToTextCommand
    {
        get => _speechToTextCommand;
        set { _speechToTextCommand = value ?? string.Empty; Apply(); }
    }

    [Category("Claude Code")]
    [DisplayName("Start the local bridge on load")]
    [Description("Advertise Visual Studio to the Claude CLI so its /ide command can connect. Turn off to keep the extension panel-only.")]
    public bool StartBridgeOnLoad
    {
        get => _startBridgeOnLoad;
        set { _startBridgeOnLoad = value; Apply(); }
    }

    public override void LoadSettingsFromStorage()
    {
        base.LoadSettingsFromStorage();
        Apply();
    }

    protected override void OnApply(PageApplyEventArgs e)
    {
        base.OnApply(e);
        Apply();
    }

    private void Apply() =>
        ExtensionOptions.Update(_claudeCliPath, _speechToTextCommand, _maskSecrets, _startBridgeOnLoad);
}

/// <summary>
/// The current settings, readable from anywhere in the extension without reaching for the shell.
/// The options page is the only writer; everything else reads. Defaults are the safe ones, so the
/// extension behaves correctly before the page has ever been opened.
/// </summary>
internal static class ExtensionOptions
{
    public static string ClaudeCliPath { get; private set; } = string.Empty;
    public static string SpeechToTextCommand { get; private set; } = string.Empty;
    public static bool MaskSecrets { get; private set; } = true;
    public static bool StartBridgeOnLoad { get; private set; } = true;

    public static void Update(string? claudeCliPath, string? speechToTextCommand, bool maskSecrets, bool startBridgeOnLoad)
    {
        ClaudeCliPath = claudeCliPath ?? string.Empty;
        SpeechToTextCommand = speechToTextCommand ?? string.Empty;
        MaskSecrets = maskSecrets;
        StartBridgeOnLoad = startBridgeOnLoad;

        // The session resolves the executable itself; hand it the override rather than having it
        // reach back into Visual Studio for a setting.
        ClaudeCliSession.ExecutableOverride =
            string.IsNullOrWhiteSpace(ClaudeCliPath) ? null : ClaudeCliPath;
    }

    /// <summary>Redacts text for display when masking is on; returns it unchanged when off.</summary>
    public static string ForDisplay(string? text) =>
        MaskSecrets ? SecretMasker.Redact(text) : text ?? string.Empty;
}
