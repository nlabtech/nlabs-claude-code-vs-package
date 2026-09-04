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

        /// <summary>Shows agent-proposed changes as a diff; the bridge calls into this.</summary>
        private DiffSession? _diffSession;

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

            // The diff surface the bridge routes agent proposals to.
            _diffSession = new DiffSession(this);

            // Next step (local bridge): will be started here.
        }

        /// <summary>Placeholder handler; wiring to the bridge lifecycle comes later.</summary>
        private void OnRestartBridge(object sender, EventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            VsShellUtilities.ShowMessageBox(
                this,
                "The local bridge will restart.",
                "Local Bridge",
                OLEMSGICON.OLEMSGICON_INFO,
                OLEMSGBUTTON.OLEMSGBUTTON_OK,
                OLEMSGDEFBUTTON.OLEMSGDEFBUTTON_FIRST);
        }
    }
}
