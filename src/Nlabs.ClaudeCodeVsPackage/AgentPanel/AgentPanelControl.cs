using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;

namespace Nlabs.ClaudeCodeVsPackage.AgentPanel;

/// <summary>
/// The agentic panel: a chat surface that drives Claude Code headlessly through
/// <see cref="ClaudeCliSession"/>. The developer types a turn, the panel spawns (or reuses) a
/// <c>claude -p</c> session in the open solution's directory, streams the reply as it arrives and
/// shows the turn's cost.
///
/// It is built in plain WPF (no embedded browser): a Visual Studio tool window is WPF already, so
/// this adds no bundled runtime and cannot clash with the shell's own components - the panel simply
/// paints with the current theme's brushes. Rich markdown/code rendering is a later enhancement.
///
/// The extension never holds an API key: the session runs on the developer's own Claude
/// subscription through the CLI. Nothing about the machine or account is shown or logged here.
/// </summary>
internal sealed class AgentPanelControl : UserControl
{
    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly TextBlock _status;
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _modeCombo;

    private ClaudeCliSession? _session;
    private TextBlock? _streamingReply;
    private string _streamingText = string.Empty;
    private bool _busy;

    public AgentPanelControl()
    {
        this.SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        this.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _messages = new StackPanel { Margin = new Thickness(8) };
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _messages,
        };

        _status = new TextBlock
        {
            Margin = new Thickness(10, 4, 10, 4),
            Opacity = 0.7,
            Text = "Claude Code (nLabtech) - type a message and press Enter.",
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 1,
            MaxLines = 6,
            Margin = new Thickness(8),
            Padding = new Thickness(6),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        _input.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        _input.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _input.PreviewKeyDown += OnInputKeyDown;

        _modelCombo = MakeCombo(new (string, string?)[] { ("Default model", null), ("Opus", "opus"), ("Sonnet", "sonnet") });
        _modeCombo = MakeCombo(new (string, string?)[] { ("Ask each time", null), ("Accept edits", "acceptEdits") });

        var newButton = new Button
        {
            Content = "New session",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 10, 0),
        };
        newButton.Click += (_, __) => NewSession();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 8, 8, 0),
        };
        toolbar.Children.Add(newButton);
        toolbar.Children.Add(LabelFor("Model", _modelCombo));
        toolbar.Children.Add(LabelFor("Permission", _modeCombo));

        var root = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        DockPanel.SetDock(_status, Dock.Top);
        DockPanel.SetDock(_input, Dock.Bottom);
        root.Children.Add(toolbar);
        root.Children.Add(_status);
        root.Children.Add(_input);
        root.Children.Add(_scroller);
        Content = root;
    }

    // Enter sends; Shift+Enter inserts a newline.
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async System.Threading.Tasks.Task SendAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // establish the UI thread
        string text = _input.Text?.Trim() ?? string.Empty;
        if (text.Length == 0 || _busy) return; // one turn at a time

        _input.Clear();
        AddBubble(text, isUser: true);

        try
        {
            EnsureSession();
            _streamingText = string.Empty;
            _streamingReply = AddBubble(string.Empty, isUser: false);
            SetBusy(true);
            await _session!.SendAsync(text);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = "Could not reach the Claude CLI: " + ex.Message;
        }
    }

    // Creates the CLI session the first time, in the open solution's directory.
    private void EnsureSession()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_session != null) return;

        _session = new ClaudeCliSession();
        _session.Event += OnCliEvent;
        _session.Exited += (_, __) => OnUi(() => { SetBusy(false); _status.Text = "Session ended."; });
        ClaudeCliOptions options = CurrentOptions();
        options.SettingsPath = WriteSafetySettings();
        _session.Start(SolutionDirectory(), options);
        _status.Text = "Connected.";
    }

    // CLI events arrive on the pump thread; marshal every UI change to the dispatcher.
    private void OnCliEvent(object sender, CliEvent e)
    {
        switch (e.Kind)
        {
            case CliEventKind.SystemInit:
                if (!string.IsNullOrEmpty(e.Model)) OnUi(() => _status.Text = "Model: " + e.Model);
                break;

            case CliEventKind.StreamDelta:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText += e.Text;
                    OnUi(() => { if (_streamingReply != null) _streamingReply.Text = _streamingText; ScrollToEnd(); });
                }
                break;

            case CliEventKind.Assistant:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText = e.Text!;
                    OnUi(() => { if (_streamingReply != null) _streamingReply.Text = _streamingText; ScrollToEnd(); });
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    _streamingReply = null;
                    SetBusy(false);
                    if (e.TotalCostUsd.HasValue)
                    {
                        _status.Text = e.IsError
                            ? "Turn failed."
                            : string.Format("Ready - last turn ${0:0.0000}.", e.TotalCostUsd.Value);
                    }
                });
                break;
        }
    }

    private TextBlock AddBubble(string text, bool isUser)
    {
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 6, 10, 6),
        };
        content.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var bubble = new Border
        {
            Child = content,
            CornerRadius = new System.Windows.CornerRadius(8),
            Margin = new Thickness(0, 4, 0, 4),
            MaxWidth = 620,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        bubble.SetResourceReference(Border.BackgroundProperty,
            isUser ? VsBrushes.ComboBoxBackgroundKey : VsBrushes.ToolWindowBackgroundKey);
        bubble.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        bubble.BorderThickness = new Thickness(1);

        _messages.Children.Add(bubble);
        ScrollToEnd();
        return content;
    }

    // While a turn is running, lock the input so turns cannot interleave, and show progress.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _input.IsEnabled = !busy;
        if (busy) { _status.Text = "Claude is working..."; }
        else { _input.Focus(); }
    }

    private void ScrollToEnd() => _scroller.ScrollToEnd();

    // CLI events arrive on the pump thread; this marshals each update to the panel's own WPF
    // dispatcher. Dispatcher.Invoke is the right tool for a control's thread affinity - it is not a
    // Visual Studio service call, so the shell threading rule does not apply.
#pragma warning disable VSTHRD001
    private void OnUi(Action action) => Dispatcher.Invoke(action);
#pragma warning restore VSTHRD001

    // The CLI options chosen in the toolbar. They take effect when a session starts, so changing
    // them after a session is running applies on the next New session.
    private ClaudeCliOptions CurrentOptions() => new ClaudeCliOptions
    {
        Model = (_modelCombo.SelectedItem as ComboBoxItem)?.Tag as string,
        PermissionMode = (_modeCombo.SelectedItem as ComboBoxItem)?.Tag as string,
    };

    // Ends the current session and clears the transcript; the next message starts a fresh one
    // with the currently selected model and permission mode.
    private void NewSession()
    {
        _session?.Dispose();
        _session = null;
        _streamingReply = null;
        _messages.Children.Clear();
        SetBusy(false);
        _status.Text = "New session - the model and permission apply on your next message.";
    }

    // Writes the always-on permission floor (deny destructive shell + secret reads) to a temp
    // settings file that the session passes with --settings. If it cannot be written, the session
    // still starts - just without the extra floor - rather than blocking the developer.
    private static string? WriteSafetySettings()
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nlabs_claude_settings_" + Guid.NewGuid().ToString("n") + ".json");
            System.IO.File.WriteAllText(path, PermissionPolicy.BuildSettingsJson());
            return path;
        }
        catch
        {
            return null;
        }
    }

    private ComboBox MakeCombo((string label, string? value)[] options)
    {
        var combo = new ComboBox { MinWidth = 120, Margin = new Thickness(0, 0, 10, 0) };
        foreach (var (label, value) in options)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        combo.SelectedIndex = 0;
        return combo;
    }

    private static UIElement LabelFor(string text, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        var label = new TextBlock
        {
            Text = text + ":",
            Opacity = 0.7,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    private static string SolutionDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(DTE)) is DTE2 dte && dte.Solution != null &&
                !string.IsNullOrEmpty(dte.Solution.FullName))
            {
                return System.IO.Path.GetDirectoryName(dte.Solution.FullName) ?? string.Empty;
            }
        }
        catch { /* fall through to empty */ }
        return string.Empty;
    }

    public void ShutDown()
    {
        _session?.Dispose();
        _session = null;
    }
}
