using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    // The accent - the one colour the panel picks itself; the surface follows the Visual Studio theme.
    // These two are kept mutable and shared: every element paints with the same brush instance, so
    // recolouring in place (ApplyAccent) repaints the header dot, Send, the user bubbles and the task
    // ticks at once, with no rebuild. The final Claude palette comes in the design pass.
    private readonly SolidColorBrush Accent = new SolidColorBrush(Color.FromRgb(0xD9, 0x77, 0x57));
    private readonly SolidColorBrush AccentHover = new SolidColorBrush(Color.FromRgb(0xE0, 0x88, 0x6B));
    // A faint accent-tinted fill for the user's own turn - the same hue as the accent, barely there.
    private readonly SolidColorBrush UserFill = new SolidColorBrush(Color.FromArgb(0x1F, 0xD9, 0x77, 0x57));
    private static readonly Brush OnAccent = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));

    // The accent choices. Claude's warm tone is the default; the rest are alternatives.
    private static readonly (string Name, Color Color)[] Accents =
    {
        ("Claude", Color.FromRgb(0xD9, 0x77, 0x57)),
        ("Indigo", Color.FromRgb(0x5B, 0x6C, 0xF0)),
        ("Violet", Color.FromRgb(0x8B, 0x5C, 0xF6)),
        ("Teal",   Color.FromRgb(0x14, 0xB8, 0xA6)),
        ("Rose",   Color.FromRgb(0xE1, 0x1D, 0x48)),
    };
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

    // An image waiting to be sent: its bytes (for the CLI) and a thumbnail (for the chip and echo).
    private sealed class PendingImage
    {
        public string Base64 = string.Empty;
        public string MediaType = "image/png";
        public ImageSource? Thumb;
    }

    // A turn that couldn't send yet because one was already running: its text and its images.
    private sealed class PendingTurn
    {
        public string Text = string.Empty;
        public List<ImageAttachment> Images = new List<ImageAttachment>();
    }

    // One entry in the "/" command menu. A panel command runs locally (Panel set); a forward command
    // (Panel null) is dropped into the input as "/name " for the CLI to expand when the turn is sent.
    private sealed class SlashCommand
    {
        public string Name = string.Empty;
        public string Description = string.Empty;
        public Action? Panel;
    }

    private readonly StackPanel _messages;
    private readonly ScrollViewer _scroller;
    private readonly TextBox _input;
    private readonly TextBlock _placeholder;
    private readonly WrapPanel _attachStrip = new WrapPanel { Margin = new Thickness(0, 0, 0, 6), Visibility = Visibility.Collapsed };
    private readonly List<PendingImage> _pending = new List<PendingImage>();
    private Border? _inputBorder;
    private Popup? _slashPopup;   // the "/" command menu, anchored above the input
    private ListBox? _slashList;
    private readonly TextBlock _status;
    private readonly ComboBox _modelCombo;
    private readonly ComboBox _modeCombo;
    private readonly ComboBox _effortCombo;
    private readonly ComboBox _convCombo;
    private TextBox? _renameBox;   // swapped in over the switcher while renaming the current chat
    private readonly ComboBox _langCombo;
    private readonly ComboBox _accentCombo;
    private Border? _primary;       // the Send button; becomes Stop while a turn runs
    private TextBlock? _primaryLabel;
    private readonly Border _tasksBox;
    private readonly StackPanel _tasksList;

    // UI strings by language; every visible label re-reads these when the language changes.
    private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>> Strings =
        new System.Collections.Generic.Dictionary<string, System.Collections.Generic.Dictionary<string, string>>
        {
            ["en"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["placeholder"] = "Ask Claude - Enter sends", ["model"] = "Model", ["permission"] = "Permission",
                ["chat"] = "Chat", ["language"] = "Language", ["new"] = "New", ["delete"] = "Delete", ["rename"] = "Rename",
                ["send"] = "Send", ["stop"] = "Stop", ["you"] = "You", ["assistant"] = "Claude",
                ["defaultModel"] = "Default model", ["askEach"] = "Ask each time", ["acceptEdits"] = "Accept edits", ["planMode"] = "Plan mode",
                ["hello"] = "Type a message and press Enter.", ["working"] = "Claude is working...",
                ["newChat"] = "New chat - type a message to begin.",
                ["switched"] = "Switched - your next message resumes this chat.", ["tasks"] = "Tasks",
                ["copy"] = "Copy", ["copied"] = "Copied", ["session"] = "session",
                ["allow"] = "Allow", ["deny"] = "Deny", ["always"] = "Always allow",
                ["wantsToRun"] = "wants to run", ["allowed"] = "Allowed", ["denied"] = "Denied",
                ["edit"] = "Edit", ["accent"] = "Accent", ["attachHint"] = "Attach an image",
                ["tokens"] = "tokens", ["defaultEffort"] = "Effort: default",
                ["pickFolder"] = "Pick folder", ["folderSet"] = "Folder set - your next message starts here.",
                ["cmdNew"] = "Start a new chat", ["cmdClear"] = "Clear this chat and its context",
                ["commands"] = "Commands", ["projectCommands"] = "Project commands",
                ["addSelection"] = "Add the editor selection", ["noSelection"] = "Select some code in the editor first.",
                ["undoTurn"] = "Undo turn",
                ["undoConfirm"] = "Revert the tracked files changed in the last turn to their state before it? New files are left in place.",
                ["undoDone"] = "Reverted the last turn's file changes.", ["undoFail"] = "Could not revert - see your git working tree.",
                ["planReady"] = "Claude has a plan", ["applyPlan"] = "Apply plan", ["keepPlanning"] = "Keep planning",
                ["planApplied"] = "Applying the plan...", ["planKept"] = "Still planning...",
                ["pickAgent"] = "Use a subagent", ["noAgents"] = "No subagents found",
                ["review"] = "Review", ["bridgeOn"] = "Approvals on", ["bridgeOff"] = "Approvals off",
                ["reviewPrompt"] = "Review my current uncommitted changes for bugs, security issues, and simple cleanups. Do not modify any files - just report your findings.",
            },
            ["tr"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["placeholder"] = "Claude'a bir sey sor - Enter gonderir", ["model"] = "Model", ["permission"] = "Izin",
                ["chat"] = "Sohbet", ["language"] = "Dil", ["new"] = "Yeni", ["delete"] = "Sil", ["rename"] = "Yeniden adlandir",
                ["send"] = "Gonder", ["stop"] = "Durdur", ["you"] = "Sen", ["assistant"] = "Claude",
                ["defaultModel"] = "Varsayilan model", ["askEach"] = "Her seferinde sor", ["acceptEdits"] = "Duzenlemeleri kabul et", ["planMode"] = "Plan modu",
                ["hello"] = "Bir mesaj yaz, Enter'a bas.", ["working"] = "Claude calisiyor...",
                ["newChat"] = "Yeni sohbet - baslamak icin bir mesaj yaz.",
                ["switched"] = "Gecildi - sonraki mesajin bu sohbeti surdurur.", ["tasks"] = "Gorevler",
                ["copy"] = "Kopyala", ["copied"] = "Kopyalandi", ["session"] = "oturum",
                ["allow"] = "Izin ver", ["deny"] = "Reddet", ["always"] = "Hep izin ver",
                ["wantsToRun"] = "calistirmak istiyor", ["allowed"] = "Izin verildi", ["denied"] = "Reddedildi",
                ["edit"] = "Duzenle", ["accent"] = "Vurgu", ["attachHint"] = "Gorsel ekle",
                ["tokens"] = "token", ["defaultEffort"] = "Efor: varsayilan",
                ["pickFolder"] = "Klasor sec", ["folderSet"] = "Klasor secildi - sonraki mesajin burada baslar.",
                ["cmdNew"] = "Yeni bir sohbet baslat", ["cmdClear"] = "Bu sohbeti ve baglamini temizle",
                ["commands"] = "Komutlar", ["projectCommands"] = "Proje komutlari",
                ["addSelection"] = "Editordeki secimi ekle", ["noSelection"] = "Once editorde bir kod sec.",
                ["undoTurn"] = "Turu geri al",
                ["undoConfirm"] = "Son turda degisen izlenen dosyalar tur oncesi haline dondurulsun mu? Yeni dosyalar yerinde kalir.",
                ["undoDone"] = "Son turun dosya degisiklikleri geri alindi.", ["undoFail"] = "Geri alinamadi - git calisma agacini kontrol et.",
                ["planReady"] = "Claude'un bir plani var", ["applyPlan"] = "Plani uygula", ["keepPlanning"] = "Planlamaya devam",
                ["planApplied"] = "Plan uygulaniyor...", ["planKept"] = "Planlama suruyor...",
                ["pickAgent"] = "Alt ajan kullan", ["noAgents"] = "Alt ajan bulunamadi",
                ["review"] = "Denetle", ["bridgeOn"] = "Onaylar acik", ["bridgeOff"] = "Onaylar kapali",
                ["reviewPrompt"] = "Commit edilmemis mevcut degisikliklerimi hata, guvenlik sorunu ve basit iyilestirmeler icin incele. Hicbir dosyayi degistirme - sadece bulgulari raporla.",
            },
        };
    private readonly System.Collections.Generic.List<Action> _localizers = new System.Collections.Generic.List<Action>();
    private string _lang = "en";

    private readonly System.Collections.Generic.Queue<PendingTurn> _queue = new System.Collections.Generic.Queue<PendingTurn>();
    private readonly System.Windows.Threading.DispatcherTimer _renderTimer;
    private readonly Border _workingStrip;
    private readonly TextBlock _workingLabel;
    private readonly System.Windows.Threading.DispatcherTimer _elapsedTimer;
    private DateTime _turnStart;
    private int _turnTokens;
    private Conversation _current = new Conversation();
    private ClaudeCliSession? _session;
    private StackPanel? _streamingContainer;
    private StackPanel? _streamingColumn;
    private TextBlock? _streamingTextBlock;
    private volatile string _streamingText = string.Empty;
    private volatile bool _renderPending;
    private bool _busy;
    private bool _switching; // guards the conversation combo while we rebuild it
    private double _sessionCost; // running total across the panel's turns
    private ApprovalService? _approval;
    private string? _hookScriptPath;
    private readonly System.Collections.Generic.HashSet<string> _alwaysAllow = new System.Collections.Generic.HashSet<string>();
    private readonly ConversationStore _store = new ConversationStore();
    private readonly PanelPreferencesStore _prefs = new PanelPreferencesStore();
    private bool _prefsLoaded; // suppresses saves while the constructor applies the stored choices
    private string _workspaceKey = string.Empty;
    private string? _workingFolder;    // an explicit working folder chosen when no solution is open
    private TextBlock? _folderLabel;
    private string? _snapshotRef;      // git ref to restore tracked files to (stash-create SHA, or HEAD)
    private Border? _undoButton;
    private TextBlock? _undoLabel;
    private Border? _bridgeDot;        // approval-bridge indicator: bright when the endpoint is live
    private TextBlock? _bridgeLabel;

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
        _input.AllowDrop = false; // let image drops fall through to the border's handler

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

        // Tier aliases, not pinned versions - each resolves to the latest model of that tier, so the
        // list doesn't go stale as new releases land.
        _modelCombo = MakeCombo(new (string, string?)[] { ("Default model", null), ("Opus", "opus"), ("Sonnet", "sonnet"), ("Fable", "fable") });
        _modeCombo = MakeCombo(new (string, string?)[] { ("Ask each time", null), ("Accept edits", "acceptEdits"), ("Plan mode", "plan") });
        _effortCombo = MakeCombo(new (string, string?)[]
        {
            ("Effort: default", null), ("Low", "low"), ("Medium", "medium"), ("High", "high"), ("xHigh", "xhigh"), ("Max", "max"),
        });
        // Localize the wording of the fixed entries (model, level and mode names stay as-is).
        Bind(() => ((ComboBoxItem)_modelCombo.Items[0]).Content = Loc("defaultModel"));
        Bind(() => ((ComboBoxItem)_modeCombo.Items[0]).Content = Loc("askEach"));
        Bind(() => ((ComboBoxItem)_modeCombo.Items[1]).Content = Loc("acceptEdits"));
        Bind(() => ((ComboBoxItem)_modeCombo.Items[2]).Content = Loc("planMode"));
        Bind(() => ((ComboBoxItem)_effortCombo.Items[0]).Content = Loc("defaultEffort"));
        _convCombo = new ComboBox
        {
            MinWidth = 150,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _convCombo.SelectionChanged += OnConversationSelected;
        // The tool window always builds its content on the UI thread, so this UI-thread call is safe.
#pragma warning disable VSTHRD010
        LoadConversations();
#pragma warning restore VSTHRD010

        _langCombo = new ComboBox { MinWidth = 90, FontSize = 12, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        _langCombo.Items.Add(new ComboBoxItem { Content = "English", Tag = "en" });
        _langCombo.Items.Add(new ComboBoxItem { Content = "Turkce", Tag = "tr" });
        _langCombo.SelectedIndex = 0;
        _langCombo.SelectionChanged += (_, __) => OnLanguageChanged();

        _accentCombo = new ComboBox { MinWidth = 90, FontSize = 12, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var a in Accents) _accentCombo.Items.Add(new ComboBoxItem { Content = a.Name, Tag = a.Name });
        _accentCombo.SelectedIndex = 0;
        _accentCombo.SelectionChanged += (_, __) => OnAccentChanged();

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
            Background = UserFill,
            Visibility = Visibility.Collapsed,
        };
        _tasksBox.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        // A live "working" strip shown only during a turn: a pulsing accent dot, elapsed seconds and a
        // running token count, so a long turn visibly makes progress instead of looking stuck.
        _workingLabel = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        _workingLabel.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var workingDot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(4), Background = Accent, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var workingRow = new StackPanel { Orientation = Orientation.Horizontal };
        workingRow.Children.Add(workingDot);
        workingRow.Children.Add(_workingLabel);
        _workingStrip = new Border
        {
            Child = workingRow,
            Margin = new Thickness(0, 0, 0, 6),
            Padding = new Thickness(10, 6, 10, 6),
            CornerRadius = new CornerRadius(8),
            Background = UserFill,
            Visibility = Visibility.Collapsed,
        };

        _elapsedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, __) => UpdateWorkingStrip();

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
            ScrollToEndIfAtBottom();
        };

        // Build each section once - these add fields (input, status, combos) as children, so a second
        // call would try to re-parent the same element and throw.
        UIElement header = BuildHeader();
        UIElement chatRow = BuildChatRow();
        // BuildComposer reads the current folder (a solution-service call) while wiring the status bar;
        // the tool window always builds on the UI thread, so this is safe.
#pragma warning disable VSTHRD010
        UIElement composer = BuildComposer();
#pragma warning restore VSTHRD010
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(chatRow, Dock.Top);
        DockPanel.SetDock(_tasksBox, Dock.Top);
        DockPanel.SetDock(composer, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(header);
        root.Children.Add(chatRow);
        root.Children.Add(_tasksBox);
        root.Children.Add(composer);
        root.Children.Add(_scroller);
        Content = root;

        // Everything is built and every label registered, so it's safe to apply the stored choices -
        // selecting an item fires the change handlers, which re-localize and recolour.
        ApplyPreferences();
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

        var brand = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        brand.Children.Add(accentDot);
        brand.Children.Add(title);
        brand.Children.Add(tag);

        // Language and accent live top-right, out of the way of the conversation itself.
        var prefs = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _langCombo.Margin = new Thickness(0, 0, 6, 0);
        _accentCombo.Margin = new Thickness(0);
        prefs.Children.Add(_langCombo);
        prefs.Children.Add(_accentCombo);

        var row = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(brand, Dock.Left);
        DockPanel.SetDock(prefs, Dock.Right);
        row.Children.Add(brand);
        row.Children.Add(prefs);

        var bar = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
        bar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        return bar;
    }

    // The conversation switcher plus New, Rename and Delete. Rename swaps a text box in over the
    // switcher; Enter commits it, Escape or a click away cancels.
    private UIElement BuildChatRow()
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(12, 8, 12, 0),
        };
        row.Children.Add(LabelFor("chat", _convCombo));

        _renameBox = new TextBox
        {
            MinWidth = 150,
            FontSize = 12,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _renameBox.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _renameBox.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        _renameBox.KeyDown += OnRenameKey;
        _renameBox.LostKeyboardFocus += (_, __) => EndRename();
        row.Children.Add(_renameBox);

        row.Children.Add(MakeGhostButton("new", NewConversation));
        var rename = MakeGhostButton("rename", BeginRename);
        rename.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(rename);
        var del = MakeGhostButton("delete", DeleteConversation);
        del.Margin = new Thickness(6, 0, 0, 0);
        row.Children.Add(del);
        return row;
    }

    // Shows the rename box seeded with the current title. LabelFor keeps the switcher inside its own
    // panel, so hide the combo itself and show the box beside it.
    private void BeginRename()
    {
        if (_renameBox == null) return;
        _renameBox.Text = _current.Title;
        _convCombo.Visibility = Visibility.Collapsed;
        _renameBox.Visibility = Visibility.Visible;
        _renameBox.Focus();
        _renameBox.SelectAll();
    }

    private void OnRenameKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            RenameCurrent(_renameBox?.Text ?? string.Empty);
            SaveConversations();
            EndRename();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            EndRename();
            e.Handled = true;
        }
    }

    // Restores the switcher. Idempotent, so a commit followed by the box losing focus is harmless.
    private void EndRename()
    {
        if (_renameBox == null) return;
        _renameBox.Visibility = Visibility.Collapsed;
        _convCombo.Visibility = Visibility.Visible;
    }

    // Applies a manual title (a touch longer than the auto title, since the developer chose it).
    private void RenameCurrent(string title)
    {
        title = title.Replace("\r", " ").Replace("\n", " ").Trim();
        if (title.Length == 0) return;
        if (title.Length > 60) title = title.Substring(0, 60).TrimEnd() + "...";
        _current.Title = title;
        if (_current.Item != null)
        {
            _switching = true;
            _current.Item.Content = title;
            _switching = false;
        }
    }

    // The bottom dock: the rounded input card (chip strip, text, and a toolbar row with the attach
    // button on the left and the model/permission pills packed against Send on the right), with a
    // quiet status line beneath it.
    private UIElement BuildComposer()
    {
        var inputGrid = new Grid { Margin = new Thickness(2, 2, 2, 6) };
        inputGrid.Children.Add(_input);
        inputGrid.Children.Add(_placeholder);

        // Left of the toolbar: attach an image, and pull in the code selected in the active editor.
        var leftTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftTools.Children.Add(MakeIconButton("+", "attachHint", PickImages));
#pragma warning disable VSTHRD010
        leftTools.Children.Add(MakeIconButton("{}", "addSelection", AddSelection));
        Border agentButton = null!;
        agentButton = MakeIconButton("@", "pickAgent", () => ShowSubagentMenu(agentButton));
        leftTools.Children.Add(agentButton);
#pragma warning restore VSTHRD010

        // Right of the toolbar: the selectors kept tight against the primary button, so a narrow
        // panel never wraps them away from it.
        var rightTools = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _modelCombo.Margin = new Thickness(0, 0, 6, 0);
        _modeCombo.Margin = new Thickness(0, 0, 6, 0);
        _effortCombo.Margin = new Thickness(0, 0, 6, 0);
        rightTools.Children.Add(_modelCombo);
        rightTools.Children.Add(_modeCombo);
        rightTools.Children.Add(_effortCombo);
        rightTools.Children.Add(BuildPrimaryButton());

        var toolbar = new DockPanel { LastChildFill = false };
        DockPanel.SetDock(leftTools, Dock.Left);
        DockPanel.SetDock(rightTools, Dock.Right);
        toolbar.Children.Add(leftTools);
        toolbar.Children.Add(rightTools);

        var inner = new StackPanel();
        inner.Children.Add(_attachStrip);
        inner.Children.Add(inputGrid);
        inner.Children.Add(toolbar);

        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(12),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 8, 8, 8),
            Child = inner,
            AllowDrop = true,
        };
        inputBorder.SetResourceReference(Border.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        inputBorder.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        inputBorder.DragEnter += OnInputDragOver;
        inputBorder.DragOver += OnInputDragOver;
        inputBorder.DragLeave += (_, __) => inputBorder.BorderThickness = new Thickness(1);
        inputBorder.Drop += OnInputDrop;
        _inputBorder = inputBorder;

        // The "/" command menu opens above the input as the developer types a command name. Building the
        // list reads the project's .claude/commands folder; the panel is on the UI thread here and while
        // typing, so the solution-service read is safe.
        Popup slashPopup = BuildSlashPopup(inputBorder);
#pragma warning disable VSTHRD010
        _input.TextChanged += (_, __) => UpdateSlashPopup();
#pragma warning restore VSTHRD010

        // The bottom status bar: quiet mini-buttons on the left (the working folder for now; more join
        // it as those features land), the status text and running cost on the right.
        var folderButton = BuildStatusButton(PickFolder, out _folderLabel);
        // The panel is built on the UI thread; the folder caption reads the solution service safely here.
#pragma warning disable VSTHRD010
        Bind(() => _folderLabel!.Text = FolderCaption());
#pragma warning restore VSTHRD010

        // The undo control sits next to the folder; it stays hidden until a turn has a snapshot to revert.
        // UndoTurnAsync re-establishes the UI thread itself before any shell access.
#pragma warning disable VSTHRD010
        var undoButton = BuildStatusButton(() => _ = UndoTurnAsync(), out _undoLabel);
#pragma warning restore VSTHRD010
        undoButton.Visibility = Visibility.Collapsed;
        _undoButton = undoButton;
        Bind(() => { if (_undoLabel != null) _undoLabel.Text = Loc("undoTurn"); });

        // A one-click review of the working tree - sends a read-only "find issues" turn.
#pragma warning disable VSTHRD010
        Border reviewButton = BuildStatusButton(() => _ = SendReviewAsync(), out TextBlock reviewLabel);
#pragma warning restore VSTHRD010
        Bind(() => reviewLabel.Text = Loc("review"));

        var leftStatus = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        leftStatus.Children.Add(folderButton);
        leftStatus.Children.Add(undoButton);
        leftStatus.Children.Add(reviewButton);

        // The approval-bridge indicator on the right: a dot that brightens once the endpoint is live.
        _bridgeDot = new Border
        {
            Width = 6, Height = 6, CornerRadius = new CornerRadius(3),
            Background = Accent, Opacity = 0.25,
            Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center,
        };
        _bridgeLabel = new TextBlock { FontSize = 11, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center };
        _bridgeLabel.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => _bridgeLabel.Text = Loc(_approval != null ? "bridgeOn" : "bridgeOff"));
        var bridgePanel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        bridgePanel.Children.Add(_bridgeDot);
        bridgePanel.Children.Add(_bridgeLabel);

        var statusBar = new DockPanel { Margin = new Thickness(4, 7, 4, 0), LastChildFill = true };
        DockPanel.SetDock(leftStatus, Dock.Left);
        DockPanel.SetDock(bridgePanel, Dock.Right);
        statusBar.Children.Add(leftStatus);
        statusBar.Children.Add(bridgePanel);
        statusBar.Children.Add(_status);

        var composer = new StackPanel { Margin = new Thickness(12, 6, 12, 12) };
        composer.Children.Add(_workingStrip);
        composer.Children.Add(inputBorder);
        composer.Children.Add(statusBar);
        composer.Children.Add(slashPopup); // no layout footprint; renders in its own window
        return composer;
    }

    // The one primary button: Send while idle, Stop while a turn runs. Same accent, same spot, so the
    // eye doesn't hunt for a separate stop control.
    private Border BuildPrimaryButton()
    {
        _primaryLabel = new TextBlock
        {
            Foreground = OnAccent,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(() => _primaryLabel.Text = Loc(_busy ? "stop" : "send"));
        _primary = new Border
        {
            Child = _primaryLabel,
            Background = Accent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16, 6, 16, 6),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _primary.MouseEnter += (_, __) => _primary.Background = AccentHover;
        _primary.MouseLeave += (_, __) => _primary.Background = Accent;
        _primary.MouseLeftButtonUp += (_, __) => { if (_busy) _ = StopAsync(); else _ = SendAsync(); };
        return _primary;
    }

    // Enter sends; Shift+Enter inserts a newline. Ctrl+V pastes an image if the clipboard holds one.
    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        // While the "/" menu is open it owns the arrow, Enter, Tab and Escape keys: they navigate and
        // pick a command instead of moving the caret or sending the turn.
        if (_slashPopup != null && _slashPopup.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: MoveSlash(1); e.Handled = true; return;
                case Key.Up: MoveSlash(-1); e.Handled = true; return;
                case Key.Escape: HideSlash(); e.Handled = true; return;
                case Key.Enter:
                case Key.Tab:
                    if (_slashList?.SelectedItem is ListBoxItem it && it.Tag is SlashCommand cmd)
                    {
                        RunSlash(cmd);
                        e.Handled = true;
                        return;
                    }
                    break;
            }
        }

        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            e.Handled = true;
            _ = SendAsync();
            return;
        }

        if (e.Key == Key.V && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && ClipboardHasImage())
        {
            e.Handled = true; // don't also paste the image's file path as text
            AddClipboardImage();
        }
    }

    private async System.Threading.Tasks.Task SendAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(); // establish the UI thread
        string text = _input.Text?.Trim() ?? string.Empty;
        List<PendingImage> images = TakePending();
        if (text.Length == 0 && images.Count == 0) return;

        _input.Clear();
        AddUserBubble(text, images);
        bool firstInChat = _current.Messages.Count == 0;
        string stored = text.Length > 0 ? text : "(image)";
        _current.Messages.Add((true, stored));
        if (firstInChat) SetConversationTitle(_current, stored);

        List<ImageAttachment> payload = ToAttachments(images);

        // The input stays live during a turn so the next message can be composed. If a turn is
        // running, queue this one and send it when the turn ends, instead of dropping it or
        // interleaving two turns on one session.
        if (_busy)
        {
            _queue.Enqueue(new PendingTurn { Text = text, Images = payload });
            _status.Text = "Queued - it will send when the current turn ends.";
            return;
        }

        await RunTurnAsync(text, payload);
    }

    // Runs one turn: opens (or reuses) the session, adds the streaming reply bubble and sends the
    // text. Used both for a fresh message and for draining the queue.
    private async System.Threading.Tasks.Task RunTurnAsync(string text, List<ImageAttachment>? images)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        try
        {
            EnsureSession();
            // Snapshot the working tree before Claude can touch it, so this turn's file changes can be
            // reverted. Fast and non-destructive; it must complete before the turn starts editing.
#pragma warning disable VSTHRD010
            string snapshotDir = WorkingDirectory();
#pragma warning restore VSTHRD010
            await SnapshotBeforeTurnAsync(snapshotDir);
            // The reply bubble is opened lazily (on the first text delta or after a tool chip), not
            // up front, so a turn that opens with a tool call renders the chip before any text bubble.
            _streamingText = string.Empty;
            _streamingContainer = null;
            _streamingColumn = null;
            _streamingTextBlock = null;
            _renderPending = false;
            _renderTimer.Start();
            SetBusy(true);
            await _session!.SendAsync(text, images);
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

        EnsureApproval();
        UpdateBridgeStatus();
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
        if (_approval != null)
        {
            options.ApprovalPort = _approval.Port;
            options.ApprovalToken = _approval.Token;
        }
        _session.Start(WorkingDirectory(), options);
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
                if (!string.IsNullOrEmpty(e.SessionId)) OnUi(() => { _current.CliSessionId = e.SessionId; SaveConversations(); });
                if (!string.IsNullOrEmpty(e.Model)) OnUi(() => _status.Text = "Model: " + e.Model);
                break;

            case CliEventKind.StreamDelta:
                // Accumulate and mark dirty; the render timer repaints on its own cadence.
                if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText += e.Text;
                    _renderPending = true;
                }
                // The running token count feeds the working strip; the elapsed timer paints it.
                if (e.OutputTokens.HasValue && e.OutputTokens.Value > _turnTokens)
                {
                    _turnTokens = e.OutputTokens.Value;
                }
                break;

            case CliEventKind.Assistant:
                if (e.Tools != null)
                {
                    // A tool step: close the text so far into its own bubble, then drop a chip per call.
                    // Prefer the message's own text (authoritative) in case a delta lagged behind.
                    var tools = e.Tools;
                    string? preface = e.Text;
                    OnUi(() =>
                    {
                        if (!string.IsNullOrEmpty(preface)) _streamingText = preface!;
                        FlushAssistantText();
                        foreach (ToolCall call in tools) AddToolChip(call);
                    });
                }
                else if (!string.IsNullOrEmpty(e.Text))
                {
                    _streamingText = e.Text!;
                    _renderPending = true;
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    // Turn done: finalize whatever text is still open, then stop the render loop.
                    _renderTimer.Stop();
                    FlushAssistantText();
                    SetBusy(false);
                    if (e.TotalCostUsd.HasValue)
                    {
                        _sessionCost += e.TotalCostUsd.Value;
                        _status.Text = e.IsError
                            ? "Turn failed."
                            : string.Format("${0:0.0000} · {1} ${2:0.0000}", e.TotalCostUsd.Value, Loc("session"), _sessionCost);
                    }
                    SaveConversations();
                    DrainQueue();
                });
                break;
        }
    }

    // A user's turn: plain text in an accent bubble, right-aligned, with Copy and Edit under it.
    private void AddUserBubble(string text) => AddUserBubble(text, null);

    private void AddUserBubble(string text, List<PendingImage>? images)
    {
        var stack = new StackPanel();

        if (images != null && images.Count > 0)
        {
            var strip = new WrapPanel { Margin = new Thickness(0, 0, 0, text.Length > 0 ? 6 : 0) };
            foreach (PendingImage img in images)
            {
                strip.Children.Add(new Image
                {
                    Source = img.Thumb,
                    Height = 54,
                    Margin = new Thickness(0, 0, 6, 0),
                    Stretch = Stretch.Uniform,
                });
            }
            stack.Children.Add(strip);
        }

        if (text.Length > 0)
        {
            var body = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            body.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            stack.Children.Add(body);
        }

        StackPanel column = AddMessageColumn(stack, isUser: true);
        AppendActions(column, () => text, isUser: true);
    }

    // Claude's turn. While streaming, the text goes into one plain TextBlock (cheap to update on
    // every delta); the full markdown render - code boxes and all - runs once when the turn ends.
    private StackPanel AddAssistantBubble()
    {
        var container = new StackPanel();
        _streamingColumn = AddMessageColumn(container, isUser: false);

        var streaming = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 18 };
        streaming.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        container.Children.Add(streaming);
        _streamingTextBlock = streaming;
        _streamingContainer = container;
        return container;
    }

    // Opens a fresh reply bubble on demand - the turn doesn't create one up front, so a turn that
    // starts with a tool call shows the chip first and text lands in a bubble opened after it.
    private void EnsureStreamingBubble()
    {
        if (_streamingTextBlock == null) AddAssistantBubble();
    }

    // Finalizes the current reply bubble: renders its markdown, records it, and attaches Copy. Called
    // before a tool chip (so text and chips keep their order) and at the end of the turn. Idempotent.
    private void FlushAssistantText()
    {
        _renderPending = false;
        if (_streamingTextBlock != null && _streamingText.Length > 0)
        {
            if (_streamingContainer != null) RenderMarkdownInto(_streamingContainer, _streamingText);
            string finalText = _streamingText;
            _current.Messages.Add((false, finalText));
            if (_streamingColumn != null) AppendActions(_streamingColumn, () => finalText, isUser: false);
        }
        _streamingContainer = null;
        _streamingColumn = null;
        _streamingTextBlock = null;
        _streamingText = string.Empty;
    }

    // One tool call as a compact chip in the feed: an accent dot, the tool name, and a muted summary.
    private void AddToolChip(ToolCall call)
    {
        var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(14, 3, 8, 3) };
        line.Inlines.Add(new Run("●  ") { Foreground = Accent }); // leading dot
        line.Inlines.Add(new Run(call.Name) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(call.Summary))
        {
            var detail = new Run("  " + call.Summary) { FontFamily = MonoFont };
            line.Inlines.Add(detail);
        }
        line.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        line.Opacity = 0.85;
        _messages.Children.Add(line);
        ScrollToEndIfAtBottom();
    }

    // A completed assistant message restored from history: rendered and given a Copy action at once.
    private void AddStoredAssistant(string text)
    {
        var container = new StackPanel();
        StackPanel column = AddMessageColumn(container, isUser: false);
        RenderMarkdownInto(container, text);
        AppendActions(column, () => text, isUser: false);
    }

    // One message row, laid out as a flat feed rather than paired bubbles: role reads from shape, not
    // a label. The user's turn is a faint card with a 2px accent stripe down its left edge; Claude's
    // is bare markdown, indented to line up with the user's text. Returns the column for the actions.
    private StackPanel AddMessageColumn(UIElement content, bool isUser)
    {
        var column = new StackPanel { Margin = new Thickness(0, 6, 0, 2) };

        if (isUser)
        {
            column.Children.Add(new Border
            {
                Child = content,
                Background = UserFill,
                BorderBrush = Accent,
                BorderThickness = new Thickness(2, 0, 0, 0),
                CornerRadius = new CornerRadius(0, 6, 6, 0),
                Padding = new Thickness(12, 9, 12, 9),
            });
        }
        else
        {
            if (content is FrameworkElement fe) fe.Margin = new Thickness(14, 2, 8, 2);
            column.Children.Add(content);
        }

        _messages.Children.Add(column);
        ScrollToEnd();
        return column;
    }

    // A small Copy (and, for the user's own turn, Edit) strip under a message.
    private void AppendActions(StackPanel column, Func<string> getText, bool isUser)
    {
        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(14, 3, 4, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        var copy = MakeLink("copy", null);
        copy.MouseLeftButtonUp += (_, __) =>
        {
            try { Clipboard.SetText(getText()); copy.Text = Loc("copied"); } catch { }
        };
        actions.Children.Add(copy);

        if (isUser)
        {
            var edit = MakeLink("edit", () =>
            {
                _input.Text = getText();
                _input.CaretIndex = _input.Text.Length;
                _input.Focus();
            });
            actions.Children.Add(edit);
        }

        column.Children.Add(actions);
    }

    private TextBlock MakeLink(string key, Action? onClick)
    {
        var link = new TextBlock
        {
            FontSize = 10.5,
            Opacity = 0.5,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
        };
        link.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => link.Text = Loc(key));
        if (onClick != null) link.MouseLeftButtonUp += (_, __) => onClick();
        return link;
    }

    // Cheap per-delta update: just set the streaming TextBlock's text, no tree rebuild.
    private void RenderStreaming()
    {
        if (_streamingText.Length == 0) return;
        EnsureStreamingBubble();
        _streamingTextBlock!.Text = _streamingText;
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

        // Language caption on the left, a Copy affordance on the right.
        var copy = new TextBlock { Text = Loc("copy"), FontSize = 10.5, Opacity = 0.6, Cursor = Cursors.Hand };
        copy.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        copy.MouseLeftButtonUp += (_, __) =>
        {
            try { Clipboard.SetText(block.Text); copy.Text = Loc("copied"); }
            catch { /* clipboard busy - ignore */ }
        };

        var topRow = new DockPanel { Margin = new Thickness(2, 0, 0, 3) };
        DockPanel.SetDock(copy, Dock.Right);
        topRow.Children.Add(copy);
        if (!string.IsNullOrEmpty(block.Language))
        {
            var caption = new TextBlock { Text = block.Language, FontSize = 10.5, Opacity = 0.55 };
            caption.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            topRow.Children.Add(caption);
        }
        panel.Children.Add(topRow);

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
        if (_primaryLabel != null) _primaryLabel.Text = Loc(busy ? "stop" : "send");
        if (busy)
        {
            _status.Text = Loc("working");
            _turnStart = DateTime.UtcNow;
            _turnTokens = 0;
            _workingStrip.Visibility = Visibility.Visible;
            UpdateWorkingStrip();
            _elapsedTimer.Start();
        }
        else
        {
            _elapsedTimer.Stop();
            _workingStrip.Visibility = Visibility.Collapsed;
            _input.Focus();
        }
    }

    // Refreshes the working strip: "Claude is working... 12s - 2.9k tokens".
    private void UpdateWorkingStrip()
    {
        int secs = (int)(DateTime.UtcNow - _turnStart).TotalSeconds;
        string tokens = _turnTokens >= 1000
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}k", _turnTokens / 1000.0)
            : _turnTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _workingLabel.Text = string.Format("{0}  {1}s  ·  {2} {3}", Loc("working"), secs, tokens, Loc("tokens"));
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
        _streamingColumn = null;
        _streamingTextBlock = null;
        SetBusy(false);
        _status.Text = "Stopped - your next message starts a new session.";
    }

    // After a turn ends, send the next queued message (if any) as its own turn. Called from the UI
    // dispatcher; RunTurnAsync re-establishes the main thread before touching the shell.
    private void DrainQueue()
    {
        if (_busy || _queue.Count == 0) return;
        PendingTurn next = _queue.Dequeue();
        _ = RunTurnAsync(next.Text, next.Images);
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

    // Auto-follow the stream only when the user is already at the bottom; if they scrolled up to read
    // earlier text, don't yank them back down.
    private void ScrollToEndIfAtBottom()
    {
        if (_scroller.VerticalOffset >= _scroller.ScrollableHeight - 48) _scroller.ScrollToEnd();
    }

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
        Effort = (_effortCombo.SelectedItem as ComboBoxItem)?.Tag as string,
    };

    // Starts a fresh chat and switches to it.
    private void NewConversation()
    {
        var c = new Conversation();
        RegisterConversation(c, select: true);
        SwitchTo(c);
        SaveConversations();
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
            SaveConversations();
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
        SaveConversations();
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

    // Restores this workspace's chats from disk, or starts a single fresh one.
    private void LoadConversations()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _workspaceKey = SolutionDirectory();
        List<ConversationRecord> records = _store.Load(_workspaceKey);
        if (records.Count == 0)
        {
            RegisterConversation(_current, select: true);
            return;
        }

        Conversation? first = null;
        foreach (ConversationRecord rec in records)
        {
            var c = new Conversation { Title = rec.Title, CliSessionId = rec.CliSessionId };
            foreach (MessageRecord m in rec.Messages) c.Messages.Add((m.IsUser, m.Text));
            RegisterConversation(c, select: false);
            if (first == null) first = c;
        }

        if (first != null)
        {
            _current = first;
            _switching = true;
            if (first.Item != null) _convCombo.SelectedItem = first.Item;
            _switching = false;
            RebuildMessages();
        }
    }

    // Writes every chat (title, session id, messages) back to disk. Called whenever they change.
    private void SaveConversations()
    {
        var records = new List<ConversationRecord>();
        foreach (object obj in _convCombo.Items)
        {
            if (obj is ComboBoxItem it && it.Tag is Conversation c)
            {
                var rec = new ConversationRecord { Title = c.Title, CliSessionId = c.CliSessionId };
                foreach (var (isUser, text) in c.Messages)
                {
                    rec.Messages.Add(new MessageRecord { IsUser = isUser, Text = text });
                }
                records.Add(rec);
            }
        }
        _store.Save(_workspaceKey, records);
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
        _streamingColumn = null;
        _streamingTextBlock = null;

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
            if (isUser) AddUserBubble(text);
            else AddStoredAssistant(text);
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

    // Writes the deny floor plus, when the approval endpoint is up, the PreToolUse hook that routes
    // each tool call to an approval card. If it cannot be written the session still starts.
    private string? WriteSafetySettings()
    {
        try
        {
            string? hookCommand = (_approval != null && _hookScriptPath != null)
                ? "powershell -NoProfile -ExecutionPolicy Bypass -File \"" + _hookScriptPath + "\""
                : null;
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nlabs_claude_settings_" + Guid.NewGuid().ToString("n") + ".json");
            System.IO.File.WriteAllText(path, PermissionPolicy.BuildSettingsJson(null, hookCommand));
            return path;
        }
        catch
        {
            return null;
        }
    }

    // Brings up the approval endpoint and the little hook script that talks to it. Best-effort: if it
    // fails, sessions run without interactive cards (the deny floor still applies).
    private void EnsureApproval()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_approval != null) return;
        try
        {
            var svc = new ApprovalService();
            svc.Start();
            svc.Requested += OnApprovalRequested;
            _hookScriptPath = WriteHookScript();
            _approval = _hookScriptPath != null ? svc : null;
            if (_approval == null) svc.Dispose();
        }
        catch
        {
            _approval = null;
            _hookScriptPath = null;
        }
    }

    // The PreToolUse hook: reads the tool call on stdin, asks the panel over the loopback endpoint,
    // and writes the decision back. A tiny PowerShell relay so nothing has to be bundled.
    private static string? WriteHookScript()
    {
        try
        {
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "nlabs_claude_hook_" + Guid.NewGuid().ToString("n") + ".ps1");
            const string script =
                "$ErrorActionPreference = 'Stop'\r\n" +
                "try {\r\n" +
                "  $body = [Console]::In.ReadToEnd()\r\n" +
                "  $u = \"http://127.0.0.1:$($env:NLABS_APPROVAL_PORT)/permission\"\r\n" +
                "  $h = @{ 'x-nlabs-approval' = $env:NLABS_APPROVAL_TOKEN }\r\n" +
                "  $r = Invoke-WebRequest -Uri $u -Method Post -Body $body -ContentType 'application/json' -Headers $h -UseBasicParsing -TimeoutSec 310\r\n" +
                "  [Console]::Out.Write($r.Content)\r\n" +
                "} catch {\r\n" +
                "  [Console]::Out.Write('{\"hookSpecificOutput\":{\"hookEventName\":\"PreToolUse\",\"permissionDecision\":\"deny\",\"permissionDecisionReason\":\"Approval bridge unreachable.\"}}')\r\n" +
                "}\r\n";
            System.IO.File.WriteAllText(path, script);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // A tool wants to run. Auto-allow if the developer whitelisted it this session; else show a card.
    private void OnApprovalRequested(object sender, ApprovalRequestedEventArgs e)
    {
        string id = e.Id;
        HookRequest req = e.Request;
        OnUi(() =>
        {
            if (_alwaysAllow.Contains(req.ToolName)) { _approval?.Resolve(id, true); return; }
            ShowApprovalCard(id, req);
        });
    }

    // The approval card. A normal tool shows its name and parameters with Allow / Deny / Always. When
    // Claude presents a plan (ExitPlanMode in plan mode), it becomes a plan card: the plan rendered as
    // markdown, with Apply (proceed) and Keep planning (refine) instead.
    private void ShowApprovalCard(string id, HookRequest req)
    {
        bool isPlan = req.ToolName == "ExitPlanMode";

        var title = new TextBlock { FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        if (isPlan)
        {
            title.Inlines.Add(new Run(Loc("planReady")) { FontWeight = FontWeights.Bold, Foreground = Accent });
        }
        else
        {
            title.Inlines.Add(new Run(req.ToolName) { FontWeight = FontWeights.Bold, Foreground = Accent });
            title.Inlines.Add(new Run(" " + Loc("wantsToRun")));
        }
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        UIElement body;
        if (isPlan)
        {
            // Render the plan as markdown, scrolled if long, so a big plan stays readable in the card.
            var planPanel = new StackPanel { Margin = new Thickness(0, 6, 0, 8) };
            RenderMarkdownInto(planPanel, req.Plan ?? req.InputPreview);
            body = new ScrollViewer
            {
                Content = planPanel,
                MaxHeight = 300,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
        }
        else
        {
            var preview = new TextBox
            {
                Text = req.InputPreview,
                IsReadOnly = true,
                BorderThickness = new Thickness(0),
                Margin = new Thickness(0, 6, 0, 8),
                FontFamily = MonoFont,
                FontSize = 12,
                Background = Brushes.Transparent,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 160,
            };
            preview.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            body = preview;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var note = new TextBlock { Opacity = 0.7, VerticalAlignment = VerticalAlignment.Center };
        note.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var inner = new StackPanel();
        inner.Children.Add(title);
        inner.Children.Add(body);
        inner.Children.Add(buttons);

        var card = new Border
        {
            Child = inner,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(12, 10, 12, 10),
            Margin = new Thickness(0, 6, 0, 6),
            Background = UserFill,
        };
        card.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        void Decide(bool allow, bool always, string doneKey)
        {
            if (always) _alwaysAllow.Add(req.ToolName);
            _approval?.Resolve(id, allow);
            buttons.Children.Clear();
            note.Text = Loc(doneKey);
            buttons.Children.Add(note);
        }

        if (isPlan)
        {
            var applyBtn = MakeAccentButton("applyPlan", () => Decide(true, false, "planApplied"));
            applyBtn.Margin = new Thickness(0, 0, 8, 0);
            var keepBtn = MakeGhostButton("keepPlanning", () => Decide(false, false, "planKept"));
            buttons.Children.Add(applyBtn);
            buttons.Children.Add(keepBtn);
        }
        else
        {
            var allowBtn = MakeAccentButton("allow", () => Decide(true, false, "allowed"));
            allowBtn.Margin = new Thickness(0, 0, 8, 0);
            var denyBtn = MakeGhostButton("deny", () => Decide(false, false, "denied"));
            denyBtn.Margin = new Thickness(0, 0, 8, 0);
            var alwaysBtn = MakeGhostButton("always", () => Decide(true, true, "allowed"));
            buttons.Children.Add(allowBtn);
            buttons.Children.Add(denyBtn);
            buttons.Children.Add(alwaysBtn);
        }

        _messages.Children.Add(card);
        ScrollToEnd();
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
        SavePreferences();
    }

    private void OnAccentChanged()
    {
        int i = Array.FindIndex(Accents, a => a.Name == ((_accentCombo.SelectedItem as ComboBoxItem)?.Tag as string));
        if (i < 0) i = 0;
        ApplyAccent(Accents[i].Color);
        SavePreferences();
    }

    // Recolours the shared accent brushes in place; every element painted with them follows.
    private void ApplyAccent(Color c)
    {
        Accent.Color = c;
        AccentHover.Color = Lighten(c, 0.14);
        UserFill.Color = Color.FromArgb(0x1F, c.R, c.G, c.B);
    }

    private static Color Lighten(Color c, double t) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * t),
        (byte)(c.G + (255 - c.G) * t),
        (byte)(c.B + (255 - c.B) * t));

    // Applies the saved language and accent to the combos. The flag keeps the resulting change events
    // from writing the file straight back while we're still loading it.
    private void ApplyPreferences()
    {
        PanelPreferences p = _prefs.Load();

        SelectByTag(_langCombo, p.Language);
        SelectByTag(_accentCombo, p.Accent);
        // A no-op selection (already on the stored value) won't have fired the handler, so apply directly.
        _lang = (_langCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "en";
        foreach (Action apply in _localizers) apply();
        int ai = Array.FindIndex(Accents, a => a.Name == p.Accent);
        ApplyAccent(Accents[ai < 0 ? 0 : ai].Color);

        _prefsLoaded = true;
    }

    private void SavePreferences()
    {
        if (!_prefsLoaded) return;
        _prefs.Save(new PanelPreferences
        {
            Language = _lang,
            Accent = (_accentCombo.SelectedItem as ComboBoxItem)?.Tag as string ?? "Claude",
        });
    }

    private static void SelectByTag(ComboBox combo, string tag)
    {
        foreach (object item in combo.Items)
        {
            if (item is ComboBoxItem ci && (ci.Tag as string) == tag) { combo.SelectedItem = ci; return; }
        }
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

    // A single-glyph round button (the attach +), with a localized tooltip.
    private Border MakeIconButton(string glyph, string tipKey, Action onClick)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var button = new Border
        {
            Child = label,
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 6, 0),
        };
        Bind(() => button.ToolTip = Loc(tipKey));
        button.MouseEnter += (_, __) => button.Opacity = 0.6;
        button.MouseLeave += (_, __) => button.Opacity = 1.0;
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // A quiet status-bar button: muted text that brightens on hover. The label is returned so the
    // caller can keep its text current (the folder name, a status, a count).
    private Border BuildStatusButton(Action onClick, out TextBlock label)
    {
        label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var button = new Border
        {
            Child = label,
            Cursor = Cursors.Hand,
            Padding = new Thickness(0, 0, 10, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        button.MouseEnter += (_, __) => button.Opacity = 0.55;
        button.MouseLeave += (_, __) => button.Opacity = 1.0;
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // The working directory Claude runs in: an explicitly chosen folder if set, else the open
    // solution's directory (empty when neither - the CLI then runs in its default location).
    private string WorkingDirectory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!string.IsNullOrEmpty(_workingFolder) && Directory.Exists(_workingFolder)) return _workingFolder!;
        return SolutionDirectory();
    }

    private string FolderCaption()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string dir = WorkingDirectory();
        if (string.IsNullOrEmpty(dir)) return "📁 " + Loc("pickFolder");
        return "📁 " + (Path.GetFileName(dir.TrimEnd('\\', '/')) ?? dir);
    }

    // Lets the developer point the session at a folder - the main path when no solution is open. The
    // running session is dropped so the next turn starts in the new directory.
    private void PickFolder()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        using (var dlg = new System.Windows.Forms.FolderBrowserDialog())
        {
            string current = WorkingDirectory();
            if (!string.IsNullOrEmpty(current)) dlg.SelectedPath = current;
            if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

            _workingFolder = dlg.SelectedPath;
            _workspaceKey = _workingFolder;
            _session?.Dispose();
            _session = null;
            _snapshotRef = null; // a snapshot from the old folder no longer applies
            UpdateUndoButton();
            if (_folderLabel != null) _folderLabel.Text = FolderCaption();
            _status.Text = Loc("folderSet");
        }
    }

    // --- Checkpoint / undo ---------------------------------------------------------------------

    // Snapshots the working tree before a turn so its file changes can be reverted. "git stash create"
    // records the current state as a dangling commit WITHOUT touching the working tree, index or stash
    // list; a clean tree returns nothing, so we fall back to HEAD. Untracked new files are not captured,
    // and undo only ever restores tracked files - it never deletes - which keeps it safe.
    private async System.Threading.Tasks.Task SnapshotBeforeTurnAsync(string dir)
    {
        _snapshotRef = null;
        if (!string.IsNullOrEmpty(dir))
        {
            var (inside, flag) = await RunGitAsync(dir, "rev-parse --is-inside-work-tree");
            if (inside && flag == "true")
            {
                var (made, sha) = await RunGitAsync(dir, "stash create");
                if (made && sha.Length > 0)
                {
                    _snapshotRef = sha;
                }
                else
                {
                    var (okHead, head) = await RunGitAsync(dir, "rev-parse HEAD");
                    _snapshotRef = okHead && head.Length > 0 ? head : null;
                }
            }
        }
        UpdateUndoButton();
    }

    // Reverts the tracked files changed since the snapshot back to it - only after the developer
    // confirms, since it overwrites their current working-tree versions of those files.
    private async System.Threading.Tasks.Task UndoTurnAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (_snapshotRef == null) return;
        string dir = WorkingDirectory();
        if (string.IsNullOrEmpty(dir)) return;

        MessageBoxResult confirm = MessageBox.Show(
            Loc("undoConfirm"), Loc("undoTurn"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var (ok, _) = await RunGitAsync(dir, "checkout " + _snapshotRef + " -- .");
        _status.Text = Loc(ok ? "undoDone" : "undoFail");
        _snapshotRef = null;
        UpdateUndoButton();
    }

    private void UpdateUndoButton()
    {
        if (_undoButton != null) _undoButton.Visibility = _snapshotRef != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // Reflects whether the approval bridge is live: the dot brightens and the label flips. No port or
    // other endpoint detail is shown - only that approvals are wired up.
    private void UpdateBridgeStatus()
    {
        bool on = _approval != null;
        if (_bridgeDot != null) _bridgeDot.Opacity = on ? 1.0 : 0.25;
        if (_bridgeLabel != null) _bridgeLabel.Text = Loc(on ? "bridgeOn" : "bridgeOff");
    }

    // Sends a read-only review of the working tree as a turn, reusing the normal send path (so it
    // queues behind a running turn rather than interleaving).
    private async System.Threading.Tasks.Task SendReviewAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _input.Text = Loc("reviewPrompt");
        await SendAsync();
    }

    // Runs a git command in dir off the UI thread and returns (exit-zero, trimmed stdout). Never throws:
    // no git, no repo or a timeout all come back as (false, ""), which simply disables the undo control.
    private static async System.Threading.Tasks.Task<(bool ok, string output)> RunGitAsync(string dir, string args)
    {
        return await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("git", args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using (System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return (false, string.Empty);
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd(); // drain so the pipe never blocks the process
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } return (false, string.Empty); }
                    return (p.ExitCode == 0, output.Trim());
                }
            }
            catch { return (false, string.Empty); }
        });
    }

    // --- Slash commands ------------------------------------------------------------------------

    // Builds the "/" menu popup once: a themed list anchored above the input. A click picks the item
    // under the pointer; the arrow keys and Enter are handled in OnInputKeyDown while it is open.
    private Popup BuildSlashPopup(UIElement anchor)
    {
        _slashList = new ListBox
        {
            MaxHeight = 260,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };
        _slashList.SetResourceReference(ListBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _slashList.PreviewMouseLeftButtonUp += (_, __) =>
        {
            if (_slashList.SelectedItem is ListBoxItem it && it.Tag is SlashCommand c) RunSlash(c);
        };

        var frame = new Border
        {
            Child = _slashList,
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4),
            MinWidth = 340,
        };
        frame.SetResourceReference(Border.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        frame.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        _slashPopup = new Popup
        {
            Child = frame,
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            VerticalOffset = -6,
        };
        return _slashPopup;
    }

    // Opens/updates the menu as the developer types. It shows only while the line is a bare command
    // being typed: it starts with "/" and has no space or newline yet (once an argument is typed the
    // command is chosen and the menu gets out of the way).
    private void UpdateSlashPopup()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string text = _input.Text ?? string.Empty;
        if (!text.StartsWith("/") || text.IndexOf(' ') >= 0 || text.IndexOf('\n') >= 0)
        {
            HideSlash();
            return;
        }

        string fragment = text.Substring(1);
        var matches = new List<SlashCommand>();
        foreach (SlashCommand c in SlashCommands())
        {
            if (c.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) matches.Add(c);
        }
        if (matches.Count == 0) { HideSlash(); return; }

        PopulateSlash(matches);
        if (_slashPopup != null) _slashPopup.IsOpen = true;
    }

    // Fills the list with the current matches, the first one preselected so Enter picks it at once.
    private void PopulateSlash(List<SlashCommand> matches)
    {
        if (_slashList == null) return;
        _slashList.Items.Clear();
        foreach (SlashCommand c in matches)
        {
            var name = new TextBlock { Text = "/" + c.Name, FontWeight = FontWeights.SemiBold, FontSize = 12.5 };
            name.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            var desc = new TextBlock { Text = c.Description, FontSize = 11, Opacity = 0.6, TextTrimming = TextTrimming.CharacterEllipsis };
            desc.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var row = new StackPanel { Margin = new Thickness(6, 3, 6, 3) };
            row.Children.Add(name);
            if (!string.IsNullOrEmpty(c.Description)) row.Children.Add(desc);

            _slashList.Items.Add(new ListBoxItem { Content = row, Tag = c, Padding = new Thickness(2) });
        }
        _slashList.SelectedIndex = 0;
    }

    private void MoveSlash(int delta)
    {
        if (_slashList == null || _slashList.Items.Count == 0) return;
        int i = _slashList.SelectedIndex + delta;
        if (i < 0) i = _slashList.Items.Count - 1;
        else if (i >= _slashList.Items.Count) i = 0;
        _slashList.SelectedIndex = i;
        if (_slashList.SelectedItem is ListBoxItem it) it.BringIntoView();
    }

    private void HideSlash()
    {
        if (_slashPopup != null) _slashPopup.IsOpen = false;
    }

    // Panel commands run locally and clear the input; forward commands drop "/name " into the input so
    // an argument can be typed, then Enter sends the whole line for the CLI to expand.
    private void RunSlash(SlashCommand c)
    {
        HideSlash();
        if (c.Panel != null)
        {
            _input.Clear();
            c.Panel();
        }
        else
        {
            _input.Text = "/" + c.Name + " ";
            _input.CaretIndex = _input.Text.Length;
        }
        _input.Focus();
    }

    // The menu contents: the panel's own actions first, then any custom commands discovered in the
    // project's .claude/commands folder (which the CLI expands when the line is sent).
    private List<SlashCommand> SlashCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var list = new List<SlashCommand>
        {
            new SlashCommand { Name = "new", Description = Loc("cmdNew"), Panel = NewConversation },
            new SlashCommand { Name = "clear", Description = Loc("cmdClear"), Panel = ClearCurrentChat },
        };
        list.AddRange(DiscoverProjectCommands());
        return list;
    }

    // Reads the project's custom slash commands from .claude/commands (nested folders become "dir:name",
    // matching the CLI's own naming). Best-effort: an unreadable tree just yields no extra commands.
    private List<SlashCommand> DiscoverProjectCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var result = new List<SlashCommand>();
        try
        {
            string dir = WorkingDirectory();
            if (string.IsNullOrEmpty(dir)) return result;
            string commandsDir = Path.Combine(dir, ".claude", "commands");
            if (!Directory.Exists(commandsDir)) return result;

            foreach (string file in Directory.EnumerateFiles(commandsDir, "*.md", SearchOption.AllDirectories))
            {
                string rel = file.Substring(commandsDir.Length).TrimStart('\\', '/');
                string name = rel.Substring(0, rel.Length - ".md".Length).Replace('\\', ':').Replace('/', ':');
                if (name.Length == 0) continue;
                result.Add(new SlashCommand { Name = name, Description = FirstMeaningfulLine(file) });
            }
        }
        catch { /* unreadable tree - no custom commands */ }
        return result;
    }

    // A short description for a command file: its frontmatter "description:", else the first real line
    // of body text. Reads only the head of the file and trims to a single tidy line.
    private static string FirstMeaningfulLine(string file)
    {
        try
        {
            bool inFrontmatter = false;
            int seen = 0;
            foreach (string raw in File.ReadLines(file))
            {
                if (++seen > 40) break;
                string line = raw.Trim();
                if (seen == 1 && line == "---") { inFrontmatter = true; continue; }
                if (inFrontmatter)
                {
                    if (line == "---") { inFrontmatter = false; continue; }
                    if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        return Tidy(line.Substring("description:".Length));
                    continue;
                }
                if (line.Length == 0) continue;
                return Tidy(line.TrimStart('#', ' '));
            }
        }
        catch { /* unreadable - no description */ }
        return string.Empty;
    }

    private static string Tidy(string s)
    {
        s = s.Trim().Trim('"', '\'');
        if (s.Length > 72) s = s.Substring(0, 72).TrimEnd() + "...";
        return s;
    }

    // Resets the current chat: same effect as the CLI's /clear - drop the running session and forget
    // this chat's messages and its resume id, so the next message starts a clean context here.
    private void ClearCurrentChat()
    {
        _renderTimer.Stop();
        _renderPending = false;
        _queue.Clear();
        _session?.Dispose();
        _session = null;
        _streamingContainer = null;
        _streamingColumn = null;
        _streamingTextBlock = null;

        _current.Messages.Clear();
        _current.CliSessionId = null;
        SetConversationTitle(_current, "New chat");
        ClearTasks();
        RebuildMessages();
        SetBusy(false);
        _status.Text = Loc("newChat");
        SaveConversations();
    }

    // --- Subagents -----------------------------------------------------------------------------

    // Opens a menu of the subagents defined for this project and the developer, anchored to the button.
    // Picking one drops a directive into the input so the CLI delegates that part of the turn to it.
    private void ShowSubagentMenu(UIElement anchor)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top };
        List<(string Name, string Description)> agents = DiscoverSubagents();
        if (agents.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Loc("noAgents"), IsEnabled = false });
        }
        else
        {
            foreach ((string Name, string Description) a in agents)
            {
                string name = a.Name;
                var item = new MenuItem { Header = name };
                if (!string.IsNullOrEmpty(a.Description)) item.ToolTip = a.Description;
                item.Click += (_, __) => InsertSubagent(name);
                menu.Items.Add(item);
            }
        }
        menu.IsOpen = true;
    }

    // Prepends "Use the <name> subagent to " so the developer just finishes the sentence with the task.
    private void InsertSubagent(string name)
    {
        string directive = "Use the " + name + " subagent to ";
        _input.Text = directive + (_input.Text ?? string.Empty);
        _input.CaretIndex = _input.Text.Length;
        _input.Focus();
    }

    // Discovers subagents from the project's and the developer's .claude/agents folders, de-duplicated
    // by name (project wins) and sorted. Best-effort: an unreadable folder just contributes nothing.
    private List<(string Name, string Description)> DiscoverSubagents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in SubagentRoots())
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                foreach (string file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
                {
                    string name = FrontmatterValue(file, "name") ?? Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    found.Add((name, FrontmatterValue(file, "description") ?? string.Empty));
                }
            }
            catch { /* unreadable folder - skip it */ }
        }
        found.Sort((x, y) => string.Compare(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase));
        return found;
    }

    private IEnumerable<string> SubagentRoots()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string dir = WorkingDirectory();
        if (!string.IsNullOrEmpty(dir)) yield return Path.Combine(dir, ".claude", "agents");
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home)) yield return Path.Combine(home, ".claude", "agents");
    }

    // Reads one frontmatter value (e.g. name, description) from the head of a command/agent .md file.
    private static string? FrontmatterValue(string file, string key)
    {
        try
        {
            int seen = 0;
            bool inFrontmatter = false;
            foreach (string raw in File.ReadLines(file))
            {
                if (++seen > 60) break;
                string line = raw.Trim();
                if (seen == 1) { if (line == "---") { inFrontmatter = true; continue; } return null; }
                if (!inFrontmatter || line == "---") break;
                if (line.StartsWith(key + ":", StringComparison.OrdinalIgnoreCase))
                    return Tidy(line.Substring(key.Length + 1));
            }
        }
        catch { /* unreadable - no value */ }
        return null;
    }

    // --- Attachments ---------------------------------------------------------------------------

    // The image formats the model accepts, and the extensions that map to each media type.
    private static readonly Dictionary<string, string> ImageTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif", [".webp"] = "image/webp",
    };

    private void PickImages()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "Images|*.png;*.jpg;*.jpeg;*.gif;*.webp",
        };
        if (dlg.ShowDialog() != true) return;
        foreach (string path in dlg.FileNames) AddImageFromFile(path);
    }

    // The file extensions that map to a fenced-code language tag, for the selection block.
    private static readonly Dictionary<string, string> CodeLanguages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        [".cs"] = "csharp", [".ts"] = "ts", [".tsx"] = "tsx", [".js"] = "js", [".jsx"] = "jsx",
        [".html"] = "html", [".css"] = "css", [".scss"] = "scss", [".json"] = "json", [".xml"] = "xml",
        [".py"] = "python", [".sql"] = "sql", [".sh"] = "bash", [".ps1"] = "powershell", [".razor"] = "razor",
        [".java"] = "java", [".go"] = "go", [".rs"] = "rust", [".yml"] = "yaml", [".yaml"] = "yaml", [".md"] = "markdown",
    };

    // Drops the code selected in the active editor into the input as a fenced block, captioned with the
    // file and line range so Claude has the reference. The main path for "look at this bit of code".
    private void AddSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        (string? code, string? caption, string? ext) = ActiveSelection();
        if (string.IsNullOrEmpty(code))
        {
            _status.Text = Loc("noSelection");
            return;
        }

        string lang = ext != null && CodeLanguages.TryGetValue(ext, out string l) ? l : string.Empty;
        string block = (caption != null ? caption + "\n" : string.Empty)
            + "```" + lang + "\n" + code!.TrimEnd('\r', '\n') + "\n```\n";

        int at = _input.CaretIndex;
        string existing = _input.Text ?? string.Empty;
        if (at > 0 && at <= existing.Length && existing.Length > 0 && existing[at - 1] != '\n') block = "\n" + block;
        _input.Text = existing.Insert(Math.Min(at, existing.Length), block);
        _input.CaretIndex = Math.Min(at + block.Length, _input.Text.Length);
        _input.Focus();
    }

    // Reads the active document's current selection: its text, a "file:line" caption and the file's
    // extension. Empty when no document is open or nothing is selected.
    private static (string? code, string? caption, string? ext) ActiveSelection()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        try
        {
            if (Package.GetGlobalService(typeof(DTE)) is DTE2 dte && dte.ActiveDocument != null &&
                dte.ActiveDocument.Selection is EnvDTE.TextSelection sel)
            {
                string text = sel.Text ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text)) return (null, null, null);
                string file = dte.ActiveDocument.Name ?? "selection";
                int top = sel.TopPoint.Line, bottom = sel.BottomPoint.Line;
                string caption = top == bottom ? file + ":" + top : file + ":" + top + "-" + bottom;
                string ext = System.IO.Path.GetExtension(file);
                return (text, caption, ext);
            }
        }
        catch { /* no active document or selection unavailable */ }
        return (null, null, null);
    }

    private void OnInputDragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.Bitmap);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok && _inputBorder != null) _inputBorder.BorderThickness = new Thickness(2);
        e.Handled = true;
    }

    private void OnInputDrop(object sender, DragEventArgs e)
    {
        if (_inputBorder != null) _inputBorder.BorderThickness = new Thickness(1);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (string f in files)
            {
                if (ImageTypes.ContainsKey(Path.GetExtension(f))) AddImageFromFile(f);
            }
        }
        else if (e.Data.GetData(DataFormats.Bitmap) is BitmapSource bmp)
        {
            AddImageFromBitmap(bmp);
        }
        e.Handled = true;
    }

    private static bool ClipboardHasImage()
    {
        try { return Clipboard.ContainsImage(); } catch { return false; }
    }

    private void AddClipboardImage()
    {
        try
        {
            BitmapSource? bmp = Clipboard.GetImage();
            if (bmp != null) AddImageFromBitmap(bmp);
        }
        catch { /* clipboard busy or empty - nothing to attach */ }
    }

    private void AddImageFromFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > 8 * 1024 * 1024) return; // skip missing or oversize files
            byte[] bytes = File.ReadAllBytes(path);
            string media = ImageTypes.TryGetValue(Path.GetExtension(path), out string m) ? m : "image/png";
            AddPending(new PendingImage
            {
                Base64 = Convert.ToBase64String(bytes),
                MediaType = media,
                Thumb = Thumbnail(path),
            });
        }
        catch { /* unreadable file - ignore */ }
    }

    private void AddImageFromBitmap(BitmapSource bmp)
    {
        try
        {
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bmp));
            using (var ms = new MemoryStream())
            {
                encoder.Save(ms);
                AddPending(new PendingImage
                {
                    Base64 = Convert.ToBase64String(ms.ToArray()),
                    MediaType = "image/png",
                    Thumb = bmp,
                });
            }
        }
        catch { /* couldn't encode - ignore */ }
    }

    // A small decoded copy for the chip and echo, so we don't hold the full image in the visual tree.
    private static ImageSource Thumbnail(string path)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelHeight = 108;
        bmp.UriSource = new Uri(path);
        bmp.EndInit();
        bmp.Freeze();
        return bmp;
    }

    private void AddPending(PendingImage img)
    {
        _pending.Add(img);
        RefreshAttachStrip();
    }

    // Removes the pending images from the strip and hands them back so the turn can send them.
    private List<PendingImage> TakePending()
    {
        var taken = new List<PendingImage>(_pending);
        _pending.Clear();
        RefreshAttachStrip();
        return taken;
    }

    private static List<ImageAttachment> ToAttachments(List<PendingImage> images)
    {
        var list = new List<ImageAttachment>();
        foreach (PendingImage img in images)
        {
            list.Add(new ImageAttachment { MediaType = img.MediaType, Base64Data = img.Base64 });
        }
        return list;
    }

    // Rebuilds the row of removable thumbnail chips under the composer, hiding it when empty.
    private void RefreshAttachStrip()
    {
        _attachStrip.Children.Clear();
        foreach (PendingImage img in _pending)
        {
            PendingImage captured = img;
            var thumb = new Image { Source = img.Thumb, Height = 46, Stretch = Stretch.Uniform };
            var remove = new TextBlock
            {
                Text = "x",
                FontSize = 12,
                Foreground = OnAccent,
                Padding = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
            };
            var badge = new Border
            {
                Background = Accent,
                CornerRadius = new CornerRadius(7),
                Margin = new Thickness(0, 0, 0, 0),
                Child = remove,
            };
            var overlay = new Grid { Margin = new Thickness(0, 0, 6, 0) };
            overlay.Children.Add(thumb);
            overlay.Children.Add(badge);
            badge.HorizontalAlignment = HorizontalAlignment.Right;
            badge.VerticalAlignment = VerticalAlignment.Top;
            remove.MouseLeftButtonUp += (_, __) => { _pending.Remove(captured); RefreshAttachStrip(); };
            _attachStrip.Children.Add(overlay);
        }
        _attachStrip.Visibility = _pending.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _approval?.Dispose();
        _approval = null;
    }
}
