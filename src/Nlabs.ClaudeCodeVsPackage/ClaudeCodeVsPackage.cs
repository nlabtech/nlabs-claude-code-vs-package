using System;
using System.ComponentModel.Design;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// The extension entry point.
    ///
    /// Loaded in the background when a solution opens (AllowsBackgroundLoading), so
    /// loading never blocks the UI thread. This skeleton version does no real work;
    /// later steps will start the local bridge (a WebSocket server that listens only
    /// on 127.0.0.1) from here.
    ///
    /// UseManagedResourcesOnly = true: package resources are looked up on the managed
    /// side.
    ///
    /// ProvideMenuResource is the easy-to-miss part: it tells Visual Studio to load and
    /// MERGE the compiled command table ("Menus.ctmenu") from this package. The .vsct can
    /// compile, the .vsix can build, and the command handler can register - but WITHOUT
    /// this attribute the menu item simply never appears, and nothing errors out. The id
    /// "Menus.ctmenu" must match the VSCTCompile ResourceName in the .csproj.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [Guid(PackageGuidString)]
    public sealed class ClaudeCodeVsPackage : AsyncPackage
    {
        /// <summary>The package's unique id. The registration (pkgdef) matches on this.</summary>
        public const string PackageGuidString = "952c382f-7793-44ac-beab-e4c14cd9470c";

        /// <summary>The local WebSocket bridge (127.0.0.1). Started on load, disposed on shutdown.</summary>
        private Bridge.BridgeServer? _bridge;

        /// <summary>Routes bridge messages to Visual Studio actions.</summary>
        private BridgeRouter? _router;

        /// <summary>
        /// Package initialization. After the base call we may switch to the main thread,
        /// but we do nothing here yet.
        /// </summary>
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Adding a command touches the menu service, which lives on the UI thread.
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            if (await GetServiceAsync(typeof(IMenuCommandService)) is OleMenuCommandService commandService)
            {
                var commandId = new CommandID(PackageGuids.CommandSet, PackageIds.RestartBridgeCommandId);
                commandService.AddCommand(new MenuCommand(OnRestartBridge, commandId));
            }

            // Start the local bridge and wire it to Visual Studio through the router.
            StartBridge();
        }

        /// <summary>Creates the bridge server + router and starts listening on 127.0.0.1.</summary>
        private void StartBridge()
        {
            _bridge = new Bridge.BridgeServer();
            _router = new BridgeRouter(_bridge, JoinableTaskFactory, this);
            _bridge.Start();
        }

        /// <summary>Restarts the bridge and shows the connection info to paste into Claude Code.</summary>
        private void OnRestartBridge(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            _bridge?.Dispose();
            StartBridge();

            string info =
                $"Local bridge listening on 127.0.0.1:{_bridge!.Port}\n\n" +
                "Token (paste into Claude Code):\n" +
                _bridge.Token;

            VsShellUtilities.ShowMessageBox(
                this,
                info,
                "Local Bridge",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _bridge?.Dispose();
                _bridge = null;
                _router = null;
            }

            base.Dispose(disposing);
        }
    }
}
