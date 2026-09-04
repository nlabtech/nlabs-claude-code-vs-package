using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>How a proposed diff ended: the human accepted it (and its final text) or rejected it.</summary>
    internal readonly struct DiffOutcome
    {
        private DiffOutcome(bool accepted, string finalContents, string tabName)
        {
            Accepted = accepted;
            FinalContents = finalContents;
            TabName = tabName;
        }

        public bool Accepted { get; }
        public string FinalContents { get; }
        public string TabName { get; }

        public static DiffOutcome Accept(string finalContents) => new DiffOutcome(true, finalContents, string.Empty);
        public static DiffOutcome Reject(string tabName) => new DiffOutcome(false, string.Empty, tabName);
    }

    /// <summary>
    /// Shows a proposed change as a Visual Studio diff (current | proposed) and waits for the
    /// developer's verdict - the native openDiff contract, which does not answer until the
    /// human decides.
    ///
    /// Single-writer principle: the agent never touches the user's buffer. Both panes are temp
    /// files the session owns - left holds the current contents, right holds the proposal and is
    /// the only editable pane. The developer reviews, edits if they like, then either SAVES the
    /// right pane (accept: the session writes it to the real file and reports FILE_SAVED with the
    /// final text) or CLOSES the diff (reject: nothing is written, DIFF_REJECTED is reported).
    /// Detection rides the Running Document Table - OnAfterSave for accept, OnBeforeLastDocumentUnlock
    /// for close - so a plain tab switch never counts as either.
    /// </summary>
    internal sealed class DiffSession : IDisposable
    {
        private readonly IAsyncServiceProvider _services;
        private readonly JoinableTaskFactory _jtf;
        private readonly object _gate = new object();

        // Right-pane (proposal) moniker -> the awaiting call. The RDT hands us monikers, so we key on them.
        private readonly Dictionary<string, Pending> _pending =
            new Dictionary<string, Pending>(StringComparer.OrdinalIgnoreCase);

        private IVsRunningDocumentTable? _rdt;
        private uint _rdtCookie;
        private bool _disposed;

        public DiffSession(IAsyncServiceProvider services, JoinableTaskFactory jtf)
        {
            _services = services;
            _jtf = jtf;
        }

        private sealed class Pending
        {
            public string RealFilePath = string.Empty;
            public string LeftTempPath = string.Empty;
            public string RightTempPath = string.Empty;
            public string TabName = string.Empty;
            public TaskCompletionSource<DiffOutcome> Completion =
                new TaskCompletionSource<DiffOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            public bool Settled;
        }

        /// <summary>
        /// Opens the comparison window and returns only once the developer accepts (saves) or
        /// rejects (closes) it. The caller may be on any thread.
        /// </summary>
        public async Task<DiffOutcome> ShowAsync(
            string realFilePath, string proposedContents, string tabName, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(realFilePath))
                throw new ArgumentException("A file path is required.", nameof(realFilePath));

            await _jtf.SwitchToMainThreadAsync(ct);
            EnsureRdtSubscribed();

            string currentContents = File.Exists(realFilePath) ? File.ReadAllText(realFilePath) : string.Empty;

            // Both panes are session-owned temp files; the real file is written only on accept.
            string stamp = Guid.NewGuid().ToString("n");
            string leftPath = Path.Combine(Path.GetTempPath(), "nlabs_current_" + stamp + "_" + Path.GetFileName(realFilePath));
            string rightPath = Path.Combine(Path.GetTempPath(), "nlabs_proposed_" + stamp + "_" + Path.GetFileName(realFilePath));
            File.WriteAllText(leftPath, currentContents);
            File.WriteAllText(rightPath, proposedContents ?? string.Empty);

            string caption = string.IsNullOrEmpty(tabName) ? "Proposed change" : tabName;

            if (!(await _services.GetServiceAsync(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
            {
                SafeDelete(leftPath);
                SafeDelete(rightPath);
                throw new InvalidOperationException("The diff service is unavailable.");
            }

            var pending = new Pending
            {
                RealFilePath = realFilePath,
                LeftTempPath = leftPath,
                RightTempPath = rightPath,
                TabName = caption,
            };

            lock (_gate) { _pending[rightPath] = pending; }

            // Left is marked temporary (read-only of record); right stays editable so a save = accept.
            diff.OpenComparisonWindow2(
                leftPath,
                rightPath,
                caption,
                null,
                "Current",
                "Proposed (Claude Code)",
                null,
                null,
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_LeftFileIsTemporary);

            using (ct.Register(() => Settle(rightPath, p => DiffOutcome.Reject(p.TabName))))
            {
                // Intentionally awaiting a TaskCompletionSource that the Running Document Table
                // events resolve when the developer accepts or rejects - the deferred verdict is
                // the whole point of openDiff, so this is not a stray unstarted task.
#pragma warning disable VSTHRD003
                return await pending.Completion.Task.ConfigureAwait(false);
#pragma warning restore VSTHRD003
            }
        }

        private void EnsureRdtSubscribed()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_rdt != null) return;

            _rdt = ThreadHelper.JoinableTaskFactory.Run(async () =>
                await _services.GetServiceAsync(typeof(SVsRunningDocumentTable)) as IVsRunningDocumentTable);
            if (_rdt != null)
            {
                _rdt.AdviseRunningDocTableEvents(new RdtSink(this), out _rdtCookie);
            }
        }

        // Accept: the developer saved the proposal. Apply it to the real file, report FILE_SAVED.
        private void OnSaved(string moniker)
        {
            Settle(moniker, p =>
            {
                string finalText = File.Exists(p.RightTempPath) ? File.ReadAllText(p.RightTempPath) : string.Empty;
                File.WriteAllText(p.RealFilePath, finalText);
                return DiffOutcome.Accept(finalText);
            });
        }

        // Reject: the proposal window is closing without a save. Nothing is written.
        private void OnClosed(string moniker)
        {
            Settle(moniker, p => DiffOutcome.Reject(p.TabName));
        }

        private void Settle(string moniker, Func<Pending, DiffOutcome> outcome)
        {
            Pending? pending = null;
            lock (_gate)
            {
                if (_pending.TryGetValue(moniker, out var found) && !found.Settled)
                {
                    found.Settled = true;
                    _pending.Remove(moniker);
                    pending = found;
                }
            }

            if (pending == null) return;

            DiffOutcome result;
            try
            {
                result = outcome(pending);
            }
            catch (Exception ex)
            {
                pending.Completion.TrySetException(ex);
                CleanupTemps(pending);
                return;
            }

            pending.Completion.TrySetResult(result);
            CleanupTemps(pending);
        }

        private static void CleanupTemps(Pending pending)
        {
            SafeDelete(pending.LeftTempPath);
            SafeDelete(pending.RightTempPath);
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Fail any still-open diffs so no caller waits forever.
            Pending[] leftover;
            lock (_gate)
            {
                leftover = new Pending[_pending.Count];
                _pending.Values.CopyTo(leftover, 0);
                _pending.Clear();
            }
            foreach (var p in leftover)
            {
                p.Completion.TrySetResult(DiffOutcome.Reject(p.TabName));
                CleanupTemps(p);
            }

            if (_rdt != null && _rdtCookie != 0)
            {
                try { _rdt.UnadviseRunningDocTableEvents(_rdtCookie); } catch { }
                _rdtCookie = 0;
            }
        }

        /// <summary>
        /// Bridges Running Document Table events to the session. A save on the proposal pane is an
        /// accept; the last unlock (window closing) is a reject. Both look up the document's moniker
        /// from its cookie and let the session decide whether it belongs to an open diff.
        /// </summary>
        private sealed class RdtSink : IVsRunningDocTableEvents3
        {
            private readonly DiffSession _owner;
            public RdtSink(DiffSession owner) => _owner = owner;

            public int OnAfterSave(uint docCookie)
            {
                ThreadHelper.ThrowIfNotOnUIThread(); // RDT events are raised on the UI thread.
                string? moniker = MonikerOf(docCookie);
                if (moniker != null) _owner.OnSaved(moniker);
                return VSConstants.S_OK;
            }

            public int OnBeforeLastDocumentUnlock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining)
            {
                ThreadHelper.ThrowIfNotOnUIThread(); // RDT events are raised on the UI thread.
                // Only when the document is truly leaving the table (no locks left) - not on a tab switch.
                if (dwReadLocksRemaining == 0 && dwEditLocksRemaining == 0)
                {
                    string? moniker = MonikerOf(docCookie);
                    if (moniker != null) _owner.OnClosed(moniker);
                }
                return VSConstants.S_OK;
            }

            private string? MonikerOf(uint docCookie)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                var rdt = _owner._rdt;
                if (rdt == null) return null;
                try
                {
                    rdt.GetDocumentInfo(docCookie, out _, out _, out _, out string moniker, out _, out _, out _);
                    return moniker;
                }
                catch
                {
                    return null;
                }
            }

            // Unused RDT events.
            public int OnAfterFirstDocumentLock(uint docCookie, uint dwRDTLockType, uint dwReadLocksRemaining, uint dwEditLocksRemaining) => VSConstants.S_OK;
            public int OnAfterAttributeChange(uint docCookie, uint grfAttribs) => VSConstants.S_OK;
            public int OnBeforeDocumentWindowShow(uint docCookie, int fFirstShow, IVsWindowFrame pFrame) => VSConstants.S_OK;
            public int OnAfterDocumentWindowHide(uint docCookie, IVsWindowFrame pFrame) => VSConstants.S_OK;
            public int OnAfterAttributeChangeEx(uint docCookie, uint grfAttribs, IVsHierarchy pHierOld, uint itemidOld, string pszMkDocumentOld, IVsHierarchy pHierNew, uint itemidNew, string pszMkDocumentNew) => VSConstants.S_OK;
            public int OnBeforeSave(uint docCookie) => VSConstants.S_OK;
        }
    }
}
