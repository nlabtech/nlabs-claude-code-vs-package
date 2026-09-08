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

    // Windows' own UI icon font - the vocabulary Visual Studio's toolbars are drawn from. Emoji were
    // the obvious shortcut and the wrong one: they render as multicoloured stickers that ignore the
    // theme and the accent, and sit beside Solution Explorer looking like someone else's product.
    private static readonly FontFamily IconFont =
        new FontFamily("Segoe MDL2 Assets, Segoe Fluent Icons, Segoe UI Symbol");

    // The glyphs used, by role. Written as escapes so this file stays plain ASCII: they live in the
    private const string IconAttach = "\uE723";     // paperclip
    private const string IconSelection = "\uE943";  // braces
    private const string IconSubagent = "\uEA86";   // puzzle piece
    private const string IconMic = "\uE720";        // microphone
    private const string IconNew = "\uE710";        // plus
    private const string IconRename = "\uE70F";     // pencil
    private const string IconDelete = "\uE74D";     // waste basket
    private const string IconFolder = "\uED25";     // open folder
    private const string IconUndo = "\uE7A7";       // undo arrow
    private const string IconReview = "\uE721";     // magnifier
    private const string IconModel = "\uE734";      // star
    private const string IconPermission = "\uE72E"; // padlock
    private const string IconEffort = "\uE945";     // lightning bolt
    private const string IconLanguage = "\uE774";   // globe
    private const string IconAccent = "\uE790";     // palette
    // A neutral grey wash rather than a theme colour: eight percent of mid-grey darkens a light
    // background and lightens a dark one by the same amount, so one brush suits both themes.
    private static readonly Brush CardFill = Frozen(Color.FromArgb(0x14, 0x80, 0x80, 0x80));
    // The one colour outside the accent: danger. A high-risk approval must not read as brand colour.
    private static readonly Brush HighRisk = Frozen(Color.FromRgb(0xC0, 0x39, 0x2B));

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

    // One row of the completion popup, whichever token opened it. Panel commands run locally; every
    // other entry replaces the token being typed with Insert.
    private sealed class CompletionItem
    {
        public string Label = string.Empty;
        public string Description = string.Empty;
        public string Insert = string.Empty;
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
                ["thinking"] = "Thinking...", ["thoughtFor"] = "Thought for {0}s",
                ["thinkingTokens"] = "thinking", ["outTokens"] = "out", ["inTokens"] = "in",
                ["cacheTokens"] = "cached", ["sessionTotals"] = "This session ({0} turns)",
                ["newChat"] = "New chat - type a message to begin.",
                ["switched"] = "Switched - your next message resumes this chat.", ["tasks"] = "Tasks",
                ["copy"] = "Copy", ["copied"] = "Copied", ["session"] = "session",
                ["allow"] = "Allow", ["deny"] = "Deny", ["always"] = "Always allow",
                ["wantsToRun"] = "wants to run", ["allowed"] = "Allowed", ["denied"] = "Denied",
                ["edit"] = "Edit", ["accent"] = "Accent", ["attachHint"] = "Attach an image",
                ["tokens"] = "tokens", ["defaultEffort"] = "Effort: default",
                ["pickFolder"] = "Pick folder", ["folderSet"] = "Folder set - your next message starts here.",
                ["cmdNew"] = "Start a new chat", ["cmdClear"] = "Clear this chat and its context",
                ["cmdModel"] = "Choose the model", ["cmdEffort"] = "Choose the thinking effort",
                ["cmdPermission"] = "Choose how tools are approved", ["cmdLanguage"] = "Change the panel language",
                ["cmdAgents"] = "Delegate part of the turn to a subagent", ["cmdAttach"] = "Attach an image",
                ["cmdFolder"] = "Change the working folder", ["cmdReview"] = "Review the uncommitted changes",
                ["cmdUndo"] = "Undo the last turn's file changes",
                ["cmdInit"] = "Write a CLAUDE.md for this project", ["cmdCompact"] = "Summarise the context so far",
                ["cmdContext"] = "Show what is filling the context", ["cmdCost"] = "Show this session's cost",
                ["cmdMemory"] = "Edit the memory files", ["cmdMcp"] = "Show the MCP servers",
                ["cmdTodos"] = "Show the current task list", ["cmdStatus"] = "Show the CLI's status",
                ["cmdDoctor"] = "Check the installation", ["cmdHelp"] = "List the CLI's own commands",
                ["cmdProject"] = "Project command", ["cmdUser"] = "Your command",
                ["commands"] = "Commands", ["projectCommands"] = "Project commands",
                ["addSelection"] = "Add the editor selection", ["noSelection"] = "Select some code in the editor first.",
                ["undoTurn"] = "Undo turn",
                ["undoConfirm"] = "Revert the tracked files changed in the last turn to their state before it? New files are left in place.",
                ["undoDone"] = "Reverted the last turn's file changes.", ["undoFail"] = "Could not revert - see your git working tree.",
                ["planReady"] = "Claude has a plan", ["applyPlan"] = "Apply plan", ["keepPlanning"] = "Keep planning",
                ["planApplied"] = "Applying the plan...", ["planKept"] = "Still planning...",
                ["pickAgent"] = "Use a subagent", ["noAgents"] = "No subagents found",
                ["review"] = "Review", ["bridgeOn"] = "Approvals on", ["bridgeOff"] = "Approvals off",
                ["riskLow"] = "Low risk", ["riskMedium"] = "Changes files", ["riskHigh"] = "High risk",
                ["bridgeOnWhat"] = "Claude asks here before it edits a file or runs a command, and you answer in the panel.",
                ["bridgeOffWhat"] = "Nothing is asking for approval, so the CLI follows its own permission settings.",
                ["newModel"] = "The CLI offers a newer model: {0}",
                ["micHint"] = "Dictate a message", ["recording"] = "Recording - click the mic again to stop.",
                ["transcribing"] = "Transcribing...", ["micFailed"] = "No microphone was available.",
                ["sttNotSet"] = "Set a speech-to-text command in Tools > Options > Claude Code (nLabtech).",
                ["sttFailed"] = "The speech-to-text command failed.", ["sttEmpty"] = "Nothing was recognised.",
                ["bypassMode"] = "Bypass permissions",
                ["imageZoom"] = "Click to see it full size", ["imageTooMany"] = "Up to {0} images per message.",
                ["agentProject"] = "This project", ["agentUser"] = "Yours", ["subagent"] = "Subagent",
                ["latestOf"] = "{0} (latest)", ["usageTitle"] = "Subscription usage",
                ["win5h"] = "Session (5 hours)", ["win7d"] = "Weekly (7 days)", ["win7dOpus"] = "Weekly (Opus)",
                ["short5h"] = "5h", ["short7d"] = "7d", ["short7dOpus"] = "7d Opus", ["percent"] = "{0}%",
                ["queued"] = "Queued - it will send when the current turn ends.",
                ["cliMissing"] = "The Claude CLI was not found. Install it, or set its path in Tools > Options > Claude Code (nLabtech).",
                ["cliStart"] = "The Claude CLI could not be started:", ["cliLost"] = "The session ended.",
                ["turnFailed"] = "Turn failed.",
                ["deleteConfirm"] = "Delete this chat and its messages? This cannot be undone.", ["cancel"] = "Cancel",
                ["emptyTitle"] = "Claude Code, in Visual Studio",
                ["emptyBody"] = "Ask a question, describe a change, or hand over a task. Claude works in the folder shown below and asks before it runs anything.",
                ["hintSlash"] = "commands - the panel's own and the CLI's",
                ["hintAt"] = "mention a file or folder from this workspace",
                ["hintImage"] = "attach an image, or just paste one",
                ["hintMic"] = "dictate instead of typing",
                ["resetsIn"] = "resets in", ["resetSoon"] = "resetting now",
                ["unitDay"] = "d", ["unitHour"] = "h", ["unitMinute"] = "m",
                ["limitNear"] = "Near the limit.", ["limitUnknown"] = "No usage reported yet.",
                ["stopped"] = "Stopped - your next message starts a new session.", ["connected"] = "Connected.",
                ["stalled"] = "still working",
                ["workingVerbs"] = "Thinking...|Reading...|Working...|Writing...|Checking...",
                ["micChecking"] = "Looking for a speech engine...",
                ["micSaveFailed"] = "The recording could not be saved.",
                ["sttTimeout"] = "Transcription took too long and was stopped.",
                ["sttNoEngine"] = "No speech engine found. Install one (pip install faster-whisper) or set a command in Tools > Options > Claude Code (nLabtech).",
                ["reviewPrompt"] = "Review my current uncommitted changes for bugs, security issues, and simple cleanups. Do not modify any files - just report your findings.",
            },
            ["tr"] = new System.Collections.Generic.Dictionary<string, string>
            {
                ["placeholder"] = "Claude'a bir sey sor - Enter gonderir", ["model"] = "Model", ["permission"] = "Izin",
                ["chat"] = "Sohbet", ["language"] = "Dil", ["new"] = "Yeni", ["delete"] = "Sil", ["rename"] = "Yeniden adlandir",
                ["send"] = "Gonder", ["stop"] = "Durdur", ["you"] = "Sen", ["assistant"] = "Claude",
                ["defaultModel"] = "Varsayilan model", ["askEach"] = "Her seferinde sor", ["acceptEdits"] = "Duzenlemeleri kabul et", ["planMode"] = "Plan modu",
                ["hello"] = "Bir mesaj yaz, Enter'a bas.", ["working"] = "Claude calisiyor...",
                ["thinking"] = "Dusunuyor...", ["thoughtFor"] = "{0} sn dusundu",
                ["thinkingTokens"] = "dusunme", ["outTokens"] = "cikis", ["inTokens"] = "giris",
                ["cacheTokens"] = "onbellek", ["sessionTotals"] = "Bu oturum ({0} tur)",
                ["newChat"] = "Yeni sohbet - baslamak icin bir mesaj yaz.",
                ["switched"] = "Gecildi - sonraki mesajin bu sohbeti surdurur.", ["tasks"] = "Gorevler",
                ["copy"] = "Kopyala", ["copied"] = "Kopyalandi", ["session"] = "oturum",
                ["allow"] = "Izin ver", ["deny"] = "Reddet", ["always"] = "Hep izin ver",
                ["wantsToRun"] = "calistirmak istiyor", ["allowed"] = "Izin verildi", ["denied"] = "Reddedildi",
                ["edit"] = "Duzenle", ["accent"] = "Vurgu", ["attachHint"] = "Gorsel ekle",
                ["tokens"] = "token", ["defaultEffort"] = "Efor: varsayilan",
                ["pickFolder"] = "Klasor sec", ["folderSet"] = "Klasor secildi - sonraki mesajin burada baslar.",
                ["cmdNew"] = "Yeni bir sohbet baslat", ["cmdClear"] = "Bu sohbeti ve baglamini temizle",
                ["cmdModel"] = "Model sec", ["cmdEffort"] = "Dusunme eforunu sec",
                ["cmdPermission"] = "Araclarin nasil onaylanacagini sec", ["cmdLanguage"] = "Panel dilini degistir",
                ["cmdAgents"] = "Turun bir parcasini alt ajana devret", ["cmdAttach"] = "Gorsel ekle",
                ["cmdFolder"] = "Calisma klasorunu degistir", ["cmdReview"] = "Commit edilmemis degisiklikleri incele",
                ["cmdUndo"] = "Son turun dosya degisikliklerini geri al",
                ["cmdInit"] = "Bu proje icin CLAUDE.md yaz", ["cmdCompact"] = "Buraya kadarki baglami ozetle",
                ["cmdContext"] = "Baglami ne dolduruyor goster", ["cmdCost"] = "Bu oturumun maliyetini goster",
                ["cmdMemory"] = "Hafiza dosyalarini duzenle", ["cmdMcp"] = "MCP sunucularini goster",
                ["cmdTodos"] = "Gecerli gorev listesini goster", ["cmdStatus"] = "CLI durumunu goster",
                ["cmdDoctor"] = "Kurulumu denetle", ["cmdHelp"] = "CLI'nin kendi komutlarini listele",
                ["cmdProject"] = "Proje komutu", ["cmdUser"] = "Senin komutun",
                ["commands"] = "Komutlar", ["projectCommands"] = "Proje komutlari",
                ["addSelection"] = "Editordeki secimi ekle", ["noSelection"] = "Once editorde bir kod sec.",
                ["undoTurn"] = "Turu geri al",
                ["undoConfirm"] = "Son turda degisen izlenen dosyalar tur oncesi haline dondurulsun mu? Yeni dosyalar yerinde kalir.",
                ["undoDone"] = "Son turun dosya degisiklikleri geri alindi.", ["undoFail"] = "Geri alinamadi - git calisma agacini kontrol et.",
                ["planReady"] = "Claude'un bir plani var", ["applyPlan"] = "Plani uygula", ["keepPlanning"] = "Planlamaya devam",
                ["planApplied"] = "Plan uygulaniyor...", ["planKept"] = "Planlama suruyor...",
                ["pickAgent"] = "Alt ajan kullan", ["noAgents"] = "Alt ajan bulunamadi",
                ["review"] = "Denetle", ["bridgeOn"] = "Onaylar acik", ["bridgeOff"] = "Onaylar kapali",
                ["riskLow"] = "Dusuk risk", ["riskMedium"] = "Dosya degistirir", ["riskHigh"] = "Yuksek risk",
                ["bridgeOnWhat"] = "Claude bir dosyayi degistirmeden ya da komut calistirmadan once burada sorar; yaniti panelde verirsin.",
                ["bridgeOffWhat"] = "Onay isteyen bir sey yok; CLI kendi izin ayarlarina gore davranir.",
                ["newModel"] = "CLI'de daha yeni model var: {0}",
                ["micHint"] = "Sesle yaz", ["recording"] = "Kayitta - durdurmak icin mikrofona tekrar bas.",
                ["transcribing"] = "Yaziya cevriliyor...", ["micFailed"] = "Mikrofon bulunamadi.",
                ["sttNotSet"] = "Tools > Options > Claude Code (nLabtech) altinda konusma-yazi komutunu ayarla.",
                ["sttFailed"] = "Konusma-yazi komutu basarisiz oldu.", ["sttEmpty"] = "Hicbir sey anlasilmadi.",
                ["bypassMode"] = "Izinleri atla",
                ["imageZoom"] = "Tam boyut icin tikla", ["imageTooMany"] = "Mesaj basina en fazla {0} gorsel.",
                ["agentProject"] = "Bu proje", ["agentUser"] = "Senin", ["subagent"] = "Alt ajan",
                ["latestOf"] = "{0} (en guncel)", ["usageTitle"] = "Abonelik kullanimi",
                ["win5h"] = "Oturum (5 saat)", ["win7d"] = "Haftalik (7 gun)", ["win7dOpus"] = "Haftalik (Opus)",
                ["short5h"] = "5s", ["short7d"] = "7g", ["short7dOpus"] = "7g Opus", ["percent"] = "%{0}",
                ["queued"] = "Sirada - bu tur bitince gonderilecek.",
                ["cliMissing"] = "Claude CLI bulunamadi. Kur ya da yolunu Tools > Options > Claude Code (nLabtech) altinda ayarla.",
                ["cliStart"] = "Claude CLI baslatilamadi:", ["cliLost"] = "Oturum sona erdi.",
                ["turnFailed"] = "Tur basarisiz oldu.",
                ["deleteConfirm"] = "Bu sohbet ve mesajlari silinsin mi? Geri alinamaz.", ["cancel"] = "Vazgec",
                ["emptyTitle"] = "Visual Studio icinde Claude Code",
                ["emptyBody"] = "Bir soru sor, bir degisiklik anlat ya da isi devret. Claude asagida yazan klasorde calisir ve bir sey calistirmadan once sorar.",
                ["hintSlash"] = "komutlar - panelin kendi komutlari ve CLI'ninkiler",
                ["hintAt"] = "bu calisma alanindan bir dosya ya da klasor an",
                ["hintImage"] = "gorsel ekle, ya da dogrudan yapistir",
                ["hintMic"] = "yazmak yerine konus",
                ["resetsIn"] = "sifirlanmasina", ["resetSoon"] = "simdi sifirlaniyor",
                ["unitDay"] = "g", ["unitHour"] = "sa", ["unitMinute"] = "dk",
                ["limitNear"] = "Limite yaklasildi.", ["limitUnknown"] = "Henuz kullanim bildirilmedi.",
                ["stopped"] = "Durduruldu - sonraki mesajin yeni bir oturum baslatir.", ["connected"] = "Baglandi.",
                ["stalled"] = "hala calisiyor",
                ["workingVerbs"] = "Dusunuyor...|Okuyor...|Calisiyor...|Yaziyor...|Kontrol ediyor...",
                ["micChecking"] = "Konusma motoru araniyor...",
                ["micSaveFailed"] = "Kayit kaydedilemedi.",
                ["sttTimeout"] = "Yaziya cevirme cok uzun surdu, durduruldu.",
                ["sttNoEngine"] = "Konusma motoru bulunamadi. Birini kur (pip install faster-whisper) ya da Tools > Options > Claude Code (nLabtech) altinda komut ayarla.",
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
    private bool _modelCheckDone; // the model-staleness check runs once per panel
    private readonly VoiceRecorder _recorder = new VoiceRecorder();
    private RateLimitStatus? _lastUsage; // the newest usage the CLI reported, for the detail card
    private readonly TurnUsage _sessionUsage = new TurnUsage(); // what this panel has spent since it opened
    private int _sessionTurns;
    private int _turnThinkingTokens; // live reasoning estimate for the turn in flight
    private Grid? _scrim;            // the dimmed layer a confirmation is drawn on
    private string _thinkingText = string.Empty;
    private Border? _thinkingBox;
    private TextBlock? _thinkingBody;
    private TextBlock? _thinkingCaption;
    private ScrollViewer? _thinkingScroller;
    private DateTime _thinkingStart;
    private string? _speechCommand;  // the transcriber found on this machine, if any
    private bool _speechProbed;      // looked for one already - the answer will not change
    private StackPanel? _emptyState; // the welcome block, shown only while the feed is empty
    // Task call id -> the subagent it delegated to, so its later lines can be named.
    private readonly System.Collections.Generic.Dictionary<string, string> _subagentByToolId =
        new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
    private DateTime _lastEventAt;   // when the CLI last said anything, so a stall can be named
    private Border? _micButton;
    private List<string>? _pathCache;   // workspace paths for "@" completion, rebuilt on a timer
    private string _pathCacheDir = string.Empty;
    private DateTime _pathCacheAt;
    private string _workspaceKey = string.Empty;
    private string? _workingFolder;    // an explicit working folder chosen when no solution is open
    private TextBlock? _folderLabel;
    private string? _snapshotRef;      // git ref to restore tracked files to (stash-create SHA, or HEAD)
    private Border? _undoButton;
    private TextBlock? _undoLabel;
    private Border? _bridgeDot;        // approval-bridge indicator: bright when the endpoint is live
    private TextBlock? _bridgeLabel;
    private TextBlock? _usageLabel;    // subscription usage (5h / 7d windows), from rate_limit_event

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
        _modelCombo = MakeCombo(IconModel, new (string, string?)[]
        {
            ("Default model", null), ("Opus", "opus"), ("Sonnet", "sonnet"), ("Haiku", "haiku"), ("Fable", "fable"),
        });
        // The CLI's four permission modes, in order of how much they hand over. "Bypass" is last and
        // named plainly: it stops the panel asking at all, which is a decision, not a convenience.
        _modeCombo = MakeCombo(IconPermission, new (string, string?)[]
        {
            ("Ask each time", null), ("Accept edits", "acceptEdits"), ("Plan mode", "plan"), ("Bypass permissions", "bypassPermissions"),
        });
        _effortCombo = MakeCombo(IconEffort, new (string, string?)[]
        {
            ("Effort: default", null), ("Low", "low"), ("Medium", "medium"), ("High", "high"), ("xHigh", "xhigh"), ("Max", "max"),
        });
        // Localize the wording of the fixed entries (model, level and mode names stay as-is).
        // The tiers say "(latest)" because that is what an alias means: it follows the newest model of
        // that tier, so a pinned version never quietly goes stale here.
        Bind(() => SetComboItemLabel(_modelCombo, 0, Loc("defaultModel")));
        Bind(() => SetComboItemLabel(_modelCombo, 1, string.Format(Loc("latestOf"), "Opus")));
        Bind(() => SetComboItemLabel(_modelCombo, 2, string.Format(Loc("latestOf"), "Sonnet")));
        Bind(() => SetComboItemLabel(_modelCombo, 3, string.Format(Loc("latestOf"), "Haiku")));
        Bind(() => SetComboItemLabel(_modelCombo, 4, string.Format(Loc("latestOf"), "Fable")));
        Bind(() => SetComboItemLabel(_modeCombo, 0, Loc("askEach")));
        Bind(() => SetComboItemLabel(_modeCombo, 1, Loc("acceptEdits")));
        Bind(() => SetComboItemLabel(_modeCombo, 2, Loc("planMode")));
        Bind(() => SetComboItemLabel(_modeCombo, 3, Loc("bypassMode")));
        Bind(() => SetComboItemLabel(_effortCombo, 0, Loc("defaultEffort")));
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

        _langCombo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        _langCombo.Items.Add(MakeComboItem(IconLanguage, "English", "en"));
        _langCombo.Items.Add(MakeComboItem(IconLanguage, "Turkce", "tr"));
        _langCombo.SelectedIndex = 0;
        _langCombo.SelectionChanged += (_, __) => OnLanguageChanged();

        _accentCombo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        foreach (var a in Accents) _accentCombo.Items.Add(MakeComboItem(IconAccent, a.Name, a.Name));
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
            RenderThinking();
            RenderStreaming();
            ScrollToEndIfAtBottom();
        };

        // Build each section once - these add fields (input, status, combos) as children, so a second
        // call would try to re-parent the same element and throw.
        UIElement header = BuildHeader();
        // BuildComposer reads the current folder (a solution-service call) while wiring the status bar;
        // the tool window always builds on the UI thread, so this is safe.
#pragma warning disable VSTHRD010
        UIElement composer = BuildComposer();
#pragma warning restore VSTHRD010
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(_tasksBox, Dock.Top);
        DockPanel.SetDock(composer, Dock.Bottom);

        var root = new DockPanel { LastChildFill = true };
        root.Children.Add(header);
        root.Children.Add(_tasksBox);
        root.Children.Add(composer);
        root.Children.Add(_scroller);

        // The panel and, above it, the layer a confirmation is drawn on.
        var layers = new Grid();
        layers.Children.Add(root);
        layers.Children.Add(BuildScrim());
        layers.PreviewMouseLeftButtonDown += OnRootMouseDown;
        Content = layers;

        // Everything is built and every label registered, so it's safe to apply the stored choices -
        // selecting an item fires the change handlers, which re-localize and recolour.
        ApplyPreferences();

        // Shell styles resolve only once this control is in Visual Studio's own resource scope.
        Loaded += (_, __) => AdoptShellStyles();
    }

    // Dresses the standard WPF controls in Visual Studio's own styles.
    //
    // Without this a ComboBox and a ScrollBar keep the default Windows chrome, and the panel reads as
    // a foreign window pasted into the IDE rather than part of it - the single biggest reason a
    // hand-built tool window looks wrong next to Solution Explorer.
    private void AdoptShellStyles()
    {
        AdoptShellStyle(typeof(ComboBox), VsResourceKeys.ComboBoxStyleKey);
        AdoptShellStyle(typeof(System.Windows.Controls.Primitives.ScrollBar), VsResourceKeys.ScrollBarStyleKey);
    }

    private void AdoptShellStyle(Type target, object key)
    {
        try
        {
            // An implicit style keyed by type reaches every one of them, including the scrollbars
            // inside controls this panel never touches directly.
            if (TryFindResource(key) is Style style && style.TargetType == target) Resources[target] = style;
        }
        catch { /* an older shell without this key - the default look is not worth throwing over */ }
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

        // One row, not two: brand on the left, the chat switcher and preferences on the right. The
        // right side is added first so a narrow dock trims the product name instead of the controls.
        var row = new DockPanel { LastChildFill = false };
        UIElement controls = BuildChatControls();
        DockPanel.SetDock(controls, Dock.Right);
        row.Children.Add(controls);
        DockPanel.SetDock(brand, Dock.Left);
        row.Children.Add(brand);

        var bar = new Border
        {
            Padding = new Thickness(12, 6, 12, 6),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row,
        };
        bar.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        return bar;
    }

    // The conversation switcher plus New, Rename and Delete, then the two preferences. Rename swaps a
    // text box in over the switcher; Enter commits it, Escape or a click away cancels.
    private UIElement BuildChatControls()
    {
        var chat = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        MakePill(_convCombo);
        chat.Children.Add(_convCombo);

        _renameBox = new TextBox
        {
            MinWidth = 150,
            FontSize = 11,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        _renameBox.SetResourceReference(TextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _renameBox.SetResourceReference(TextBox.BackgroundProperty, VsBrushes.ComboBoxBackgroundKey);
        _renameBox.KeyDown += OnRenameKey;
        _renameBox.LostKeyboardFocus += (_, __) => EndRename();
        chat.Children.Add(_renameBox);

        // Glyphs, not words: three labelled buttons beside a dropdown was most of the panel's chrome,
        // and what each one does is already in its tooltip.
        chat.Children.Add(MakeIconButton(IconNew, "new", NewConversation));
        chat.Children.Add(MakeIconButton(IconRename, "rename", BeginRename));
        chat.Children.Add(MakeIconButton(IconDelete, "delete", DeleteConversation));

        // Language and accent: preferences rather than chat controls, so a gap sets them apart.
        chat.Children.Add(new Border { Width = 12 });
        chat.Children.Add(MakePill(_langCombo));
        chat.Children.Add(MakePill(_accentCombo));
        return chat;
    }

    // Shows the rename box seeded with the current title: the switcher and the box are siblings, so
    // renaming swaps one for the other in place rather than opening a dialog.
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
        leftTools.Children.Add(MakeIconButton(IconAttach, "attachHint", PickImages));
#pragma warning disable VSTHRD010
        leftTools.Children.Add(MakeIconButton(IconSelection, "addSelection", AddSelection));
        Border agentButton = null!;
        // Not "@": that belongs to file mentions, which the input completes as they are typed.
        agentButton = MakeIconButton(IconSubagent, "pickAgent", () => ShowSubagentMenu(agentButton));
        leftTools.Children.Add(agentButton);
#pragma warning restore VSTHRD010
        _micButton = MakeIconButton(IconMic, "micHint", () => _ = ToggleDictationAsync());
        leftTools.Children.Add(_micButton);

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

        // The composer is the one branded surface in the panel: an accent hairline around a faint card,
        // so the eye lands on where you type. A themed grey border made it just another grouping box.
        var inputBorder = new Border
        {
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            BorderBrush = Accent,
            Background = CardFill,
            Padding = new Thickness(8, 6, 8, 6),
            Child = inner,
            AllowDrop = true,
        };
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
        var folderButton = BuildStatusButton(IconFolder, PickFolder, out _folderLabel);
        // The panel is built on the UI thread; the folder caption reads the solution service safely here.
#pragma warning disable VSTHRD010
        Bind(() => _folderLabel!.Text = FolderCaption());
#pragma warning restore VSTHRD010

        // The undo control sits next to the folder; it stays hidden until a turn has a snapshot to revert.
        // UndoTurnAsync re-establishes the UI thread itself before any shell access.
#pragma warning disable VSTHRD010
        var undoButton = BuildStatusButton(IconUndo, () => _ = UndoTurnAsync(), out _undoLabel);
#pragma warning restore VSTHRD010
        undoButton.Visibility = Visibility.Collapsed;
        _undoButton = undoButton;
        Bind(() => { if (_undoLabel != null) _undoLabel.Text = Loc("undoTurn"); });

        // A one-click review of the working tree - sends a read-only "find issues" turn.
#pragma warning disable VSTHRD010
        Border reviewButton = BuildStatusButton(IconReview, () => _ = SendReviewAsync(), out TextBlock reviewLabel);
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
        var bridgeRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        bridgeRow.Children.Add(_bridgeDot);
        bridgeRow.Children.Add(_bridgeLabel);

        // Clickable, like the usage figure: the state is one word in the bar, and the explanation of
        // what that state actually permits is a click away. What is never shown, here or anywhere, is
        // the endpoint behind it - a port on this machine is nobody else's business.
        var bridgePanel = new Border
        {
            Child = bridgeRow,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 4, 1),
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        HoverTint(bridgePanel);
        bridgePanel.MouseLeftButtonUp += (_, __) => ShowBridgeCard(bridgePanel);

        // Subscription usage (5h / 7d windows). Sits left of the bridge indicator; hidden until the
        // first rate_limit_event arrives.
        _usageLabel = new TextBlock
        {
            FontSize = 11,
            Opacity = 0.6,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
        };
        _usageLabel.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _usageLabel.MouseLeftButtonUp += (_, __) => ShowUsageCard(_usageLabel);

        var statusBar = new DockPanel { Margin = new Thickness(4, 7, 4, 0), LastChildFill = true };
        DockPanel.SetDock(leftStatus, Dock.Left);
        DockPanel.SetDock(bridgePanel, Dock.Right);
        DockPanel.SetDock(_usageLabel, Dock.Right);
        statusBar.Children.Add(leftStatus);
        statusBar.Children.Add(bridgePanel);
        statusBar.Children.Add(_usageLabel);
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
            FontSize = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Bind(() => _primaryLabel.Text = Loc(_busy ? "stop" : "send"));
        _primary = new Border
        {
            Child = _primaryLabel,
            Background = Accent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 4, 13, 4),
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
                    if (_slashList?.SelectedItem is ListBoxItem it && it.Tag is CompletionItem cmd)
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
            _status.Text = Loc("queued");
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
            ResetThinking();
            _renderPending = false;
            _renderTimer.Start();
            SetBusy(true);
            await _session!.SendAsync(text, images);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            _status.Text = DescribeCliFailure(ex);
        }
    }

    // "Not installed" and "installed but refused to start" are the same sentence in most panels, and
    // they need opposite things from the developer - so they get different ones here.
    private string DescribeCliFailure(Exception ex)
    {
        for (Exception? e = ex; e != null; e = e.InnerException)
        {
            if (e is System.ComponentModel.Win32Exception win32 && win32.NativeErrorCode == 2)
                return Loc("cliMissing");
        }
        return Loc("cliStart") + " " + ex.Message;
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
            _status.Text = Loc("cliLost");
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
        _status.Text = Loc("connected");
        _ = CheckModelCatalogAsync(); // one-off, off the UI thread; never blocks the turn
    }

    // CLI events arrive on the pump thread; marshal every UI change to the dispatcher.
    private void OnCliEvent(object sender, CliEvent e)
    {
        // Ignore late events from a session that has been superseded (Stop, New, or a switch),
        // so a dying session cannot mutate the conversation that replaced it.
        if (!ReferenceEquals(sender, _session)) return;

        _lastEventAt = DateTime.UtcNow; // the turn is alive; the working strip reads this

        if (e.Todos != null)
        {
            var todos = e.Todos;
            OnUi(() => RenderTasks(todos));
        }

        switch (e.Kind)
        {
            case CliEventKind.SystemInit:
                if (e.ThinkingTokens.HasValue)
                {
                    // Reasoning is the one spend that happens before anything appears on screen.
                    _turnThinkingTokens = e.ThinkingTokens.Value;
                    break;
                }
                if (!string.IsNullOrEmpty(e.SessionId)) OnUi(() => { _current.CliSessionId = e.SessionId; SaveConversations(); });
                if (!string.IsNullOrEmpty(e.Model)) OnUi(() => _status.Text = "Model: " + e.Model);
                break;

            case CliEventKind.StreamDelta:
                // A subagent's tokens are not part of the answer being written here; they belong to
                // its own card, which is drawn when its turn completes. Folding them into this
                // stream would splice another agent's sentences into the middle of the reply.
                if (e.ParentToolUseId != null) break;

                if (!string.IsNullOrEmpty(e.Thinking))
                {
                    _thinkingText += e.Thinking;
                    _renderPending = true;
                }

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
                string? parent = e.ParentToolUseId;
                if (e.Tools != null)
                {
                    // A tool step: close the text so far into its own bubble, then drop a chip per call.
                    // Prefer the message's own text (authoritative) in case a delta lagged behind.
                    var tools = e.Tools;
                    string? preface = e.Text;
                    string? reasoning = e.Thinking;
                    OnUi(() =>
                    {
                        RememberSubagents(tools);
                        if (parent == null)
                        {
                            // The message's own blocks are authoritative; prefer them over what the
                            // deltas accumulated, which can lag or be cut short.
                            if (!string.IsNullOrEmpty(reasoning)) _thinkingText = reasoning!;
                            if (!string.IsNullOrEmpty(preface)) _streamingText = preface!;
                            FlushAssistantText();
                        }
                        else if (!string.IsNullOrEmpty(preface))
                        {
                            AddSubagentCard(SubagentName(parent), preface!);
                        }

                        string? owner = parent == null ? null : SubagentName(parent);
                        foreach (ToolCall call in tools) AddToolChip(call, owner);
                    });
                }
                else if (!string.IsNullOrEmpty(e.Text))
                {
                    if (parent == null)
                    {
                        _streamingText = e.Text!;
                        _renderPending = true;
                    }
                    else
                    {
                        string delegated = e.Text!;
                        string who = SubagentName(parent);
                        OnUi(() => AddSubagentCard(who, delegated));
                    }
                }
                break;

            case CliEventKind.RateLimit:
                if (e.RateLimit != null)
                {
                    RateLimitStatus usage = e.RateLimit;
                    OnUi(() => UpdateUsage(usage));
                }
                break;

            case CliEventKind.Result:
                OnUi(() =>
                {
                    // Turn done: finalize whatever text is still open, then stop the render loop.
                    _renderTimer.Stop();
                    FlushAssistantText();
                    SetBusy(false);

                    if (e.Usage != null)
                    {
                        AddTurnFooter(e.Usage, e.TotalCostUsd);
                        AccumulateSession(e.Usage);
                    }

                    if (e.TotalCostUsd.HasValue)
                    {
                        _sessionCost += e.TotalCostUsd.Value;
                        _status.Text = e.IsError
                            ? Loc("turnFailed")
                            : string.Format("${0:0.0000} \u00B7 {1} ${2:0.0000}", e.TotalCostUsd.Value, Loc("session"), _sessionCost);
                    }
                    ScrollToEndIfAtBottom();
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
                PendingImage captured = img;
                var thumb = new Image
                {
                    Source = img.Thumb,
                    Height = 54,
                    Margin = new Thickness(0, 0, 6, 0),
                    Stretch = Stretch.Uniform,
                    Cursor = Cursors.Hand,
                    ToolTip = Loc("imageZoom"),
                };
                thumb.MouseLeftButtonUp += (_, __) => ShowImageZoom(captured.Base64);
                strip.Children.Add(thumb);
            }
            stack.Children.Add(strip);
        }

        if (text.Length > 0)
        {
            // Read-only TextBox, not TextBlock: the developer's own prompt is the text most often
            // lifted back out, and a TextBlock cannot be selected here.
            var body = new TextBox
            {
                Text = text,
                IsReadOnly = true,
                IsReadOnlyCaretVisible = false,
                IsTabStop = false,
                TextWrapping = TextWrapping.Wrap,
                BorderThickness = new Thickness(0),
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                FocusVisualStyle = null,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            body.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            BubbleWheelToParent(body);
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

    // Claude's reasoning, while it is being produced: dim, and capped in height so a long chain of
    // thought scrolls inside its own box instead of pushing the conversation off the screen. It is
    // shown at all because a turn that thinks for a minute with nothing on screen looks hung.
    private void RenderThinking()
    {
        if (_thinkingText.Length == 0) return;

        if (_thinkingBody == null)
        {
            _thinkingStart = DateTime.UtcNow;

            _thinkingCaption = new TextBlock
            {
                Text = Loc("thinking"),
                FontSize = 10.5,
                Opacity = 0.55,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                Margin = new Thickness(0, 0, 0, 4),
            };
            _thinkingCaption.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            _thinkingBody = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.5 };
            _thinkingBody.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

            var scroller = new ScrollViewer
            {
                Content = _thinkingBody,
                MaxHeight = 120,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };

            var stack = new StackPanel();
            stack.Children.Add(_thinkingCaption);
            stack.Children.Add(scroller);

            // Once it has finished, the reasoning collapses to its caption - it is reference material,
            // not the answer, and it should not sit between the question and the reply.
            _thinkingCaption.MouseLeftButtonUp += (_, __) =>
                scroller.Visibility = scroller.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

            _thinkingBox = new Border
            {
                Child = stack,
                Background = CardFill,
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(14, 4, 8, 4),
            };
            _thinkingScroller = scroller;

            HideEmptyState();
            _messages.Children.Add(_thinkingBox);
        }

        _thinkingBody.Text = ExtensionOptions.ForDisplay(_thinkingText);
        _thinkingScroller?.ScrollToEnd();
    }

    // Closes the reasoning block: it stops being live, says how long it took, and folds away.
    private void FinishThinking()
    {
        // Paint whatever arrived since the last tick, so the final words are not lost to the timer.
        if (_thinkingText.Length > 0) RenderThinking();
        if (_thinkingBox == null) return;

        if (_thinkingCaption != null)
        {
            int seconds = Math.Max(1, (int)(DateTime.UtcNow - _thinkingStart).TotalSeconds);
            _thinkingCaption.Text = string.Format(Loc("thoughtFor"), seconds);
        }
        if (_thinkingScroller != null) _thinkingScroller.Visibility = Visibility.Collapsed;

        ResetThinking();
    }

    // Drops the reasoning state without touching what is already drawn - used when a turn is
    // abandoned (Stop, New, a chat switch) rather than finished.
    private void ResetThinking()
    {
        _thinkingBox = null;
        _thinkingBody = null;
        _thinkingCaption = null;
        _thinkingScroller = null;
        _thinkingText = string.Empty;
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
        FinishThinking(); // the reasoning belongs to the step that just ended, not the next one
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
        ResetThinking();
        _streamingText = string.Empty;
    }

    // One tool call as an outlined chip in the feed: a marker, the tool name, and a muted summary.
    // Outlined rather than a bare line because a turn produces a run of these, and without an edge
    // they read as one paragraph of noise instead of a list of discrete steps.
    private void AddToolChip(ToolCall call, string? owner)
    {
        var line = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 11 };
        line.Inlines.Add(new Run(owner == null ? "\u25CF  " : "\u2514  ") { Foreground = Accent });
        if (owner != null)
        {
            line.Inlines.Add(new Run(owner + ": ") { Foreground = Accent, FontWeight = FontWeights.SemiBold });
        }
        line.Inlines.Add(new Run(call.Name) { FontWeight = FontWeights.SemiBold });
        if (!string.IsNullOrEmpty(call.Summary))
        {
            line.Inlines.Add(new Run("  -  " + ExtensionOptions.ForDisplay(call.Summary)) { FontFamily = MonoFont });
        }
        line.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var chip = new Border
        {
            Child = line,
            Background = CardFill,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(9, 4, 9, 4),
            Margin = new Thickness(14, 2, 8, 2),
        };
        chip.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        HideEmptyState();
        _messages.Children.Add(chip);
        ScrollToEndIfAtBottom();
    }

    // What a delegated turn produced, kept in its own card so it never reads as the main answer. The
    // caption names the subagent: "some agent said this" and "Claude said this" are different claims.
    private void AddSubagentCard(string name, string text)
    {
        var caption = new TextBlock
        {
            Text = Loc("subagent") + " - " + name,
            FontSize = 10,
            Opacity = 0.55,
            Margin = new Thickness(0, 0, 0, 5),
        };
        caption.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var body = new StackPanel();
        RenderMarkdownInto(body, text);

        var stack = new StackPanel();
        stack.Children.Add(caption);
        stack.Children.Add(body);

        var card = new Border
        {
            Child = stack,
            Background = CardFill,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(24, 4, 8, 4), // indented: it sits under the call that asked for it
        };
        card.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        HideEmptyState();
        _messages.Children.Add(card);
        ScrollToEndIfAtBottom();
    }

    // Remembers which subagent a Task call handed work to, so the lines the CLI later reports under
    // that call can be attributed to it by name.
    private void RememberSubagents(System.Collections.Generic.IReadOnlyList<ToolCall> tools)
    {
        foreach (ToolCall call in tools)
        {
            if (!string.IsNullOrEmpty(call.Id) && !string.IsNullOrEmpty(call.Subagent))
            {
                _subagentByToolId[call.Id] = call.Subagent!;
            }
        }
    }

    private string SubagentName(string toolUseId) =>
        _subagentByToolId.TryGetValue(toolUseId, out string name) ? name : Loc("subagent");

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
                // A neutral card, not an accent wash: several prompts in a row all tinted with the
                // brand colour turned the transcript into a stack of banners. The stripe is enough.
                Background = CardFill,
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

        HideEmptyState();
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
            Opacity = 0.7,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 10, 0),
            Background = Brushes.Transparent, // without a brush only the glyphs are hit-testable
        };
        link.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        link.MouseEnter += (_, __) => link.Opacity = 1;
        link.MouseLeave += (_, __) => link.Opacity = 0.7;
        Bind(() => link.Text = Loc(key));
        if (onClick != null) link.MouseLeftButtonUp += (_, __) => onClick();
        return link;
    }

    // Cheap per-delta update: just set the streaming TextBlock's text, no tree rebuild.
    private void RenderStreaming()
    {
        if (_streamingText.Length == 0) return;
        EnsureStreamingBubble();
        _streamingTextBlock!.Text = ExtensionOptions.ForDisplay(_streamingText);
    }

    // Turns parsed markdown blocks into WPF elements: code as a selectable monospace box, headings
    // bold and larger, bullets with a leading dot, paragraphs with inline bold and code.
    private void RenderMarkdownInto(StackPanel container, string text)
    {
        container.Children.Clear();
        // Redact at the display boundary: what Claude echoed may contain a key, and a screenshot of
        // it would leak permanently. What was sent is untouched.
        text = ExtensionOptions.ForDisplay(text);
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
                    container.Children.Add(BuildBullet(block));
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

        // A RichTextBox (not a TextBox) so the code can be syntax coloured and still selected and
        // copied. A wide PageWidth stops the document wrapping, which is what gives it a horizontal
        // scrollbar instead of folding long lines.
        var code = new RichTextBox
        {
            IsReadOnly = true,
            IsReadOnlyCaretVisible = true,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10),
            FontFamily = MonoFont,
            FontSize = 12.5,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Document = BuildCodeDocument(block.Text, block.Language),
        };
        code.SetResourceReference(RichTextBox.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        code.SetResourceReference(RichTextBox.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        code.SetResourceReference(RichTextBox.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
        BubbleWheelToParent(code);

        var codeBorder = new Border
        {
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Child = code,
        };
        panel.Children.Add(codeBorder);
        return panel;
    }

    // Turns a code block into a coloured flow document. The scanner is language aware; anything it
    // does not classify stays in the theme's own foreground.
    private static FlowDocument BuildCodeDocument(string text, string? language)
    {
        bool dark = IsDarkTheme();
        var paragraph = new Paragraph { Margin = new Thickness(0), TextAlignment = TextAlignment.Left };
        foreach (CodeSpan span in CodeHighlighter.Highlight(text, language))
        {
            var run = new Run(span.Text);
            Brush? colour = SyntaxBrush(span.Kind, dark);
            if (colour != null) run.Foreground = colour;
            paragraph.Inlines.Add(run);
        }

        return new FlowDocument(paragraph)
        {
            PageWidth = 2400, // wide enough that lines do not wrap; the box scrolls instead
            FontFamily = MonoFont,
            FontSize = 12.5,
        };
    }

    // The syntax palette, chosen per theme so it stays readable on both. Plain text returns null and
    // keeps the tool window's own foreground.
    private static Brush? SyntaxBrush(CodeSpanKind kind, bool dark)
    {
        switch (kind)
        {
            case CodeSpanKind.Keyword: return dark ? Frozen(Color.FromRgb(0x56, 0x9C, 0xD6)) : Frozen(Color.FromRgb(0x00, 0x00, 0xC0));
            case CodeSpanKind.String: return dark ? Frozen(Color.FromRgb(0xCE, 0x91, 0x78)) : Frozen(Color.FromRgb(0xA3, 0x15, 0x15));
            case CodeSpanKind.Comment: return dark ? Frozen(Color.FromRgb(0x6A, 0x99, 0x55)) : Frozen(Color.FromRgb(0x00, 0x80, 0x00));
            case CodeSpanKind.Number: return dark ? Frozen(Color.FromRgb(0xB5, 0xCE, 0xA8)) : Frozen(Color.FromRgb(0x09, 0x86, 0x58));
            default: return null;
        }
    }

    // Reads the shell's own tool-window background and decides whether the theme is dark, so the
    // syntax palette matches. Falls back to dark, which is the common Visual Studio default.
    private static bool IsDarkTheme()
    {
        try
        {
            object? resource = Application.Current?.TryFindResource(VsBrushes.ToolWindowBackgroundKey);
            if (resource is SolidColorBrush brush)
            {
                Color c = brush.Color;
                double luminance = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
                return luminance < 0.5;
            }
        }
        catch { /* no resource yet - assume dark */ }
        return true;
    }

    // A bullet is a two-column row rather than a horizontal stack: the text column has to be given a
    // finite width, or the selectable body below would lay out unwrapped.
    private UIElement BuildBullet(MarkdownBlock block)
    {
        var row = new Grid { Margin = new Thickness(block.Indent * 16, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // A number needs a wider, right-aligned gutter than a dot, so "9." and "10." still line up.
        bool numbered = block.Marker.Length > 0 && block.Marker[0] >= '0' && block.Marker[0] <= '9';
        var marker = new TextBlock
        {
            Text = (block.Marker.Length == 0 ? "\u2022" : block.Marker) + "  ",
            Foreground = Accent,
            FontWeight = FontWeights.SemiBold,
            MinWidth = numbered ? 26 : 0,
            TextAlignment = numbered ? TextAlignment.Right : TextAlignment.Left,
        };
        row.Children.Add(marker);

        var body = (FrameworkElement)BuildInlineText(block.Text, bold: false, fontSize: 0, topGap: 0);
        Grid.SetColumn(body, 1);
        row.Children.Add(body);
        return row;
    }

    // A paragraph/heading/bullet line whose **bold** and `code` spans are real runs.
    //
    // It is a read-only RichTextBox rather than a TextBlock for one reason: on .NET Framework's WPF a
    // TextBlock cannot be selected, so a reply could be read but never dragged out with the mouse -
    // which is what makes a chat panel feel broken. A RichTextBox keeps the inline formatting, adds
    // selection and the native Ctrl+C, and with both scrollbars disabled it still measures to its own
    // content, so it lays out exactly like the TextBlock it replaces.
    private UIElement BuildInlineText(string text, bool bold, double fontSize, double topGap)
    {
        var paragraph = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = 18,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        };

        foreach (MarkdownInline run in MarkdownDocument.ParseInline(text))
        {
            switch (run.Kind)
            {
                case MarkdownInlineKind.Bold:
                    paragraph.Inlines.Add(new Run(run.Text) { FontWeight = FontWeights.Bold });
                    break;
                case MarkdownInlineKind.Code:
                    // Accent, not just monospace: a file name or a flag mentioned mid-sentence is the
                    // part being pointed at, and in a wall of prose the font alone does not carry it.
                    paragraph.Inlines.Add(new Run(run.Text) { FontFamily = MonoFont, Foreground = Accent });
                    break;
                default:
                    paragraph.Inlines.Add(new Run(run.Text));
                    break;
            }
        }

        var box = new RichTextBox
        {
            Document = new FlowDocument(paragraph) { PagePadding = new Thickness(0) },
            IsReadOnly = true,
            IsReadOnlyCaretVisible = false,
            IsTabStop = false,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Padding = new Thickness(0),
            Margin = new Thickness(0, topGap, 0, 0),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            FocusVisualStyle = null,
        };
        if (fontSize > 0) box.FontSize = fontSize;
        if (bold) box.FontWeight = FontWeights.Bold;
        box.SetResourceReference(Control.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        BubbleWheelToParent(box);
        return box;
    }

    // Every RichTextBox carries its own ScrollViewer, which would swallow the wheel and freeze the
    // feed whenever the pointer sat over a reply. Hand the wheel back to the container instead.
    private static void BubbleWheelToParent(FrameworkElement box)
    {
        box.PreviewMouseWheel += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            if (box.Parent is UIElement parent)
            {
                parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
                {
                    RoutedEvent = UIElement.MouseWheelEvent,
                    Source = box,
                });
            }
        };
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
            _lastEventAt = DateTime.UtcNow;
            _turnTokens = 0;
            _turnThinkingTokens = 0;
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

    // Refreshes the working strip: "Reading files...  12s  -  2.9k tokens". The verb rotates so a long
    // turn still looks alive, and a turn that has gone quiet says so rather than counting in silence -
    // waiting is fine, but not knowing whether anything is still happening is not.
    private void UpdateWorkingStrip()
    {
        int secs = (int)(DateTime.UtcNow - _turnStart).TotalSeconds;
        string tokens = _turnTokens >= 1000
            ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}k", _turnTokens / 1000.0)
            : _turnTokens.ToString(System.Globalization.CultureInfo.InvariantCulture);

        string line = string.Format("{0}  {1}s  \u00B7  {2} {3}", WorkingVerb(secs), secs, tokens, Loc("tokens"));

        // Reasoning is billed and invisible; while it is running it is the only thing being spent.
        if (_turnThinkingTokens > 0)
        {
            line += "  \u00B7  " + Compact(_turnThinkingTokens) + " " + Loc("thinkingTokens");
        }

        int quiet = (int)(DateTime.UtcNow - _lastEventAt).TotalSeconds;
        if (_lastEventAt != default(DateTime) && quiet >= 25) line += "  \u00B7  " + Loc("stalled");

        _workingLabel.Text = line;
    }

    // Running totals for the panel's lifetime, shown in the usage card. The per-turn footer answers
    // "what did that cost"; this answers "what has this session cost so far", which is the question
    // that used to have no answer anywhere once the status line had moved on.
    private void AccumulateSession(TurnUsage usage)
    {
        _sessionUsage.InputTokens += usage.InputTokens;
        _sessionUsage.OutputTokens += usage.OutputTokens;
        _sessionUsage.CacheReadTokens += usage.CacheReadTokens;
        _sessionUsage.CacheWriteTokens += usage.CacheWriteTokens;
        _sessionUsage.ThinkingTokens += usage.ThinkingTokens;
        _sessionUsage.DurationMs += usage.DurationMs;
        _sessionTurns++;
    }

    // A token count at a glance: exact while it is small, thousands once it is not.
    private static string Compact(int count) => count >= 1000
        ? string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}k", count / 1000.0)
        : count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    // What a finished turn cost, written into the transcript rather than into the status bar.
    //
    // The working strip is gone the moment the turn ends and the status line is overwritten by the
    // next thing that happens, so until now the only record of what a turn spent disappeared within
    // seconds of it being produced. Here it stays next to the answer it paid for.
    private void AddTurnFooter(TurnUsage usage, double? cost)
    {
        var parts = new System.Collections.Generic.List<string>();

        if (usage.DurationMs > 0)
        {
            parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0:0.0}s", usage.DurationMs / 1000.0));
        }
        if (usage.OutputTokens > 0) parts.Add(Loc("outTokens") + " " + Compact(usage.OutputTokens));
        if (usage.ThinkingTokens > 0) parts.Add(Loc("thinkingTokens") + " " + Compact(usage.ThinkingTokens));
        if (usage.InputTokens > 0) parts.Add(Loc("inTokens") + " " + Compact(usage.InputTokens));
        // Cache reads are most of the traffic on a long chat and a fraction of the price; shown, but
        // never added into the same figure as what was actually billed at full rate.
        if (usage.CacheReadTokens > 0) parts.Add(Loc("cacheTokens") + " " + Compact(usage.CacheReadTokens));
        if (cost.HasValue) parts.Add(string.Format(System.Globalization.CultureInfo.InvariantCulture, "${0:0.0000}", cost.Value));

        if (parts.Count == 0) return;

        var line = new TextBlock
        {
            Text = string.Join("  ·  ", parts),
            FontSize = 10,
            Opacity = 0.45,
            Margin = new Thickness(14, 0, 8, 6),
            TextWrapping = TextWrapping.Wrap,
        };
        line.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        _messages.Children.Add(line);
    }

    // One of the language's working verbs, changing every few seconds. Falls back to the plain
    // "working" line if the list is missing, so a translation gap cannot blank the strip.
    private string WorkingVerb(int seconds)
    {
        string[] verbs = Loc("workingVerbs").Split('|');
        if (verbs.Length == 0 || verbs[0].Length == 0) return Loc("working");
        return verbs[(seconds / 6) % verbs.Length];
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
        ResetThinking();
        SetBusy(false);
        _status.Text = Loc("stopped");
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
                Text = done ? "\u2713  " : active ? "\u203A  " : "\u25CB  ",
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
    private void DeleteConversation() => _ = DeleteConversationAsync();

    // A chat is the only record of what was asked and answered, and there is no undo for this.
    private async System.Threading.Tasks.Task DeleteConversationAsync()
    {
        if (!await ConfirmAsync("delete", "deleteConfirm", "delete", destructive: true)) return;
        DeleteConversationConfirmed();
    }

    private void DeleteConversationConfirmed()
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
            RebuildMessages(); // nothing to restore, so this is what paints the welcome block
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
        ResetThinking();

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
        _messages.Children.Add(EnsureEmptyState());

        foreach (var (isUser, text) in _current.Messages)
        {
            if (isUser) AddUserBubble(text);
            else AddStoredAssistant(text);
        }

        if (_current.Messages.Count == 0) ShowEmptyState();
        ScrollToEnd();
    }

    // The first thing a developer sees in an empty chat. It is built once and reused - rebuilding it
    // per chat would register a new set of localizers each time - and only shown while the feed is
    // empty, so it never sits above a conversation.
    private StackPanel EnsureEmptyState()
    {
        if (_emptyState != null) return _emptyState;

        var stack = new StackPanel { Margin = new Thickness(14, 20, 14, 8), Visibility = Visibility.Collapsed };

        var title = new TextBlock { FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) };
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => title.Text = Loc("emptyTitle"));
        stack.Children.Add(title);

        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.7, Margin = new Thickness(0, 0, 0, 12) };
        body.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => body.Text = Loc("emptyBody"));
        stack.Children.Add(body);

        // The first two are literally what you type, so they stay as characters; the last two name a
        // button, so they wear that button's icon.
        stack.Children.Add(BuildHint("/", "hintSlash", MonoFont));
        stack.Children.Add(BuildHint("@", "hintAt", MonoFont));
        stack.Children.Add(BuildHint(IconAttach, "hintImage", IconFont));
        stack.Children.Add(BuildHint(IconMic, "hintMic", IconFont));

        // Offered here as well as in the status bar: choosing where Claude works is the one thing a
        // developer may need to do before their first message.
        Border folder = MakeAccentButton("pickFolder", PickFolder);
        folder.HorizontalAlignment = HorizontalAlignment.Left;
        folder.Margin = new Thickness(0, 14, 0, 0);
        stack.Children.Add(folder);

        _emptyState = stack;
        return stack;
    }

    // One "type this, get that" line: the character in the accent colour, then what it does.
    private UIElement BuildHint(string glyph, string key, FontFamily face)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

        var mark = new TextBlock
        {
            Text = glyph,
            FontFamily = face,
            Foreground = Accent,
            MinWidth = 24,
        };
        var text = new TextBlock { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        Bind(() => text.Text = Loc(key));

        row.Children.Add(mark);
        row.Children.Add(text);
        return row;
    }

    private void ShowEmptyState() => EnsureEmptyState().Visibility = Visibility.Visible;

    private void HideEmptyState()
    {
        if (_emptyState != null) _emptyState.Visibility = Visibility.Collapsed;
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

        // Grade the call so the card says what is at stake - an approval that looks the same for
        // "list open files" and "rm -rf" teaches people to click Allow without reading.
        RiskAssessment? risk = isPlan ? null : RiskAssessor.Assess(req.ToolName, req.InputPreview);
        UIElement header = title;
        if (risk != null)
        {
            var headerRow = new WrapPanel();
            title.VerticalAlignment = VerticalAlignment.Center;
            headerRow.Children.Add(title);
            headerRow.Children.Add(BuildRiskChip(risk));
            header = headerRow;
        }

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
                Text = ExtensionOptions.ForDisplay(req.InputPreview),
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
        inner.Children.Add(header);
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
            var applyBtn = MakeAccentButtonText(Loc("applyPlan"), () => Decide(true, false, "planApplied"));
            applyBtn.Margin = new Thickness(0, 0, 8, 0);
            var keepBtn = MakeGhostButtonText(Loc("keepPlanning"), () => Decide(false, false, "planKept"));
            buttons.Children.Add(applyBtn);
            buttons.Children.Add(keepBtn);
        }
        else
        {
            var allowBtn = MakeAccentButtonText(Loc("allow"), () => Decide(true, false, "allowed"));
            allowBtn.Margin = new Thickness(0, 0, 8, 0);
            var denyBtn = MakeGhostButtonText(Loc("deny"), () => Decide(false, false, "denied"));
            denyBtn.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(allowBtn);
            buttons.Children.Add(denyBtn);

            // "Always allow" is withheld for high-risk calls: a blanket yes to a shell tool is
            // exactly the decision that should stay per-call.
            if (risk!.Level != RiskLevel.High)
            {
                buttons.Children.Add(MakeGhostButtonText(Loc("always"), () => Decide(true, true, "allowed")));
            }
        }

        _messages.Children.Add(card);
        ScrollToEnd();
    }

    // The risk badge on an approval card: filled for medium/high, quiet outline for low. The reason
    // rides along as the tooltip rather than adding a second line to the card.
    private Border BuildRiskChip(RiskAssessment risk)
    {
        var label = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        var chip = new Border
        {
            Child = label,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 1, 7, 1),
            Margin = new Thickness(8, 1, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = risk.Reason,
        };

        switch (risk.Level)
        {
            case RiskLevel.High:
                chip.Background = HighRisk;
                label.Foreground = OnAccent;
                break;
            case RiskLevel.Medium:
                chip.Background = Accent;
                label.Foreground = OnAccent;
                break;
            default:
                chip.Background = Brushes.Transparent;
                chip.BorderThickness = new Thickness(1);
                chip.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);
                label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
                label.Opacity = 0.7;
                break;
        }

        string key = risk.Level == RiskLevel.High ? "riskHigh" : risk.Level == RiskLevel.Medium ? "riskMedium" : "riskLow";
        Bind(() => label.Text = Loc(key));
        return chip;
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

    private ComboBox MakeCombo(string glyph, (string label, string? value)[] options)
    {
        var combo = new ComboBox { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (label, value) in options)
        {
            combo.Items.Add(MakeComboItem(glyph, label, value));
        }
        combo.SelectedIndex = 0;
        MakePill(combo);
        return combo;
    }

    // One row of a selector: the glyph in the accent colour, then the label. The glyph stays visible
    // in the closed pill too - in a narrow dock the label is the first thing to be trimmed away, and
    // the icon is then all that says which selector this is.
    private ComboBoxItem MakeComboItem(string glyph, string label, string? value)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 11,
            Foreground = Accent,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var text = new TextBlock
        {
            Text = label,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(icon);
        row.Children.Add(text);
        return new ComboBoxItem { Content = row, Tag = value };
    }

    // Re-labels one selector row when the language changes, leaving its glyph and value alone.
    private static void SetComboItemLabel(ComboBox combo, int index, string label)
    {
        if (index < 0 || index >= combo.Items.Count) return;
        if (combo.Items[index] is ComboBoxItem item && item.Content is StackPanel row &&
            row.Children.Count > 1 && row.Children[1] is TextBlock text)
        {
            text.Text = label;
        }
    }

    // Going straight from one open selector to another took two clicks: while its list is up a
    // ComboBox captures the mouse, so the first click only dismissed it and never reached what it was
    // aimed at. Close the open one and open the one actually clicked, in a single click.
    private void OnRootMouseDown(object sender, MouseButtonEventArgs e)
    {
        ComboBox? open = OpenSelector();
        if (open == null) return;

        // A click on the open list itself is that list being used, not a click past it.
        if (Descends(e.OriginalSource as DependencyObject, open)) return;

        ComboBox? target = SelectorUnder(e.GetPosition(this));
        if (target == null || ReferenceEquals(target, open)) return;

        open.IsDropDownOpen = false;
        e.Handled = true;

        // Opening the next list has to wait for the first one to finish closing and release the
        // mouse. This is a WPF dispatcher post from the UI thread to itself, not a thread switch, so
        // the shell's threading rule does not apply.
#pragma warning disable VSTHRD001, VSTHRD110
        Dispatcher.BeginInvoke(
            new Action(() => target.IsDropDownOpen = true),
            System.Windows.Threading.DispatcherPriority.Input);
#pragma warning restore VSTHRD001, VSTHRD110
    }

    private ComboBox? OpenSelector()
    {
        foreach (ComboBox combo in Selectors())
        {
            if (combo.IsDropDownOpen) return combo;
        }
        return null;
    }

    private ComboBox? SelectorUnder(Point point)
    {
        foreach (ComboBox combo in Selectors())
        {
            if (!combo.IsVisible) continue;
            Point local = combo.TranslatePoint(new Point(0, 0), this);
            var bounds = new Rect(local, combo.RenderSize);
            if (bounds.Contains(point)) return combo;
        }
        return null;
    }

    private System.Collections.Generic.IEnumerable<ComboBox> Selectors()
    {
        yield return _modelCombo;
        yield return _modeCombo;
        yield return _effortCombo;
        yield return _convCombo;
        yield return _langCombo;
        yield return _accentCombo;
    }

    // Walks both trees: a dropdown's items live in the combo's logical tree but in the popup's own
    // visual tree, so neither walk alone answers "did this come from that control".
    private static bool Descends(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            DependencyObject? next = node is Visual || node is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(node)
                : null;
            node = next ?? LogicalTreeHelper.GetParent(node);
        }
        return false;
    }

    // A selector sized as a pill rather than a form field: small, and free to shrink. The width is
    // deliberately not fixed - when the panel is docked narrow the pills give way instead of wrapping
    // onto a second line and pushing Send away from the selectors it belongs beside.
    private static ComboBox MakePill(ComboBox combo)
    {
        combo.FontSize = 11;
        combo.MinWidth = 0;
        combo.MaxWidth = 150;
        combo.Margin = new Thickness(0, 0, 5, 0);
        combo.VerticalAlignment = VerticalAlignment.Center;
        return combo;
    }

    // A filled accent button (Send). Built as a Border so it is fully rounded and branded, with a
    // hover tint and a hand cursor - a plain WPF Button cannot be themed this cleanly in code.
    private Border MakeAccentButton(string key, Action onClick)
    {
        var label = AccentLabel();
        Bind(() => label.Text = Loc(key));
        return AccentShell(label, onClick);
    }

    // The same button with its text fixed. Used by anything built per event - an approval, a
    // confirmation - because those must not register a localizer that outlives the card it was on.
    private Border MakeAccentButtonText(string text, Action onClick)
    {
        var label = AccentLabel();
        label.Text = text;
        return AccentShell(label, onClick);
    }

    private static TextBlock AccentLabel() => new TextBlock
    {
        Foreground = OnAccent,
        FontWeight = FontWeights.SemiBold,
        FontSize = 11,
    };

    private Border AccentShell(TextBlock label, Action onClick)
    {
        var button = new Border
        {
            Child = label,
            Background = Accent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(13, 4, 13, 4),
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
        };
        label.HorizontalAlignment = HorizontalAlignment.Center;
        button.MouseEnter += (_, __) => button.Background = AccentHover;
        button.MouseLeave += (_, __) => button.Background = Accent;
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // A quiet borderless button (New, Rename, Delete): a toolbar action, not a form control. Outlining
    // these turned every one of them into a box and made the panel read as a dialog; the only thing
    // they need is to light up under the pointer.
    private Border MakeGhostButton(string key, Action onClick)
    {
        TextBlock label = GhostLabel();
        Bind(() => label.Text = Loc(key));
        return GhostShell(label, onClick);
    }

    private Border MakeGhostButtonText(string text, Action onClick)
    {
        TextBlock label = GhostLabel();
        label.Text = text;
        return GhostShell(label, onClick);
    }

    private static TextBlock GhostLabel()
    {
        var label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        return label;
    }

    private Border GhostShell(TextBlock label, Action onClick)
    {
        var button = new Border
        {
            Child = label,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 3, 7, 3),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        HoverTint(button);
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // A single-glyph flat button (the attach +), with a localized tooltip.
    private Border MakeIconButton(string glyph, string tipKey, Action onClick)
    {
        var label = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 13, // icon fonts are drawn at their own weight; bolding them muddies the strokes
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var button = new Border
        {
            Child = label,
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 2, 0),
        };
        Bind(() => button.ToolTip = Loc(tipKey));
        HoverTint(button);
        button.MouseLeftButtonUp += (_, __) => onClick();
        return button;
    }

    // Hover as a faint wash of the accent instead of a change in opacity: dimming a control to show it
    // is under the pointer reads as "disabled", which is the opposite of what a hover means.
    private void HoverTint(Border button)
    {
        // A button that is currently "on" (the microphone while recording) keeps its fill: hovering
        // must not be able to clear a state the developer needs to see.
        button.MouseEnter += (_, __) => { if (!IsLit(button)) button.Background = UserFill; };
        button.MouseLeave += (_, __) => { if (!IsLit(button)) button.Background = Brushes.Transparent; };
    }

    private static bool IsLit(Border button) => (button.Tag as string) == "on";

    // A quiet status-bar button: muted text that brightens on hover. The label is returned so the
    // caller can keep its text current (the folder name, a status, a count).
    // A status-bar action: a glyph in the accent colour and a quiet label. The glyph is what makes a
    // row of these scannable - four words in the same weight and size are not.
    private Border BuildStatusButton(string glyph, Action onClick, out TextBlock label)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = IconFont,
            FontSize = 11,
            Foreground = Accent,
            Margin = new Thickness(0, 0, 5, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.65 };
        label.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(label);

        var button = new Border
        {
            Child = row,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 1, 6, 1),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        HoverTint(button);
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
        if (string.IsNullOrEmpty(dir)) return Loc("pickFolder");
        return (Path.GetFileName(dir.TrimEnd('\\', '/')) ?? dir);
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
            _pathCache = null;   // and so does the "@" completion index
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

        if (!await ConfirmAsync("undoTurn", "undoConfirm", "undoTurn", destructive: true)) return;

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

    // Shows subscription usage in the status bar ("5h 2%  -  7d 40%"); turns accent when near a limit.
    private void UpdateUsage(RateLimitStatus usage)
    {
        if (_usageLabel == null) return;
        _usageLabel.Text = FormatUsage(usage);
        if (usage.Warning)
        {
            _usageLabel.Foreground = Accent;
            _usageLabel.Opacity = 1.0;
        }
        else
        {
            _usageLabel.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            _usageLabel.Opacity = 0.6;
        }
        _lastUsage = usage;
        _usageLabel.ToolTip = null; // the detail is a card now, opened by clicking the figure
    }

    // The full picture behind the one-line summary: a bar per window, how full it is and how long
    // until it resets. A percentage alone does not answer "can I keep going this evening", and a bar
    // answers it without being read.
    private void ShowUsageCard(UIElement anchor)
    {
        var stack = new StackPanel { MinWidth = 260 };
        var title = new TextBlock
        {
            Text = Loc("usageTitle"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        stack.Children.Add(title);

        if (_lastUsage == null)
        {
            var none = new TextBlock { Text = Loc("limitUnknown"), FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 0, 8) };
            none.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            stack.Children.Add(none);
        }
        else
        {
            foreach (RateLimitWindow w in _lastUsage.Windows) stack.Children.Add(BuildUsageBar(w));

            if (_lastUsage.Warning)
            {
                var warn = new TextBlock { Text = Loc("limitNear"), FontSize = 10.5, Foreground = Accent, Margin = new Thickness(0, 4, 0, 0) };
                stack.Children.Add(warn);
            }
        }

        // What this panel has spent since it opened - the subscription windows above are the whole
        // account's, which does not tell the developer what this conversation is responsible for.
        if (_sessionTurns > 0)
        {
            var rule = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8), Opacity = 0.25 };
            rule.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBorderKey);
            stack.Children.Add(rule);

            var heading = new TextBlock
            {
                Text = string.Format(Loc("sessionTotals"), _sessionTurns),
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5),
            };
            heading.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            stack.Children.Add(heading);

            stack.Children.Add(UsageRow(Loc("outTokens"), Compact(_sessionUsage.OutputTokens)));
            if (_sessionUsage.ThinkingTokens > 0) stack.Children.Add(UsageRow(Loc("thinkingTokens"), Compact(_sessionUsage.ThinkingTokens)));
            stack.Children.Add(UsageRow(Loc("inTokens"), Compact(_sessionUsage.InputTokens)));
            stack.Children.Add(UsageRow(Loc("cacheTokens"), Compact(_sessionUsage.CacheReadTokens)));
            stack.Children.Add(UsageRow(
                Loc("session"),
                string.Format(System.Globalization.CultureInfo.InvariantCulture, "${0:0.0000}", _sessionCost)));
        }

        ShowCardPopup(anchor, stack);
    }

    // The panel's own confirmation, in place of Windows' MessageBox.
    //
    // A system dialog arrives in the operating system's theme, not the IDE's: against a dark Visual
    // Studio it is a white box with a warning triangle, which reads as "something broke" rather than
    // as the panel asking a question. This one is drawn on the panel, so it is themed like the rest
    // of it - and a destructive confirmation is coloured as one.
    private System.Threading.Tasks.Task<bool> ConfirmAsync(string titleKey, string bodyKey, string confirmKey, bool destructive)
    {
        var answer = new System.Threading.Tasks.TaskCompletionSource<bool>();
        if (_scrim == null) return System.Threading.Tasks.Task.FromResult(false);

        var title = new TextBlock
        {
            Text = Loc(titleKey),
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var body = new TextBlock
        {
            Text = Loc(bodyKey),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 16),
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        void Close(bool confirmed)
        {
            _scrim.Visibility = Visibility.Collapsed;
            _scrim.Children.Clear();
            answer.TrySetResult(confirmed);
        }

        Border no = MakeGhostButtonText(Loc("cancel"), () => Close(false));
        Border yes = MakeAccentButtonText(Loc(confirmKey), () => Close(true));
        if (destructive)
        {
            // The one place the panel uses a colour other than its accent: an action that cannot be
            // undone should not wear the same button as sending a message. These handlers run after
            // the shell's own, so they have the last word on the brush.
            yes.Background = HighRisk;
            yes.MouseEnter += (_, __) => yes.Background = HighRisk;
            yes.MouseLeave += (_, __) => yes.Background = HighRisk;
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(no);
        buttons.Children.Add(yes);

        var stack = new StackPanel();
        stack.Children.Add(title);
        stack.Children.Add(body);
        stack.Children.Add(buttons);

        var card = new Border
        {
            Child = stack,
            MaxWidth = 340,
            BorderThickness = new Thickness(1),
            BorderBrush = destructive ? HighRisk : Accent,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 14, 16, 14),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        card.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        // A click on the card is not a click on the scrim behind it, which would answer "no".
        card.MouseLeftButtonUp += (_, e) => e.Handled = true;

        _scrim.Children.Add(card);
        _scrim.Visibility = Visibility.Visible;
        _scrim.MouseLeftButtonUp += ScrimClick;

        void ScrimClick(object sender, MouseButtonEventArgs e)
        {
            _scrim.MouseLeftButtonUp -= ScrimClick;
            Close(false); // clicking away is the safe answer, never the destructive one
        }

        return answer.Task;
    }

    // The dimmed layer a confirmation sits on. It covers the whole panel so nothing behind it can be
    // clicked while the question is open.
    private Grid BuildScrim()
    {
        var scrim = new Grid
        {
            Background = Frozen(Color.FromArgb(0x99, 0x00, 0x00, 0x00)),
            Visibility = Visibility.Collapsed,
        };
        _scrim = scrim;
        return scrim;
    }

    // What the approval state means, in a sentence. Deliberately says nothing about how the panel and
    // the CLI reach each other: that is an implementation detail, and printing an address into a
    // screenshot is how one leaks.
    private void ShowBridgeCard(UIElement anchor)
    {
        bool on = _approval != null;

        var stack = new StackPanel { MaxWidth = 300 };
        var title = new TextBlock
        {
            Text = Loc(on ? "bridgeOn" : "bridgeOff"),
            FontWeight = FontWeights.SemiBold,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 6),
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        stack.Children.Add(title);

        var body = new TextBlock
        {
            Text = Loc(on ? "bridgeOnWhat" : "bridgeOffWhat"),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 11,
            Opacity = 0.75,
        };
        body.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        stack.Children.Add(body);

        ShowCardPopup(anchor, stack);
    }

    // The shell every one of these little detail cards sits in.
    private void ShowCardPopup(UIElement anchor, UIElement content)
    {
        var card = new Border
        {
            Child = content,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
        };
        card.SetResourceReference(Border.BackgroundProperty, VsBrushes.ToolWindowBackgroundKey);
        card.SetResourceReference(Border.BorderBrushProperty, VsBrushes.ToolWindowBorderKey);

        new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            Child = card,
        }.IsOpen = true;
    }

    // A label on the left, its figure right-aligned, so a column of them lines up.
    private UIElement UsageRow(string label, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };

        var name = new TextBlock { Text = label, FontSize = 11, Opacity = 0.7 };
        name.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);

        var figure = new TextBlock { Text = value, FontSize = 11, FontFamily = MonoFont };
        figure.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        DockPanel.SetDock(figure, Dock.Right);

        row.Children.Add(figure);
        row.Children.Add(name);
        return row;
    }

    // One window: its name, its percentage, a filled bar, and when it resets.
    private UIElement BuildUsageBar(RateLimitWindow window)
    {
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };
        var name = new TextBlock { Text = WindowName(window.Name), FontSize = 11 };
        name.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        var percent = new TextBlock
        {
            Text = string.Format(Loc("percent"), (int)Math.Round(window.Utilization * 100)),
            FontSize = 11,
            Opacity = 0.7,
        };
        percent.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        DockPanel.SetDock(percent, Dock.Right);
        head.Children.Add(percent);
        head.Children.Add(name);

        // The fill is a column in a two-column grid, so it tracks the card's width without any
        // arithmetic on actual pixels - which would be wrong the moment the panel is resized.
        var track = new Grid { Height = 5 };
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, window.Utilization), GridUnitType.Star) });
        track.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(0.0001, 1 - window.Utilization), GridUnitType.Star) });

        var fill = new Border { Background = Accent, CornerRadius = new CornerRadius(3) };
        var rest = new Border { Background = CardFill, CornerRadius = new CornerRadius(3) };
        Grid.SetColumn(rest, 1);
        track.Children.Add(fill);
        track.Children.Add(rest);

        var block = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        block.Children.Add(head);
        block.Children.Add(track);

        if (window.ResetsAt != null)
        {
            var reset = new TextBlock
            {
                Text = Loc("resetsIn") + " " + Remaining(window.ResetsAt.Value),
                FontSize = 10,
                Opacity = 0.5,
                Margin = new Thickness(0, 3, 0, 0),
            };
            reset.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            block.Children.Add(reset);
        }

        return block;
    }

    // "3h 12m" - coarse on purpose, because the exact second is never the question being asked.
    private string Remaining(DateTimeOffset resetsAt)
    {
        TimeSpan left = resetsAt - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero) return Loc("resetSoon");
        if (left.TotalDays >= 1) return (int)left.TotalDays + Loc("unitDay") + " " + left.Hours + Loc("unitHour");
        if (left.TotalHours >= 1) return (int)left.TotalHours + Loc("unitHour") + " " + left.Minutes + Loc("unitMinute");
        return Math.Max(1, (int)left.TotalMinutes) + Loc("unitMinute");
    }

    // A compact usage line: the windows the CLI reported, each as a short name and a percentage.
    private string FormatUsage(RateLimitStatus usage)
    {
        var parts = new System.Collections.Generic.List<string>();
        foreach (RateLimitWindow w in usage.Windows)
        {
            parts.Add(ShortWindow(w.Name) + " " + string.Format(Loc("percent"), (int)Math.Round(w.Utilization * 100)));
            if (parts.Count >= 3) break;
        }
        return string.Join("  \u00B7  ", parts);
    }

    // The CLI's window keys, in the reader's language. An unknown key is shown as it came: better a
    // raw name than silently dropping a limit the developer is actually being measured against.
    private string ShortWindow(string name)
    {
        switch (name)
        {
            case "five_hour": return Loc("short5h");
            case "seven_day": return Loc("short7d");
            case "seven_day_opus": return Loc("short7dOpus");
            default: return name;
        }
    }

    private string WindowName(string name)
    {
        switch (name)
        {
            case "five_hour": return Loc("win5h");
            case "seven_day": return Loc("win7d");
            case "seven_day_opus": return Loc("win7dOpus");
            default: return name;
        }
    }

    // Sends a read-only review of the working tree as a turn, reusing the normal send path (so it
    // queues behind a running turn rather than interleaving).
    private async System.Threading.Tasks.Task SendReviewAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        _input.Text = Loc("reviewPrompt");
        await SendAsync();
    }

    private static System.Threading.Tasks.Task<(bool ok, string output)> RunGitAsync(string dir, string args)
        => RunProcessAsync("git", args, dir);

    // Asks the CLI once per panel which model aliases it accepts, and says so if the picker has
    // fallen behind - a hard-coded model list otherwise goes quietly stale as new tiers ship.
    private async System.Threading.Tasks.Task CheckModelCatalogAsync()
    {
        if (_modelCheckDone) return;
        _modelCheckDone = true;

        var offered = new List<string>();
        foreach (object item in _modelCombo.Items)
        {
            if (item is ComboBoxItem ci && ci.Tag is string alias && alias.Length > 0) offered.Add(alias);
        }

        var (fileName, arguments) = ClaudeCliSession.ComposeStart(ClaudeCliSession.ResolveExecutable(), "--help");
        var (ok, help) = await RunProcessAsync(fileName, arguments, string.Empty);
        if (!ok) return;

        ModelCatalogDiff diff = ModelCatalogCheck.Compare(help, offered);
        if (!diff.IsStale) return;

        string names = string.Join(", ", diff.NewInCli);
        _status.Text = string.Format(Loc("newModel"), names);
        _modelCombo.ToolTip = string.Format(Loc("newModel"), names);
    }

    // Runs a short-lived process off the UI thread and returns (exit-zero, trimmed stdout). Never
    // throws: a missing executable or a timeout comes back as (false, ""), so a caller can simply
    // treat the feature that needed it as unavailable.
    private static async System.Threading.Tasks.Task<(bool ok, string output)> RunProcessAsync(
        string fileName, string args, string dir)
    {
        var (ok, output, _) = await RunProcessDetailedAsync(fileName, args, dir, 15000);
        return (ok, output);
    }

    // The same runner, but reporting a timeout separately and letting the caller set the limit. A
    // transcription is minutes-long work the first time a speech model loads, and "it timed out" is
    // a different thing to tell the developer than "it failed".
    private static async System.Threading.Tasks.Task<(bool ok, string output, bool timedOut)> RunProcessDetailedAsync(
        string fileName, string args, string dir, int timeoutMs)
    {
        return await System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo(fileName, args)
                {
                    WorkingDirectory = dir,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    // Read as UTF-8 rather than the console's code page, so a transcript in a
                    // language with accents arrives intact instead of as mojibake.
                    StandardOutputEncoding = new System.Text.UTF8Encoding(false),
                    StandardErrorEncoding = new System.Text.UTF8Encoding(false),
                };
                using (System.Diagnostics.Process? p = System.Diagnostics.Process.Start(psi))
                {
                    if (p == null) return (false, string.Empty, false);
                    string output = p.StandardOutput.ReadToEnd();
                    p.StandardError.ReadToEnd(); // drain so the pipe never blocks the process
                    if (!p.WaitForExit(timeoutMs)) { try { p.Kill(); } catch { } return (false, string.Empty, true); }
                    return (p.ExitCode == 0, output.Trim(), false);
                }
            }
            catch { return (false, string.Empty, false); }
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
            if (_slashList.SelectedItem is ListBoxItem it && it.Tag is CompletionItem c) RunSlash(c);
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

    // The token being typed just before the caret, when it is one the panel completes: "/" for a
    // command (only at the very start of the input) or "@" for a file mention (anywhere).
    private (char Kind, int Start, string Fragment)? TokenBeforeCaret()
    {
        string text = _input.Text ?? string.Empty;
        int caret = Math.Min(_input.CaretIndex, text.Length);

        int i = caret - 1;
        while (i >= 0 && !char.IsWhiteSpace(text[i])) i--;
        int start = i + 1;
        if (start >= caret) return null;

        char kind = text[start];
        if (kind != '/' && kind != '@') return null;
        if (kind == '/' && start != 0) return null; // a command is the whole line, not a word in it

        return (kind, start, text.Substring(start + 1, caret - start - 1));
    }

    // Opens/updates the completion popup as the developer types, for whichever token is under the
    // caret. Closing it is the default: no token, or nothing matching, means get out of the way.
    private void UpdateSlashPopup()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var token = TokenBeforeCaret();
        if (token == null) { HideSlash(); return; }

        List<CompletionItem> matches = token.Value.Kind == '/'
            ? MatchCommands(token.Value.Fragment)
            : MatchFiles(token.Value.Fragment);

        if (matches.Count == 0) { HideSlash(); return; }

        PopulateCompletion(matches);
        if (_slashPopup != null) _slashPopup.IsOpen = true;
    }

    // Commands matching what has been typed, with the ones that start with it first - typing "co"
    // should offer /compact and /context before /model's description happens to contain it.
    private List<CompletionItem> MatchCommands(string fragment)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var starts = new List<CompletionItem>();
        var contains = new List<CompletionItem>();

        foreach (SlashCommand c in SlashCommands())
        {
            if (c.Name.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;
            var item = new CompletionItem
            {
                Label = "/" + c.Name,
                Description = c.Description,
                Insert = "/" + c.Name + " ",
                Panel = c.Panel,
            };

            if (fragment.Length == 0 || c.Name.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)) starts.Add(item);
            else contains.Add(item);
        }

        starts.AddRange(contains);
        return starts;
    }

    // File and folder completions for an "@" mention, ranked so a name that starts with what was
    // typed comes before one that merely contains it.
    private List<CompletionItem> MatchFiles(string fragment)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var starts = new List<CompletionItem>();
        var contains = new List<CompletionItem>();

        foreach (string relative in WorkspacePaths())
        {
            if (starts.Count + contains.Count >= 60) break;
            if (fragment.Length > 0 && relative.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) < 0) continue;

            string name = relative.TrimEnd('/');
            int slash = name.LastIndexOf('/');
            string leaf = slash >= 0 ? name.Substring(slash + 1) : name;

            var item = new CompletionItem
            {
                Label = leaf + (relative.EndsWith("/", StringComparison.Ordinal) ? "/" : string.Empty),
                Description = relative,
                Insert = "@" + relative + (relative.EndsWith("/", StringComparison.Ordinal) ? string.Empty : " "),
            };

            if (fragment.Length == 0 || leaf.StartsWith(fragment, StringComparison.OrdinalIgnoreCase)) starts.Add(item);
            else contains.Add(item);
        }

        starts.AddRange(contains);
        return starts;
    }

    // Fills the list with the current matches, the first one preselected so Enter picks it at once.
    private void PopulateCompletion(List<CompletionItem> matches)
    {
        if (_slashList == null) return;
        _slashList.Items.Clear();
        foreach (CompletionItem c in matches)
        {
            // Name and description on one line, the names in a monospace column so they align into a
            // list that can be scanned down. Two-line rows turned twenty commands into a wall.
            var row = new Grid { Margin = new Thickness(6, 2, 6, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 96 });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var name = new TextBlock
            {
                Text = c.Label,
                FontFamily = MonoFont,
                FontWeight = FontWeights.SemiBold,
                FontSize = 11.5,
                Foreground = Accent,
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(name);

            var desc = new TextBlock
            {
                Text = c.Description,
                FontSize = 10.5,
                Opacity = 0.6,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            desc.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
            Grid.SetColumn(desc, 1);
            row.Children.Add(desc);

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

    // Panel commands run locally and clear the input; everything else replaces just the token being
    // typed, so an "@file" completion lands mid-sentence without disturbing the rest of the line.
    private void RunSlash(CompletionItem c)
    {
        var token = TokenBeforeCaret();
        HideSlash();

        if (c.Panel != null)
        {
            _input.Clear();
            c.Panel();
            _input.Focus();
            return;
        }

        if (token != null)
        {
            string text = _input.Text ?? string.Empty;
            int caret = Math.Min(_input.CaretIndex, text.Length);
            int start = token.Value.Start;
            _input.Text = text.Substring(0, start) + c.Insert + text.Substring(caret);
            _input.CaretIndex = start + c.Insert.Length;
        }
        _input.Focus();
    }

    // The workspace's files and folders as forward-slashed relative paths, built once and reused -
    // walking the tree on every keystroke would stall the input on any real repository. Build output
    // and package folders are skipped: nobody @-mentions bin/obj.
    private List<string> WorkspacePaths()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string dir = WorkingDirectory();
        if (string.IsNullOrEmpty(dir)) return new List<string>();

        if (_pathCache != null && string.Equals(_pathCacheDir, dir, StringComparison.OrdinalIgnoreCase) &&
            DateTime.UtcNow - _pathCacheAt < TimeSpan.FromSeconds(30))
        {
            return _pathCache;
        }

        var paths = new List<string>();
        CollectPaths(dir, dir, paths, 0);
        _pathCache = paths;
        _pathCacheDir = dir;
        _pathCacheAt = DateTime.UtcNow;
        return paths;
    }

    private const int MaxIndexedPaths = 4000;

    private static void CollectPaths(string root, string current, List<string> sink, int depth)
    {
        if (depth > 12 || sink.Count >= MaxIndexedPaths) return;
        try
        {
            foreach (string directory in Directory.EnumerateDirectories(current))
            {
                if (sink.Count >= MaxIndexedPaths) return;
                if (IsIgnoredFolder(Path.GetFileName(directory))) continue;
                sink.Add(RelativePath(root, directory) + "/");
                CollectPaths(root, directory, sink, depth + 1);
            }
            foreach (string file in Directory.EnumerateFiles(current))
            {
                if (sink.Count >= MaxIndexedPaths) return;
                sink.Add(RelativePath(root, file));
            }
        }
        catch { /* unreadable folder - skip it */ }
    }

    private static bool IsIgnoredFolder(string? name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        switch (name!.ToLowerInvariant())
        {
            case "bin": case "obj": case ".git": case ".vs": case "node_modules":
            case "packages": case "dist": case ".angular": case ".next": case "__pycache__":
                return true;
            default:
                return name[0] == '.' && name.Length > 1; // other dot-folders are noise too
        }
    }

    private static string RelativePath(string root, string full)
    {
        string relative = full.Length > root.Length ? full.Substring(root.Length) : full;
        return relative.TrimStart('\\', '/').Replace('\\', '/');
    }

    // The menu contents, in the order they are useful: the panel's own actions (which run here,
    // because the panel owns the state they change), then the CLI's own commands (which are handed
    // to it with the message), then whatever the developer has written for this project or for
    // themselves. A command with no Panel action is simply typed into the input for the CLI.
    private List<SlashCommand> SlashCommands()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var list = new List<SlashCommand>
        {
            new SlashCommand { Name = "new", Description = Loc("cmdNew"), Panel = NewConversation },
            new SlashCommand { Name = "clear", Description = Loc("cmdClear"), Panel = ClearCurrentChat },
            new SlashCommand { Name = "model", Description = Loc("cmdModel"), Panel = () => OpenCombo(_modelCombo) },
            new SlashCommand { Name = "effort", Description = Loc("cmdEffort"), Panel = () => OpenCombo(_effortCombo) },
            new SlashCommand { Name = "permission", Description = Loc("cmdPermission"), Panel = () => OpenCombo(_modeCombo) },
            new SlashCommand { Name = "language", Description = Loc("cmdLanguage"), Panel = () => OpenCombo(_langCombo) },
            new SlashCommand { Name = "agents", Description = Loc("cmdAgents"), Panel = () => ShowSubagentMenu(_input) },
            new SlashCommand { Name = "attach", Description = Loc("cmdAttach"), Panel = PickImages },
            new SlashCommand { Name = "folder", Description = Loc("cmdFolder"), Panel = PickFolder },
            new SlashCommand { Name = "review", Description = Loc("cmdReview"), Panel = () => _ = SendReviewAsync() },
            new SlashCommand { Name = "undo", Description = Loc("cmdUndo"), Panel = () => _ = UndoTurnAsync() },

            // Handed to the CLI. These are its commands, not the panel's, so the panel does not
            // pretend to implement them - it types the line and the CLI expands it.
            new SlashCommand { Name = "init", Description = Loc("cmdInit") },
            new SlashCommand { Name = "compact", Description = Loc("cmdCompact") },
            new SlashCommand { Name = "context", Description = Loc("cmdContext") },
            new SlashCommand { Name = "cost", Description = Loc("cmdCost") },
            new SlashCommand { Name = "memory", Description = Loc("cmdMemory") },
            new SlashCommand { Name = "mcp", Description = Loc("cmdMcp") },
            new SlashCommand { Name = "todos", Description = Loc("cmdTodos") },
            new SlashCommand { Name = "status", Description = Loc("cmdStatus") },
            new SlashCommand { Name = "doctor", Description = Loc("cmdDoctor") },
            new SlashCommand { Name = "help", Description = Loc("cmdHelp") },
        };

        string dir = WorkingDirectory();
        if (!string.IsNullOrEmpty(dir))
            list.AddRange(DiscoverCommands(Path.Combine(dir, ".claude", "commands"), Loc("cmdProject")));
        list.AddRange(DiscoverCommands(UserClaudeDir("commands"), Loc("cmdUser")));
        return list;
    }

    private static void OpenCombo(ComboBox combo)
    {
        combo.Focus();
        combo.IsDropDownOpen = true;
    }

    /// <summary>The developer's own ~/.claude folder, where their personal commands and agents live.</summary>
    private static string UserClaudeDir(string leaf)
    {
        try
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrEmpty(home) ? string.Empty : Path.Combine(home, ".claude", leaf);
        }
        catch { return string.Empty; }
    }

    // Reads custom slash commands from a commands folder (nested folders become "dir:name", matching
    // the CLI's own naming). Best-effort: an unreadable tree just yields no extra commands.
    private static List<SlashCommand> DiscoverCommands(string commandsDir, string origin)
    {
        var result = new List<SlashCommand>();
        try
        {
            if (string.IsNullOrEmpty(commandsDir) || !Directory.Exists(commandsDir)) return result;

            foreach (string file in Directory.EnumerateFiles(commandsDir, "*.md", SearchOption.AllDirectories))
            {
                string rel = file.Substring(commandsDir.Length).TrimStart('\\', '/');
                string name = rel.Substring(0, rel.Length - ".md".Length).Replace('\\', ':').Replace('/', ':');
                if (name.Length == 0) continue;

                string description = FirstMeaningfulLine(file);
                result.Add(new SlashCommand
                {
                    Name = name,
                    Description = description.Length == 0 ? origin : origin + " - " + description,
                });
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
        ResetThinking();

        _current.Messages.Clear();
        _current.CliSessionId = null;
        SetConversationTitle(_current, "New chat");
        ClearTasks();
        RebuildMessages();
        SetBusy(false);
        _status.Text = Loc("newChat");
        SaveConversations();
    }

    // --- Dictation -----------------------------------------------------------------------------

    // Click to record, click again to transcribe. The extension records; the developer's own
    // speech-to-text command turns it into text, so nothing is bundled and no audio leaves the
    // machine unless their chosen engine sends it.
    private async System.Threading.Tasks.Task ToggleDictationAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (_recorder.IsRecording)
        {
            SetMicActive(false);
            string? wav = _recorder.StopAndSave();
            if (wav == null) { _status.Text = Loc("micSaveFailed"); return; }
            await TranscribeAsync(wav);
            return;
        }

        _status.Text = Loc("micChecking");
        string? engine = await ResolveSpeechCommandAsync();
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        if (engine == null) { _status.Text = Loc("sttNoEngine"); return; }

        if (!_recorder.Start()) { _status.Text = Loc("micFailed"); return; }
        SetMicActive(true);
        _status.Text = Loc("recording");
    }

    // Finds something that can transcribe. A command set in Options always wins; otherwise look for
    // what the machine already has - a Python with faster-whisper - and write the small script that
    // drives it. Probed once per panel: the answer does not change while Visual Studio is running.
    private async System.Threading.Tasks.Task<string?> ResolveSpeechCommandAsync()
    {
        if (SpeechCommand.IsConfigured(ExtensionOptions.SpeechToTextCommand))
            return ExtensionOptions.SpeechToTextCommand;

        if (_speechProbed) return _speechCommand;
        _speechProbed = true;

        foreach (string python in new[] { "python", "py", "python3" })
        {
            var (ok, _) = await RunProcessAsync(python, "-c \"import faster_whisper\"", string.Empty);
            if (!ok) continue;

            string? script = WriteTranscriberScript();
            if (script == null) break;
            _speechCommand = SpeechCommand.ComposeDefault(python, script);
            break;
        }

        return _speechCommand;
    }

    // Writes the fallback transcriber next to the extension's own data, once. Never overwrites: if
    // the developer has edited it, that edit is theirs to keep.
    private static string? WriteTranscriberScript()
    {
        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "nLabtech", "ClaudeCodeVs");
            Directory.CreateDirectory(dir);

            string path = Path.Combine(dir, "transcribe.py");
            if (!File.Exists(path))
                File.WriteAllText(path, SpeechCommand.LocalScript, new System.Text.UTF8Encoding(false));
            return path;
        }
        catch { return null; }
    }

    // Runs the configured engine over the recording and drops the text into the input. The WAV is
    // deleted either way - a stray recording of the developer's voice must not linger in temp.
    private async System.Threading.Tasks.Task TranscribeAsync(string wavPath)
    {
        _status.Text = Loc("transcribing");
        try
        {
            string? template = await ResolveSpeechCommandAsync();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (template == null) { _status.Text = Loc("sttNoEngine"); return; }

            var (fileName, arguments) = SpeechCommand.Compose(template, wavPath);

            // Five minutes, not fifteen seconds: the first run of a local speech model loads it from
            // disk, and cutting that off would look like a broken feature rather than a slow one.
            var (ok, output, timedOut) = await RunProcessDetailedAsync(
                fileName, arguments, System.IO.Path.GetTempPath(), 300000);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (timedOut) { _status.Text = Loc("sttTimeout"); return; }

            string text = ok ? SpeechCommand.CleanTranscript(output) : string.Empty;

            if (text.Length == 0)
            {
                _status.Text = Loc(ok ? "sttEmpty" : "sttFailed");
                return;
            }

            string existing = _input.Text ?? string.Empty;
            _input.Text = existing.Length == 0 ? text : existing.TrimEnd() + " " + text;
            _input.CaretIndex = _input.Text.Length;
            _input.Focus();
            _status.Text = Loc("hello");
        }
        finally
        {
            try { if (System.IO.File.Exists(wavPath)) System.IO.File.Delete(wavPath); } catch { }
        }
    }

    // The mic button carries the recording state: filled accent while live, quiet otherwise.
    private void SetMicActive(bool active)
    {
        if (_micButton == null) return;
        _micButton.Tag = active ? "on" : null; // so a hover cannot wipe the recording state
        _micButton.Background = active ? Accent : Brushes.Transparent;
        if (_micButton.Child is TextBlock glyph)
        {
            if (active) glyph.Foreground = OnAccent;
            else glyph.SetResourceReference(TextBlock.ForegroundProperty, VsBrushes.ToolWindowTextKey);
        }
    }

    // --- Subagents -----------------------------------------------------------------------------

    // Opens a menu of the subagents defined for this project and the developer, anchored to the button.
    // Picking one drops a directive into the input so the CLI delegates that part of the turn to it.
    private void ShowSubagentMenu(UIElement anchor)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = PlacementMode.Top, MaxHeight = 520 };
        List<(string Name, string Description, bool FromProject)> agents = DiscoverSubagents();
        if (agents.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = Loc("noAgents"), IsEnabled = false });
            menu.IsOpen = true;
            return;
        }

        // Grouped by where they came from, because "the project defines this one" is the difference
        // that decides whether a teammate has it too. The header stays a plain string so the menu's
        // own type-ahead keeps working - with dozens of agents, typing the name is how it is used.
        bool wroteProjectHeader = false;
        bool wroteUserHeader = false;
        foreach ((string Name, string Description, bool FromProject) a in agents)
        {
            if (a.FromProject && !wroteProjectHeader)
            {
                menu.Items.Add(new MenuItem { Header = Loc("agentProject"), IsEnabled = false });
                wroteProjectHeader = true;
            }
            else if (!a.FromProject && !wroteUserHeader)
            {
                if (wroteProjectHeader) menu.Items.Add(new Separator());
                menu.Items.Add(new MenuItem { Header = Loc("agentUser"), IsEnabled = false });
                wroteUserHeader = true;
            }

            string name = a.Name;
            var item = new MenuItem { Header = name };
            if (!string.IsNullOrEmpty(a.Description)) item.ToolTip = a.Description;
            item.Click += (_, __) => InsertSubagent(name);
            menu.Items.Add(item);
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
    private List<(string Name, string Description, bool FromProject)> DiscoverSubagents()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        var found = new List<(string, string, bool)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach ((string Root, bool FromProject) source in SubagentRoots())
        {
            try
            {
                if (!Directory.Exists(source.Root)) continue;
                foreach (string file in Directory.EnumerateFiles(source.Root, "*.md", SearchOption.AllDirectories))
                {
                    string name = FrontmatterValue(file, "name") ?? Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrEmpty(name) || !seen.Add(name)) continue;
                    found.Add((name, FrontmatterValue(file, "description") ?? string.Empty, source.FromProject));
                }
            }
            catch { /* unreadable folder - skip it */ }
        }

        // Project agents first, then each group by name: the grouping is what the menu draws.
        found.Sort((x, y) =>
        {
            if (x.Item3 != y.Item3) return x.Item3 ? -1 : 1;
            return string.Compare(x.Item1, y.Item1, StringComparison.OrdinalIgnoreCase);
        });
        return found;
    }

    private IEnumerable<(string Root, bool FromProject)> SubagentRoots()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        string dir = WorkingDirectory();
        if (!string.IsNullOrEmpty(dir)) yield return (Path.Combine(dir, ".claude", "agents"), true);
        string home = UserClaudeDir("agents");
        if (!string.IsNullOrEmpty(home)) yield return (home, false);
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

    // Six is not a technical limit: it is the point past which a turn is carrying more pictures than
    // instruction, and the CLI is being paid per image to guess what they were for.
    private const int MaxImages = 6;

    private void AddPending(PendingImage img)
    {
        if (_pending.Count >= MaxImages)
        {
            _status.Text = string.Format(Loc("imageTooMany"), MaxImages);
            return;
        }
        _pending.Add(img);
        RefreshAttachStrip();
    }

    // Opens one attachment full size. A 46px thumbnail is enough to tell two screenshots apart and
    // not enough to check what a screenshot actually shows.
    private void ShowImageZoom(string base64)
    {
        BitmapSource? full = DecodeBase64(base64);
        if (full == null) return;

        // Qualified: EnvDTE has a Window too, and this file sees both.
        var window = new System.Windows.Window
        {
            Content = new Border { Background = Brushes.Black, Child = new Image { Source = full, Stretch = Stretch.Uniform } },
            SizeToContent = SizeToContent.WidthAndHeight,
            MaxWidth = SystemParameters.WorkArea.Width * 0.9,
            MaxHeight = SystemParameters.WorkArea.Height * 0.9,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            Title = Loc("imageZoom"),
        };
        window.MouseLeftButtonUp += (_, __) => window.Close();
        window.KeyDown += (_, e) => { if (e.Key == Key.Escape) window.Close(); };

        System.Windows.Window? owner = System.Windows.Window.GetWindow(this);
        if (owner != null) window.Owner = owner;
        window.ShowDialog();
    }

    private static BitmapSource? DecodeBase64(string base64)
    {
        try
        {
            byte[] bytes = Convert.FromBase64String(base64);
            using (var stream = new System.IO.MemoryStream(bytes))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
        }
        catch { return null; }
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
            var thumb = new Image { Source = img.Thumb, Height = 46, Stretch = Stretch.Uniform, Cursor = Cursors.Hand };
            thumb.ToolTip = Loc("imageZoom");
            thumb.MouseLeftButtonUp += (_, __) => ShowImageZoom(captured.Base64);
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
        _recorder.Dispose(); // a live recording must not outlive the panel
        _session?.Dispose();
        _session = null;
        _approval?.Dispose();
        _approval = null;
    }
}
