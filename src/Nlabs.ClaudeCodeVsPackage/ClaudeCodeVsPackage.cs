using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Nlabs.ClaudeCodeVsPackage.AgentPanel;
using Nlabs.ClaudeCodeVsPackage.Bridge;
using Nlabs.ClaudeCodeVsPackage.Bridge.Ide;
using Nlabs.ClaudeCodeVsPackage.Bridge.Mcp;
using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace Nlabs.ClaudeCodeVsPackage;

/// <summary>
/// The extension entry point.
///
/// Loaded in the background when a solution opens (AllowsBackgroundLoading), so loading never
/// blocks the UI thread. On load it starts a local bridge - a WebSocket server bound to
/// 127.0.0.1 - and writes a discovery lock file to <c>~/.claude/ide</c> so Claude Code finds
/// Visual Studio with its <c>/ide</c> command. The bridge speaks the Model Context Protocol,
/// so the extension is a first-class IDE connection; its tools are NOT namespaced behind an
/// <c>mcp__</c> prefix.
///
/// ProvideMenuResource is the easy-to-miss part: it tells Visual Studio to load and MERGE the
/// compiled command table ("Menus.ctmenu"). Without it the menu item never appears and nothing
/// errors out. The id must match the VSCTCompile ResourceName in the .csproj.
/// </summary>
[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
[ProvideMenuResource("Menus.ctmenu", 1)]
[ProvideToolWindow(typeof(AgentPanelToolWindow))]
[ProvideOptionPage(typeof(ExtensionOptionsPage), "Claude Code (nLabtech)", "General", 0, 0, supportsAutomation: true)]
[Guid(PackageGuidString)]
public sealed class ClaudeCodeVsPackage : AsyncPackage
{
    /// <summary>The package's unique id. The registration (pkgdef) matches on this.</summary>
    public const string PackageGuidString = "952c382f-7793-44ac-beab-e4c14cd9470c";

    private const string ServerName = "Visual Studio";
    private const string ServerVersion = "0.1.0";

    /// <summary>The local WebSocket bridge (127.0.0.1). Started on load, disposed on shutdown.</summary>
    private BridgeServer? _bridge;

    /// <summary>Manages proposed-change diffs and their accept/reject verdicts.</summary>
    private DiffSession? _diff;

    /// <summary>The discovery lock file writer, and the port whose lock is currently written.</summary>
    private readonly IdeLockFile _lockFile = new IdeLockFile();
    private int _lockedPort;

    /// <summary>Keeps the lock file's workspace folders in step with the open solution.</summary>
    private SolutionLockWatcher? _solutionWatcher;

    /// <summary>
    /// Pushes the editor selection to Claude Code as it moves. It outlives a bridge restart - it
    /// reads whichever bridge is current rather than holding one - so it is started once, with the
    /// package, and stopped with it.
    /// </summary>
    private SelectionWatcher? _selectionWatcher;

    /// <summary>The token the current lock file was written with; reused when it is rewritten.</summary>
    private string _lockedToken = string.Empty;

    protected override async Task InitializeAsync(
        CancellationToken cancellationToken,
        IProgress<ServiceProgressData> progress)
    {
        await base.InitializeAsync(cancellationToken, progress);

        // Adding a command and reading DTE both live on the UI thread.
        await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

        if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
        {
            commandService.AddCommand(new MenuCommand(
                OnRestartBridge, new CommandID(PackageGuids.CommandSet, PackageIds.RestartBridgeCommandId)));
            commandService.AddCommand(new MenuCommand(
                OnSendSelection, new CommandID(PackageGuids.CommandSet, PackageIds.SendSelectionCommandId)));
            commandService.AddCommand(new MenuCommand(
                OnOpenPanel, new CommandID(PackageGuids.CommandSet, PackageIds.OpenPanelCommandId)));
        }

        // Force the options page to load from storage now, so the settings below (and the panel's
        // masking) reflect the developer's choices instead of the defaults on the first run.
        GetDialogPage(typeof(ExtensionOptionsPage));

        if (ExtensionOptions.StartBridgeOnLoad)
        {
            StartBridge();
        }

        // Best effort: if the editor services are not there to subscribe to, the bridge still
        // answers everything it is asked - it just stops volunteering where the developer is.
        try { _selectionWatcher = new SelectionWatcher(this, JoinableTaskFactory, () => _bridge); }
        catch { _selectionWatcher = null; }
    }

    /// <summary>Starts the bridge, wires it to the MCP handler, and writes the discovery lock file.</summary>
    private void StartBridge()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        _bridge = new BridgeServer();
        _diff = new DiffSession(this, JoinableTaskFactory);
        var catalog = new VsToolCatalog(this, JoinableTaskFactory, _diff);
        var protocol = new McpProtocol(catalog, ServerName, ServerVersion);
        BridgeServer bridge = _bridge;

        // Each inbound MCP message is handled off the receive loop; a null reply (a
        // notification) is simply not sent back.
        bridge.MessageReceived += (_, json) =>
        {
            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                string? reply = await protocol.HandleAsync(json, CancellationToken.None);
                if (reply != null)
                {
                    await bridge.SendAsync(reply);
                }
            });
        };

        bridge.Start();

        // Advertise the endpoint so `claude` can discover Visual Studio. The lock file carries
        // only the port, this process id, the connection token and the open workspace folders -
        // nothing about the machine, the account or the subscription.
        _lockedPort = bridge.Port;
        _lockedToken = bridge.Token;
        WriteLockFile();

        // The folder list is what the CLI matches its working directory against, and it is the one
        // part of this that changes while Visual Studio runs - so follow the solution rather than
        // describing whatever happened to be open at load.
        _solutionWatcher?.Dispose();
        _solutionWatcher = SolutionLockWatcher.Start(
            GetService(typeof(SVsSolution)) as IVsSolution, WriteLockFile);
    }

    /// <summary>
    /// Publishes the endpoint so <c>claude</c> can discover Visual Studio. The file carries only the
    /// port, this process id, the connection token and the open workspace folders - nothing about
    /// the machine, the account or the subscription.
    /// </summary>
    private void WriteLockFile()
    {
        ThreadHelper.ThrowIfNotOnUIThread();
        if (_lockedPort == 0) return;

        _lockFile.Write(
            port: _lockedPort,
            authToken: _lockedToken,
            processId: CurrentProcessId(),
            ideName: ServerName,
            workspaceFolders: CollectWorkspaceFolders());
    }

    /// <summary>Removes the current lock file and tears down the bridge and diff session.</summary>
    private void StopBridge()
    {
        _solutionWatcher?.Dispose();
        _solutionWatcher = null;

        if (_lockedPort != 0)
        {
            _lockFile.Remove(_lockedPort);
            _lockedPort = 0;
            _lockedToken = string.Empty;
        }

        _diff?.Dispose();
        _diff = null;

        _bridge?.Dispose();
        _bridge = null;
    }

    /// <summary>Restarts the bridge and shows the connection info.</summary>
    private void OnRestartBridge(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        StopBridge();
        StartBridge();

        string info =
            $"Local bridge listening on 127.0.0.1:{_bridge!.Port}\n\n" +
            "Discovery lock file written to ~/.claude/ide.\n" +
            "In a terminal, run:  claude  then  /ide";

        VsShellUtilities.ShowMessageBox(
            this,
            info,
            "Claude Code bridge",
            OLEMSGICON.OLEMSGICON_INFO,
            OLEMSGBUTTON.OLEMSGBUTTON_OK,
            OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
    }

    /// <summary>
    /// Pushes the current editor selection to Claude Code as an at_mentioned notification -
    /// the Visual Studio equivalent of "@-mentioning" a file range, so the model picks up
    /// what the developer is pointing at. Only the path and the zero-based line range travel.
    /// </summary>
    private void OnSendSelection(object sender, EventArgs e)
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        BridgeServer? bridge = _bridge;
        if (bridge == null) return;
        if (!(GetGlobalService(typeof(DTE)) is DTE2 dte)) return;

        Document doc = dte.ActiveDocument;
        if (doc == null || !(doc.Selection is TextSelection selection)) return;

        // Visual Studio lines are one-based; the IDE protocol is zero-based.
        int startLine = Math.Max(0, selection.TopPoint.Line - 1);
        int endLine = Math.Max(0, selection.BottomPoint.Line - 1);

        string json = IdeNotifications.AtMentioned(doc.FullName, startLine, endLine);
        _ = JoinableTaskFactory.RunAsync(async () => await bridge.SendAsync(json));
    }

    /// <summary>Opens (creating if needed) the agentic Claude panel tool window.</summary>
    private void OnOpenPanel(object sender, EventArgs e)
    {
        _ = JoinableTaskFactory.RunAsync(async () =>
        {
            ToolWindowPane? window = await ShowToolWindowAsync(
                typeof(AgentPanelToolWindow), id: 0, create: true, cancellationToken: DisposalToken);
            if (window?.Frame == null)
            {
                throw new NotSupportedException("The Claude panel window could not be created.");
            }
        });
    }

    /// <summary>The directories of the open solution's projects; the CLI treats these as roots.</summary>
    private IEnumerable<string> CollectWorkspaceFolders()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        var folders = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Resolved synchronously on the UI thread - GetGlobalService is the safe (non-blocking)
        // way to reach DTE here, avoiding a sync-over-async wait on GetServiceAsync.
        if (!(GetGlobalService(typeof(DTE)) is DTE2 dte)) return folders;

        try
        {
            Solution? solution = dte.Solution;
            if (solution == null) return folders;

            string? solutionDir = null;
            try { if (!string.IsNullOrEmpty(solution.FullName)) solutionDir = Path.GetDirectoryName(solution.FullName); }
            catch { /* unsaved solution */ }
            if (solutionDir != null && seen.Add(solutionDir)) folders.Add(solutionDir);

            foreach (Project project in solution.Projects)
            {
                try
                {
                    if (string.IsNullOrEmpty(project.FullName)) continue;
                    string? dir = Path.GetDirectoryName(project.FullName);
                    if (dir != null && seen.Add(dir)) folders.Add(dir);
                }
                catch { /* solution folders have no FullName */ }
            }
        }
        catch { /* best effort - an empty list is fine */ }

        return folders;
    }

    private static int CurrentProcessId()
    {
        using (var process = System.Diagnostics.Process.GetCurrentProcess())
        {
            return process.Id;
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _selectionWatcher?.Dispose();
            _selectionWatcher = null;
            StopBridge();
        }

        base.Dispose(disposing);
    }
}
