using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Threading;
using Nlabs.ClaudeCodeVsPackage.Bridge;
using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using System;
using System.Windows.Threading;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// Pushes <c>selection_changed</c> to Claude Code as the developer moves around the editor.
///
/// The bridge already answers questions about the selection when asked. A native IDE also does the
/// other half: it tells the CLI where the developer is looking, unasked, so the model has the
/// context before the question is put. Without this half the connection answers but never speaks.
///
/// Two event sources, because neither alone is enough. Which view has focus comes from Visual
/// Studio's text manager over a COM connection point (<see cref="IVsTextViewEvents"/>); what the
/// selection inside that view actually is comes from the editor's own WPF view, which reports the
/// span rather than only the caret's line. The COM half re-binds the WPF half on every focus
/// change, so exactly one view is ever subscribed.
///
/// What is pushed is deliberately narrow. Moving the caret fires on every keystroke, so pushes are
/// coalesced and an unchanged selection is dropped rather than repeated. And a selection is a
/// file's contents: the same workspace and secret rules the IDE tools obey apply here, which
/// matters more, not less, because nothing asked for this one.
/// </summary>
internal sealed class SelectionWatcher : IVsTextViewEvents, IDisposable
{
    // Long enough that a held arrow key produces one push rather than thirty; short enough that
    // the model's picture is current by the time the developer has finished typing the question.
    private static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(200);

    // A selection can be the whole file. The bridge drops anything past 1 MB, and the point here is
    // to say where the developer is, not to ship the file - openFile exists for that.
    private const int MaxSelectedText = 64 * 1024;

    private readonly Func<BridgeServer?> _bridge;
    private readonly JoinableTaskFactory _jtf;
    private readonly IVsEditorAdaptersFactoryService? _adapters;
    private readonly ITextDocumentFactoryService? _documents;
    private readonly DispatcherTimer _timer;

    private IConnectionPoint? _connection;
    private uint _cookie;

    private IWpfTextView? _view;
    private string? _lastPushed;
    private bool _disposed;

    // System.IServiceProvider, not the OLE one that comes in with the connection-point types.
    public SelectionWatcher(System.IServiceProvider services, JoinableTaskFactory jtf, Func<BridgeServer?> bridge)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        _bridge = bridge;
        _jtf = jtf;

        var components = services.GetService(typeof(SComponentModel)) as IComponentModel;
        _adapters = components?.GetService<IVsEditorAdaptersFactoryService>();
        _documents = components?.GetService<ITextDocumentFactoryService>();

        _timer = new DispatcherTimer { Interval = Coalesce };
        _timer.Tick += (_, __) => { _timer.Stop(); Push(); };

        if (services.GetService(typeof(SVsTextManager)) is IConnectionPointContainer container)
        {
            var events = typeof(IVsTextViewEvents).GUID;
            container.FindConnectionPoint(ref events, out IConnectionPoint connection);
            if (connection != null)
            {
                connection.Advise(this, out _cookie);
                _connection = connection;
            }
        }

        // The view that already has focus never raises OnSetFocus, so take it on the way in.
        if (services.GetService(typeof(SVsTextManager)) is IVsTextManager manager &&
            manager.GetActiveView(1, null, out IVsTextView active) == 0)
        {
            Bind(active);
        }
    }

    // --- focus, from the text manager -----------------------------------------------------------

    public void OnSetFocus(IVsTextView view)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        Bind(view);
    }

    public void OnKillFocus(IVsTextView view) { }
    public void OnSetBuffer(IVsTextView view, IVsTextLines buffer) { }
    public void OnChangeCaretLine(IVsTextView view, int newLine, int oldLine) { }
    public void OnChangeScrollInfo(IVsTextView view, int bar, int minUnit, int maxUnits, int visibleUnits, int firstVisibleUnit) { }

    private void Bind(IVsTextView? adapter)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_disposed || adapter == null) return;

        IWpfTextView? view = null;
        try { view = _adapters?.GetWpfTextView(adapter); }
        catch { /* not a WPF-backed view; nothing to watch */ }

        if (view == null || ReferenceEquals(view, _view)) return;

        Unbind();
        _view = view;
        view.Selection.SelectionChanged += OnEditorMoved;
        view.Caret.PositionChanged += OnEditorMoved;
        view.Closed += OnViewClosed;

        // A new file is itself a move: report where the developer has landed.
        Schedule();
    }

    private void Unbind()
    {
        IWpfTextView? view = _view;
        _view = null;
        if (view == null) return;

        view.Selection.SelectionChanged -= OnEditorMoved;
        view.Caret.PositionChanged -= OnEditorMoved;
        view.Closed -= OnViewClosed;
    }

    private void OnViewClosed(object sender, EventArgs e) => Unbind();

    private void OnEditorMoved(object sender, EventArgs e) => Schedule();

    private void Schedule()
    {
        if (_disposed) return;
        _timer.Stop();
        _timer.Start();
    }

    // --- the push --------------------------------------------------------------------------------

    private void Push()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        BridgeServer? bridge = _bridge();
        IWpfTextView? view = _view;
        if (bridge == null || view == null || view.IsClosed) return;

        string? path = PathOf(view);
        if (path == null || !MayReport(path)) return;

        ITextSnapshot snapshot = view.TextSnapshot;
        int startOffset = view.Selection.Start.Position.Position;
        int endOffset = view.Selection.End.Position.Position;

        ITextSnapshotLine startLine = snapshot.GetLineFromPosition(startOffset);
        ITextSnapshotLine endLine = snapshot.GetLineFromPosition(endOffset);

        bool empty = view.Selection.IsEmpty;
        string text = empty ? string.Empty : Selected(view);

        // The protocol's positions are zero-based, which is what the editor already counts in.
        string signature = string.Join("|", path, startOffset, endOffset, empty ? "1" : "0");
        if (signature == _lastPushed) return;
        _lastPushed = signature;

        string json = IdeNotifications.SelectionChanged(
            path, text,
            startLine.LineNumber, startOffset - startLine.Start.Position,
            endLine.LineNumber, endOffset - endLine.Start.Position,
            empty);

        BridgeServer target = bridge;
        _ = _jtf.RunAsync(async () =>
        {
            try { await target.SendAsync(json); }
            catch { /* no client, or it went away mid-push; the next move will say the same thing */ }
        });
    }

    private static string Selected(IWpfTextView view)
    {
        try
        {
            string text = view.Selection.StreamSelectionSpan.SnapshotSpan.GetText();
            return text.Length <= MaxSelectedText ? text : text.Substring(0, MaxSelectedText);
        }
        catch
        {
            return string.Empty;
        }
    }

    private string? PathOf(IWpfTextView view)
    {
        try
        {
            ITextBuffer buffer = view.TextDataModel?.DocumentBuffer ?? view.TextBuffer;
            if (_documents != null && _documents.TryGetTextDocument(buffer, out ITextDocument document))
            {
                return document.FilePath;
            }
        }
        catch { /* a buffer with no document behind it - an output window, a diff pane */ }
        return null;
    }

    /// <summary>
    /// Whether a file may be described to the model at all. Same two rules the IDE tools apply: it
    /// has to sit in the workspace, and a file named like a secret is refused wherever it sits.
    /// </summary>
    private static bool MayReport(string path)
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (!(Package.GetGlobalService(typeof(DTE)) is DTE2 dte)) return false;
        return PathScope.Check(path, WorkspaceRoots.Collect(dte), resolve: RealPath.Resolve) == PathVerdict.Allowed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        Unbind();

#pragma warning disable VSTHRD010 // Disposed from the package's shutdown, which is already on the UI thread.
        try { if (_connection != null && _cookie != 0) _connection.Unadvise(_cookie); }
        catch { /* shutting down */ }
#pragma warning restore VSTHRD010
        _connection = null;
        _cookie = 0;
    }
}
