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
/// this adds no bundled runtime and cannot clash with the shell's own components. The look is hand
/// styled - a brand header, role-labelled message bubbles, an accent composer with a Send button -
/// and stays theme-aware: surfaces come from the shell's brushes, and the one fixed colour is the
/// brand accent, which reads on both light and dark.
///
/// The extension never holds an API key: the session runs on the developer's own Claude
/// subscription through the CLI. Nothing about the machine or account is shown or logged here.
/// </summary>
internal sealed class AgentPanelControl : UserControl
{
    // The nLabtech accent - the single fixed colour; everything else is a theme brush so the panel
    // matches whatever Visual Studio theme is active.
    private static readonly Brush Accent = Frozen(Color.FromRgb(0x5B, 0x6C, 0xF0));
    private static readonly Brush AccentHover = Frozen(Color.FromRgb(0x6B, 0x7B, 0xF5));
    private static readonly Brush OnAccent = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush AssistantFill = Frozen(Color.FromArgb(0x16, 0x9A, 0xA6, 0xC8));
    private static readonly FontFamily MonoFont = new FontFamily("Consolas, Cascadia Mono, Courier New");

    // One chat thread: its stored messages plus the CLI session id used to resume it.
    private sealed class Conversation
    {
        public string Title = "New chat";
        public string? CliSessionId; // captured from system/init; drives --resume
        public readonly System.Collections.Generic.List<(bool IsUser, string Text)> Messages
            = new System.Collections.Generic.List<(bool, string)>();
        public ComboBoxItem? Item; // its entry in the switcher, so the title can be refreshed
    }

    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly TextBlock _status;
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _modeCombo;
    private readonly ComboBox _convCombo;
    private readonly ComboBox _langCombo;
    private readonly Border _stopButton;
    private readonly Border _tasksBox;
    private readonly StackPanel _tasksList;

    // UI strings by language; every visible label re-reads these when the language changes.
    private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> Strings =
        new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
        {
            ["en"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["placeholder"] = "Message Claude...", ["model"] = "Model", ["permission"] = "Permission",
                ["chat"] = "Chat", ["language"] = "Language", ["new"] = "New", ["delete"] = "Delete",
                ["send"] = "Send", ["stop"] = "Stop", ["you"] = "You", ["assistant"] = "Claude",
                ["defaultModel"] = "Default model", ["askEach"] = "Ask each time", ["acceptEdits"] = "Accept edits",
                ["hello"] = "Type a message and press Enter.", ["working"] = "Claude is working...",
                ["newChat"] = "New chat - type a message to begin.",
                ["switched"] = "Switched - your next message resumes this chat.", ["tasks"] = "Tasks",
            },
            ["tr"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["placeholder"] = "Claude'a yaz...", ["model"] = "Model", ["permission"] = "Izin",
                ["chat"] = "Sohbet", ["language"] = "Dil", ["new"] = "Yeni", ["delete"] = "Sil",
                ["send"] = "Gonder", ["stop"] = "Durdur", ["you"] = "Sen", ["assistant"] = "Claude",
                ["defaultModel"] = "Varsayilan model", ["askEach"] = "Her seferinde sor", ["acceptEdits"] = "Duzenlemeleri kabul et",
                ["hello"] = "Bir mesaj yaz, Enter'a bas.", ["working"] = "Claude calisiyor...",
                ["newChat"] = "Yeni sohbet - baslamak icin bir mesaj yaz.",
                ["switched"] = "Gecildi - sonraki mesajin bu sohbeti surdurur.", ["tasks"] = "Gorevler",
            },
        };
    private readonly System.Collections.Generic.List<Action> _localizers = new System.Collections.Generic.List<Action>();
    private string _lang = "en";

    private readonly System.Collections.Generic.Queue<string> _queue = new System.Collections.Generic.Queue<string>();
    private readonly System.Windows.Threading.DispatcherTimer _renderTimer;
    private Conversation _current = new Conversation();
    private ClaudeCliSession? _session;
    private StackPanel? _streamingContainer;
    private volatile string _streamingText = string.Empty;
    private volatile bool _renderPending;
    private bool _busy;
    private bool _switching; // guards the conversation combo while we rebuild it

    public AgentPanelControl()
    {
        this.SetResourceReference(BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        this.SetResourceReference(ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _messages = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        _scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = _messages,
        };

        _status = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Text = Loc("hello"),
        };
        _status.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        _input = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinLines = 1,
            MaxLines = 8,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _input.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _input.SetResourceReference(TextBox.CaretBrushProperty, VsBrushes.ToolWindowTextKey);
        _input.PreviewKeyDown += OnInputKeyDown;

        _placeholder = new TextBlock
        {
            Opacity = 0.45,
            IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 0, 0),
        };
        _placeholder.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => _placeholder.Text = Loc("placeholder"));

        // Now that the placeholder exists, toggle it with the input's content.
        _input.TextChanged += (_, __) => _placeholder.Visibility =
            string.IsNullOrEmpty(_input.Text) ? Visibility.Visible : Visibility.Collapsed;

        _modelCombo = MakeCombo(new (string, string?)[] { ("Default model", null), ("Opus", "opus"), ("Sonnet", "sonnet") });
        _modeCombo = MakeCombo(new (string, string?)[] { ("Ask each time", null), ("Accept edits", "acceptEdits") });
        // Localize the wording of the fixed entries (model names stay as-is).
        Bind(() => ((ComboBoxItem)_modelCombo.Items[0]).Content = Loc("defaultModel"));
        Bind(() => ((ComboBoxItem)_modeCombo.Items[0]).Content = Loc("askEach"));
        Bind(() => ((ComboBoxItem)_modeCombo.Items[1]).Content = Loc("acceptEdits"));
        _convCombo = new ComboBox
        {
            MinWidth = 150,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _convCombo.SelectionChanged += OnConversationSelected;
        RegisterConversation(_current, select: true);

        _langCombo = new ComboBox { MinWidth = 90, FontSize = 12, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _langCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        _langCombo.Items.Add(new ComboBoxItem { Content = "Turkce", Tag = "tr" });
        _langCombo.SelectedIndex = 0;
        _langCombo.SelectionChanged += (_, __) => OnLanguageChanged();

        _stopButton = MakeGhostButton("stop", () => _ = StopAsync());

        // Live to-do strip - hidden until the agent writes a task list, updated as it progresses.
        _tasksList = new StackPanel();
        var tasksHeader = new TextBlock { FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 5) };
        tasksHeader.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => tasksHeader.Text = Loc("tasks"));
        var tasksInner = new StackPanel();
        tasksInner.Children.Add(tasksHeader);
        tasksInner.Children.Add(_tasksList);
        _tasksBox = new Border
        {
            Child = tasksInner,
            Margin = new Thickness(12, 4, 12, 0),
            Padding = new Thickness(10, 8, 10, 8),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = AssistantFill,
            Visibility = Visibility.Collapsed,
        };
        _tasksBox.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        _stopButton.Visibility = Visibility.Collapsed; // shown only while a turn is running

        // Streaming deltas arrive far faster than a full re-render can keep up, so they only mark the
        // reply dirty; this timer repaints it a few times a second. That keeps the UI responsive on a
        // long reply instead of rebuilding the whole message tree on every token.
        _renderTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(90),
        };
        _renderTimer.Tick += (_, __) =>
        {
            if (!_renderPending) return;
            _renderPending = false;
            RenderStreaming();
            ScrollToEnd();
        };

        // Build each section once - these add fields (input, status, combos) as children, so a second
        // call would try to re-parent the same element and throw.
        UIElement header = BuildHeader();
        UIElement chatRow = BuildChatRow();
        UIElement settingsRow = BuildSettingsRow();
        UIElement composer = BuildComposer();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(chatRow, Dock.Top);
        DockPanel.SetDock(settingsRow, Dock.Top);
        DockPanel.SetDock(_tasksBox, Dock.Top);
        DockPanel.SetDock(composer, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(header);
        root.Children.Add(chatRow);
        root.Children.Add(settingsRow);
        root.Children.Add(_tasksBox);
        root.Children.Add(composer);
        root.Children.Add(_scroller);
        Content = root;
    }

    // The brand bar: product name plus a subtle nLabtech tag, over a hairline divider.
    private UIElement BuildHeader()
    {
        var title = new TextBlock { FontSize = 14, FontWeight = FontWeights.SemiBold, Text = "Claude Code" };
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var tag = new TextBlock
        {
            Text = "nLabtech",
            FontSize = 11,
            Margin = new Thickness(8, 2, 0, 0),
            Opacity = 0.55,
            VerticalAlignment = VerticalAlignment.Center,
        };
        tag.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var accentDot = new Border
        {
            Width = 8,
            Height = 8,
            CornerRadius = new CornerRadius(4),
            Background = Accent,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(accentDot);
        row.Children.Add(title);
        row.Children.Add(tag);

        var bar = new Border
        {
            Padding = new Thickness(12, 10, 12, 10),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
        bar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        return bar;
    }

    // The conversation switcher plus New and Delete.
    private UIElement BuildChatRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 12, 0),
        };
        row.Children.Add(LabelFor("chat", _convCombo));
        row.Children.Add(MakeGhostButton("new", NewConversation));
        var del = MakeGhostButton("delete", DeleteConversation);
        del.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(del);
        return row;
    }

    // Model, permission and language. A WrapPanel lets them flow to a second line in a narrow panel.
    private UIElement BuildSettingsRow()
    {
        var row = new WrapPanel { Margin = new Thickness(12, 6, 12, 2) };
        row.Children.Add(LabelFor("model", _modelCombo));
        row.Children.Add(LabelFor("permission", _modeCombo));
        row.Children.Add(LabelFor("language", _langCombo));
        return row;
    }

    // The bottom dock: a status/stop line over the rounded input + Send button.
    private UIElement BuildComposer()
    {
        var statusRow = new DockPanel { Margin = new Thickness(2, 0, 2, 6) };
        DockPanel.SetDock(_stopButton, Dock.Right);
        statusRow.Children.Add(_stopButton);
        statusRow.Children.Add(_status);

        var inputGrid = new Grid();
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(_placeholder);

        var send = MakeAccentButton("send", () => _ = SendAsync());
        send.VerticalAlignment = VerticalAlignment.Bottom;
        DockPanel.SetDock(send, Dock.Right);

        var inputRow = new DockPanel { LastChildFill = true };
        inputRow.Children.Add(send);
        inputRow.Children.Add(inputGrid);

        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 8, 8),
            Child = inputRow,
        };
        inputBorder.SetResourceReference(Border.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        inputBorder.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        var composer = new StackPanel { Margin = new Thickness(12, 6, 12, 12) };
        composer.Children.Add(statusRow);
        composer.Children.Add(inputBorder);
        return composer;
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
        AddUserBubble(text);
        bool firstInChat = _current.Messages.Count == 0;
        _current.Messages.Add((true, text));
        if (firstInChat) SetConversationTitle(_current, text);

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
            _renderPending = false;
            _renderTimer.Start();
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

        var session = new ClaudeCliSession();
        session.Event += OnCliEvent;
        // Only react to an exit if this is still the live session - a manual Stop nulls the field
        // first, and its own kill must not clobber the "Stopped" status.
        session.Exited += (_, __) => OnUi(() =>
        {
            if (!ReferenceEquals(_session, session)) return;
            SetBusy(false);
            _status.Text = "Session ended.";
        });
        _session = session;
        ClaudeCliOptions options = CurrentOptions();
        options.Resume = _current.CliSessionId; // continue this chat if it already has a session
        options.SettingsPath = WriteSafetySettings();
        _session.Start(SolutionDirectory(), options);
        _status.Text = "Connected.";
    }

    // CLI events arrive on the pump thread; marshal every UI change to the dispatcher.
    private void OnCliEvent(object sender, CliEvent e)
    {
        // Ignore late events from a session that has been superseded (Stop, New, or a switch),
        // so a dying session cannot mutate the conversation that replaced it.
        if (!ReferenceEquals(sender, _session)) return;

        if (e.Todos != null)
        {
            var todos = e.Todos;
            OnUi(() => RenderTasks(todos));
        }

        switch (e.Kind)
        {
            case CliEventKind.SystemInit:
                if (!string.IsNullOrEmpty(e.SessionId)) OnUi(() => _current.CliSessionId = e.SessionId);
                if (!string.IsNullOrEmpty(e.Model)) OnUi(() => _status.Text = "Model: " + e.Model);
                break;

            case CliEventKind.StreamDelta:
                // Accumulate and mark dirty; the render timer repaints on its own cadence.
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText += e.Text;
                    _renderPending = true;
                }
                break;

            case CliEventKind.Assistant:
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText = e.Text!;
                    _renderPending = true;
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    // Final repaint so the last tokens show, then stop streaming.
                    _renderTimer.Stop();
                    _renderPending = false;
                    RenderStreaming();
                    if (_streamingText.Length > 0) _current.Messages.Add((false, _streamingText));
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

    // A user's turn: plain text in an accent bubble, right-aligned. The user typed it, so it needs
    // no markdown pass.
    private void AddUserBubble(string text)
    {
        var content = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Foreground = OnAccent,
        };
        AddMessageRow(content, isUser: true);
    }

    // Claude's turn: a block container (paragraphs, headings, bullets, code blocks) filled by the
    // markdown renderer as text streams in.
    private StackPanel AddAssistantBubble()
    {
        var container = new StackPanel();
        AddMessageRow(container, isUser: false);
        return container;
    }

    // One message row: a small role label over the bubble, aligned to its side.
    private void AddMessageRow(UIElement bubbleContent, bool isUser)
    {
        var label = new TextBlock
        {
            Text = isUser ? Loc("you") : Loc("assistant"),
            FontSize = 10.5,
            Opacity = 0.5,
            Margin = new Thickness(4, 0, 4, 3),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var bubble = new Border
        {
            Child = bubbleContent,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 9, 12, 9),
            MaxWidth = 560,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        if (isUser)
        {
            bubble.Background = Accent;
        }
        else
        {
            bubble.Background = AssistantFill;
            bubble.BorderThickness = new Thickness(1);
            bubble.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        }

        var column = new StackPanel
        {
            Margin = new Thickness(0, 5, 0, 5),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
        };
        column.Children.Add(label);
        column.Children.Add(bubble);

        _messages.Children.Add(column);
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
                    container.Children.Add(BuildInlineText(block.Text, bold: false, fontSize: 0, topGap: 3));
                    break;
            }
        }
    }

    // A read-only, horizontally scrolling monospace box - selectable and copyable, so a demo can lift
    // the code straight out. An optional language caption sits above it.
    private UIElement BuildCodeBlock(MarkdownBlock block)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 6, 0, 4) };
        if (!string.IsNullOrEmpty(block.Language))
        {
            var caption = new TextBlock
            {
                Text = block.Language,
                FontSize = 10.5,
                Opacity = 0.55,
                Margin = new Thickness(2, 0, 0, 3),
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
            Padding = new Thickness(10),
            FontFamily = MonoFont,
            FontSize = 12.5,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        code.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        code.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        code.SetResourceReference(TextBox.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        var codeBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = code,
        };
        panel.Children.Add(codeBorder);
        return panel;
    }

    private UIElement BuildBullet(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };
        var dot = new TextBlock { Text = "•  ", Foreground = Accent, FontWeight = FontWeights.Bold };
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
            LineHeight = 18,
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
                    tb.Inlines.Add(new Run(run.Text) { FontFamily = MonoFont });
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
        if (busy) { _status.Text = Loc("working"); }
        else { _input.Focus(); }
    }

    // Stops the running turn by ending the CLI process - the plain CLI has no client-answerable
    // interrupt channel, so a kill is the reliable stop. Queued messages are dropped (a manual stop
    // means "stop", not "run the next one"), and the next message opens a fresh session. Whatever
    // text already streamed stays on screen.
    private async System.Threading.Tasks.Task StopAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (!_busy) return;
        _renderTimer.Stop();
        _renderPending = false;
        _queue.Clear();
        _session?.Cancel();
        _session = null;
        _streamingContainer = null;
        SetBusy(false);
        _status.Text = "Stopped - your next message starts a new session.";
    }

    // After a turn ends, send the next queued message (if any) as its own turn. Called from the UI
    // dispatcher; RunTurnAsync re-establishes the main thread before touching the shell.
    private void DrainQueue()
    {
        if (_busy || _queue.Count == 0) return;
        string next = _queue.Dequeue();
        _ = RunTurnAsync(next);
    }

    // Paints the agent's live to-do list: a dot per item coloured by state, the current one in bold.
    private void RenderTasks(System.Collections.Generic.IReadOnlyList<TodoItem> todos)
    {
        _tasksList.Children.Clear();
        if (todos.Count == 0) { _tasksBox.Visibility = Visibility.Collapsed; return; }

        foreach (TodoItem t in todos)
        {
            bool done = t.Status == "completed";
            bool active = t.Status == "in_progress";

            var dot = new TextBlock
            {
                Text = done ? "✓  " : active ? "›  " : "○  ",
                FontWeight = active ? FontWeights.Bold : FontWeights.Normal,
            };
            if (done || active) dot.Foreground = Accent;
            else dot.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var text = new TextBlock
            {
                Text = t.Content,
                TextWrapping = TextWrapping.Wrap,
                Opacity = done ? 0.55 : 1.0,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
                TextDecorations = done ? TextDecorations.Strikethrough : null,
            };
            text.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 1, 0, 1) };
            row.Children.Add(dot);
            row.Children.Add(text);
            _tasksList.Children.Add(row);
        }
        _tasksBox.Visibility = Visibility.Visible;
    }

    private void ClearTasks()
    {
        _tasksList.Children.Clear();
        _tasksBox.Visibility = Visibility.Collapsed;
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

    // Starts a fresh chat and switches to it.
    private void NewConversation()
    {
        var c = new Conversation();
        RegisterConversation(c, select: true);
        SwitchTo(c);
    }

    // Removes the current chat; keeps at least one around (clearing the last one just resets it).
    private void DeleteConversation()
    {
        if (_convCombo.Items.Count <= 1)
        {
            _current.Messages.Clear();
            _current.CliSessionId = null;
            SetConversationTitle(_current, "New chat");
            SwitchTo(_current);
            return;
        }

        ComboBoxItem? removing = _current.Item;
        Conversation? next = null;
        foreach (var obj in _convCombo.Items)
        {
            if (obj is ComboBoxItem it && !ReferenceEquals(it, removing) && it.Tag is Conversation cc) { next = cc; break; }
        }

        _session?.Dispose();
        _session = null;
        _switching = true;
        if (removing != null) _convCombo.Items.Remove(removing);
        _switching = false;
        if (next != null) SwitchTo(next);
    }

    private void RegisterConversation(Conversation c, bool select)
    {
        var item = new ComboBoxItem { Content = c.Title, Tag = c };
        c.Item = item;
        _switching = true;
        _convCombo.Items.Add(item);
        if (select) _convCombo.SelectedItem = item;
        _switching = false;
    }

    private void OnConversationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_switching) return;
        if (_convCombo.SelectedItem is ComboBoxItem item && item.Tag is Conversation c && !ReferenceEquals(c, _current))
        {
            SwitchTo(c);
        }
    }

    // Drops the running session and shows the target chat; the next message resumes it via --resume.
    private void SwitchTo(Conversation c)
    {
        _renderTimer.Stop();
        _renderPending = false;
        _queue.Clear();
        _session?.Dispose();
        _session = null;
        _streamingContainer = null;

        _current = c;
        _switching = true;
        if (c.Item != null) _convCombo.SelectedItem = c.Item;
        _switching = false;

        ClearTasks();
        RebuildMessages();
        SetBusy(false);
        _status.Text = c.CliSessionId == null ? Loc("newChat") : Loc("switched");
    }

    // Repaints the transcript of the current chat from its stored messages.
    private void RebuildMessages()
    {
        _messages.Children.Clear();
        foreach (var (isUser, text) in _current.Messages)
        {
            if (isUser)
            {
                AddUserBubble(text);
            }
            else
            {
                StackPanel container = AddAssistantBubble();
                RenderMarkdownInto(container, text);
            }
        }
        ScrollToEnd();
    }

    private void SetConversationTitle(Conversation c, string text)
    {
        string title = text.Trim().Replace("\r", " ").Replace("\n", " ");
        if (title.Length > 32) title = title.Substring(0, 32).TrimEnd() + "...";
        if (title.Length == 0) title = "New chat";
        c.Title = title;
        if (c.Item != null)
        {
            _switching = true;
            c.Item.Content = title;
            _switching = false;
        }
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

    private string Loc(string key) =>
        Strings.TryGetValue(_lang, out var map) && map.TryGetValue(key, out var s) ? s : key;

    // Registers a text setter so a language switch can re-apply it, and applies it once now.
    private void Bind(Action apply)
    {
        apply();
        _localizers.Add(apply);
    }

    private void OnLanguageChanged()
    {
        _lang = (_langCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        foreach (Action apply in _localizers) apply();
        RebuildMessages(); // role labels are rebuilt with the new language
    }

    private ComboBox MakeCombo((string label, string? value)[] options)
    {
        var combo = new ComboBox
        {
            MinWidth = 118,
            FontSize = 12,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        foreach (var (label, value) in options)
        {
            combo.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        }
        combo.SelectedIndex = 0;
        return combo;
    }

    private UIElement LabelFor(string key, UIElement control)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 6, 0) };
        var label = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => label.Text = Loc(key));
        panel.Children.Add(label);
        panel.Children.Add(control);
        return panel;
    }

    // A filled accent button (Send). Built as a Border so it is fully rounded and branded, with a
    // hover tint and a hand cursor - a plain WPF Button cannot be themed this cleanly in code.
    private Border MakeAccentButton(string key, Action onClick)
    {
        var label = new TextBlock
        {
            Foreground = OnAccent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
        };
        Bind(() => label.Text = Loc(key));
        var button = new Border
        {
            Child = label,
            Background = Accent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(8, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        label.HorizontalAlignment = HorizontalAlignment.Center;
        button.MouseEnter += (_, __) => button.Background = AccentHover;
        button.MouseLeave += (_, __) => button.Background = Accent;
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // A quiet outlined button (New, Stop): themed border, no fill, hand cursor.
    private Border MakeGhostButton(string key, Action onClick)
    {
        var label = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => label.Text = Loc(key));
        var button = new Border
        {
            Child = label,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 4, 12, 4),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        button.MouseEnter += (_, __) => button.Opacity = 0.7;
        button.MouseLeave += (_, __) => button.Opacity = 1.0;
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
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

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void ShutDown()
    {
        _session?.Dispose();
        _session = null;
    }
}
