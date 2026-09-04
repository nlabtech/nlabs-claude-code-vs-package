using System;
using System.IO;
using System.Threading;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Shows a proposed change as a Visual Studio diff window (current | proposed).
    ///
    /// Single-writer principle: the external agent (Claude Code, over the local bridge)
    /// never edits the user's buffer directly. It writes its proposal to a temp file; VS
    /// opens a diff against the file on disk; the human stays the only one who actually
    /// applies the change. One writer of record - no silent edits behind the developer.
    /// </summary>
    internal sealed class DiffSession
    {
        private readonly IAsyncServiceProvider _services;

        public DiffSession(IAsyncServiceProvider services) => _services = services;

        /// <summary>
        /// Opens the comparison window. Must run on the UI thread; the caller may be on any.
        /// </summary>
        public async Task ShowAsync(string currentFilePath, string proposedText, CancellationToken ct)
        {
            if (string.IsNullOrEmpty(currentFilePath))
                throw new ArgumentException("A current file path is required.", nameof(currentFilePath));

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);

            // The proposal lands in a temp file the agent owns; the user's file is untouched.
            var proposedPath = Path.Combine(
                Path.GetTempPath(),
                "proposed_" + Path.GetFileName(currentFilePath));
            File.WriteAllText(proposedPath, proposedText ?? string.Empty);

            if (!(await _services.GetServiceAsync(typeof(SVsDifferenceService)) is IVsDifferenceService diff))
                return;

            // Marking the right (proposed) file temporary tells VS it may clean it up and
            // must not treat it as an editable document of record.
            diff.OpenComparisonWindow2(
                currentFilePath,                 // left  = current, on disk
                proposedPath,                    // right = proposed, temp
                "Proposed change",               // caption
                null,                            // tooltip
                "Current",                       // left label
                "Proposed (Claude Code)",        // right label
                null,                            // inline label
                null,                            // roles
                (uint)__VSDIFFSERVICEOPTIONS.VSDIFFOPT_RightFileIsTemporary);
        }
    }
}
