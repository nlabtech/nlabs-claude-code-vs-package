using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// Keeps the discovery lock file in step with whatever solution is actually open.
///
/// The CLI finds this IDE by reading the lock files and picking the one whose workspace folders
/// contain its own working directory. That makes the folder list the matching key, not a detail -
/// and it is the one thing about a Visual Studio session that changes while the session runs.
///
/// Written once at load, the file is wrong for most of the day: Visual Studio usually starts with
/// no solution open (so the list is empty and nothing can match), and opening or switching a
/// solution afterwards leaves the file describing a workspace that is no longer there. Either way
/// <c>/ide</c> reports no IDE, or attaches to the wrong one, for reasons nothing on screen explains.
///
/// So the file is rewritten whenever the solution opens or closes. The port and token do not change
/// with it - only the folders do - which keeps an already-connected CLI attached across a switch.
/// </summary>
internal sealed class SolutionLockWatcher : IVsSolutionEvents, IDisposable
{
    private readonly IVsSolution? _solution;
    private readonly Action _refresh;
    private uint _cookie;
    private bool _disposed;

    private SolutionLockWatcher(IVsSolution? solution, Action refresh)
    {
        _solution = solution;
        _refresh = refresh;
    }

    /// <summary>
    /// Starts watching, and refreshes once straight away so a solution that was already open before
    /// the package loaded is described correctly rather than waiting for the next open.
    /// </summary>
    public static SolutionLockWatcher Start(IVsSolution? solution, Action refresh)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var watcher = new SolutionLockWatcher(solution, refresh);
        solution?.AdviseSolutionEvents(watcher, out watcher._cookie);
        watcher.Refresh();
        return watcher;
    }

    private void Refresh()
    {
        // Best-effort: a lock file that cannot be rewritten is not worth taking the IDE down for.
        try { _refresh(); }
        catch { }
    }

    public int OnAfterOpenSolution(object pUnkReserved, int fNewSolution)
    {
        Refresh();
        return VSConstants.S_OK;
    }

    public int OnAfterCloseSolution(object pUnkReserved)
    {
        Refresh();
        return VSConstants.S_OK;
    }

    // A folder or project coming and going does not change the solution's own root, which is what
    // the CLI matches on, so the rest of the interface is deliberately inert.
    public int OnAfterOpenProject(IVsHierarchy pHierarchy, int fAdded) => VSConstants.S_OK;
    public int OnQueryCloseProject(IVsHierarchy pHierarchy, int fRemoving, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseProject(IVsHierarchy pHierarchy, int fRemoved) => VSConstants.S_OK;
    public int OnAfterLoadProject(IVsHierarchy pStubHierarchy, IVsHierarchy pRealHierarchy) => VSConstants.S_OK;
    public int OnQueryUnloadProject(IVsHierarchy pRealHierarchy, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeUnloadProject(IVsHierarchy pRealHierarchy, IVsHierarchy pStubHierarchy) => VSConstants.S_OK;
    public int OnQueryCloseSolution(object pUnkReserved, ref int pfCancel) => VSConstants.S_OK;
    public int OnBeforeCloseSolution(object pUnkReserved) => VSConstants.S_OK;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

#pragma warning disable VSTHRD010 // Disposed from the package's shutdown, which is already on the UI thread.
        try { if (_cookie != 0) _solution?.UnadviseSolutionEvents(_cookie); }
        catch { }
#pragma warning restore VSTHRD010
        _cookie = 0;
    }
}
