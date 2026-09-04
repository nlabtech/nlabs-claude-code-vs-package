using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Nlabs.ClaudeCodeVsPackage.Bridge.Agent;
using Nlabs.ClaudeCodeVsPackage.Bridge.Markdown;

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
    private readonly Button _stopButton;

    private readonly System.Collections.Generic.Queue<string> _queue = new System.Collections.Generic.Queue<string>();
    private ClaudeCliSession? _session;
    private StackPanel? _streamingContainer;
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

        _stopButton = new Button
        {
            Content = "Stop",
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(10, 0, 0, 0),
            Visibility = Visibility.Collapsed, // shown only while a turn is running
        };
        _stopButton.Click += (_, __) => _ = StopAsync();

        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 8, 8, 0),
        };
        toolbar.Children.Add(newButton);
        toolbar.Children.Add(LabelFor("Model", _modelCombo));
        toolbar.Children.Add(LabelFor("Permission", _modeCombo));
        toolbar.Children.Add(_stopButton);

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
        if (text.Length == 0) return;

        _input.Clear();
        AddBubble(text, isUser: true);

        // The input stays live during a turn so the next message can be composed. If a turn is
        // running, queue this one and send it when the turn ends, instead of dropping it or
        // interleaving two turns on one session.
        if (_busy)
        {
            _queue.Enqueue(text);
            _status.Text = "Queued - it will send when the current turn ends.";
            return;
        }

        await RunTurnAsync(text);
    }

    // Runs one turn: opens (or reuses) the session, adds the streaming reply bubble and sends the
    // text. Used both for a fresh message and for draining the queue.
    private async System.Threading.Tasks.Task RunTurnAsync(string text)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            EnsureSession();
            _streamingText = string.Empty;
            _streamingContainer = AddAssistantBubble();
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
                    OnUi(() => { RenderStreaming(); ScrollToEnd(); });
                }
                break;

            case CliEventKind.Assistant:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText = e.Text!;
                    OnUi(() => { RenderStreaming(); ScrollToEnd(); });
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    _streamingContainer = null;
                    SetBusy(false);
                    if (e.TotalCostUsd.HasValue)
                    {
                        _status.Text = e.IsError
                            ? "Turn failed."
                            : string.Format("Ready - last turn ${0:0.0000}.", e.TotalCostUsd.Value);
                    }
                    DrainQueue();
                });
                break;
        }
    }

    // A user's turn: plain text, right-aligned. The user typed it, so it needs no markdown pass.
    private TextBlock AddBubble(string text, bool isUser)
    {
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(10, 6, 10, 6),
        };
        content.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        WrapInBubble(content, isUser);
        return content;
    }

    // Claude's turn: a block container (paragraphs, headings, bullets, code blocks) filled by the
    // markdown renderer as text streams in.
    private StackPanel AddAssistantBubble()
    {
        var container = new StackPanel { Margin = new Thickness(10, 6, 10, 6) };
        WrapInBubble(container, isUser: false);
        return container;
    }

    private void WrapInBubble(UIElement content, bool isUser)
    {
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
    }

    // Re-renders the in-flight reply from the accumulated text. Cheap enough per delta for the reply
    // sizes the panel sees; markdown parsing is pure and the block list is small.
    private void RenderStreaming()
    {
        if (_streamingContainer == null) return;
        RenderMarkdownInto(_streamingContainer, _streamingText);
    }

    // Turns parsed markdown blocks into WPF elements: code as a selectable monospace box, headings
    // bold and larger, bullets with a leading dot, paragraphs with inline bold and code.
    private void RenderMarkdownInto(StackPanel container, string text)
    {
        container.Children.Clear();
        foreach (MarkdownBlock block in MarkdownDocument.Parse(text))
        {
            switch (block.Kind)
            {
                case MarkdownBlockKind.Code:
                    container.Children.Add(BuildCodeBlock(block));
                    break;
                case MarkdownBlockKind.Heading:
                    container.Children.Add(BuildInlineText(block.Text, bold: true,
                        fontSize: 15 + Math.Max(0, 3 - block.HeadingLevel), topGap: 6));
                    break;
                case MarkdownBlockKind.Bullet:
                    container.Children.Add(BuildBullet(block.Text));
                    break;
                default:
                    container.Children.Add(BuildInlineText(block.Text, bold: false, fontSize: 0, topGap: 2));
                    break;
            }
        }
    }

    // A read-only, horizontally scrolling monospace box - selectable and copyable, so a demo can lift
    // the code straight out. An optional language caption sits above it.
    private UIElement BuildCodeBlock(MarkdownBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };
        if (!string.IsNullOrEmpty(block.Language))
        {
            var caption = new TextBlock
            {
                Text = block.Language,
                FontSize = 11,
                Opacity = 0.6,
                Margin = new Thickness(2, 0, 0, 2),
            };
            caption.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            panel.Children.Add(caption);
        }

        var code = new TextBox
        {
            Text = block.Text,
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        code.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        code.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        code.SetResourceReference(TextBox.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        panel.Children.Add(code);
        return panel;
    }

    private UIElement BuildBullet(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
        var dot = new TextBlock { Text = "•  ", Margin = new Thickness(0, 0, 0, 0) };
        dot.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        row.Children.Add(dot);
        row.Children.Add(BuildInlineText(text, bold: false, fontSize: 0, topGap: 0));
        return row;
    }

    // A paragraph/heading/bullet line whose **bold** and `code` spans are real runs.
    private TextBlock BuildInlineText(string text, bool bold, double fontSize, double topGap)
    {
        var tb = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, topGap, 0, 0),
        };
        if (fontSize > 0) tb.FontSize = fontSize;
        if (bold) tb.FontWeight = FontWeights.Bold;
        tb.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        foreach (MarkdownInline run in MarkdownDocument.ParseInline(text))
        {
            switch (run.Kind)
            {
                case MarkdownInlineKind.Bold:
                    tb.Inlines.Add(new Run(run.Text) { FontWeight = FontWeights.Bold });
                    break;
                case MarkdownInlineKind.Code:
                    tb.Inlines.Add(new Run(run.Text)
                    {
                        FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                    });
                    break;
                default:
                    tb.Inlines.Add(new Run(run.Text));
                    break;
            }
        }
        return tb;
    }

    // Shows turn progress. The input stays live during a turn (messages typed then are queued); the
    // Stop button appears only while a turn runs.
    private void SetBusy(bool busy)
    {
        _busy = busy;
        _stopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (busy) { _status.Text = "Claude is working..."; }
        else { _input.Focus(); }
    }

    // Asks the running turn to stop. Queued messages are dropped (a manual stop means "stop", not
    // "run the next one"); the session stays alive so the conversation can continue with a new
    // message. The CLI ends the turn with a result event, which returns the panel to idle.
    private async System.Threading.Tasks.Task StopAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_busy || _session == null) return;
        _queue.Clear();
        _status.Text = "Stopping...";
        try { await _session.InterruptAsync(); }
        catch { /* the session is already gone; the idle transition still happens below */ }
    }

    // After a turn ends, send the next queued message (if any) as its own turn. Called from the UI
    // dispatcher; RunTurnAsync re-establishes the main thread before touching the shell.
    private void DrainQueue()
    {
        if (_busy || _queue.Count == 0) return;
        string next = _queue.Dequeue();
        _ = RunTurnAsync(next);
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
        _streamingContainer = null;
        _queue.Clear();
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
