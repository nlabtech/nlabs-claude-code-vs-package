using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
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
    /// side. Once a command table (.ctmenu) is added in a later step, this will require
    /// the correct resource stream to be found; there are no commands yet.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClaudeCodeVsPackage : AsyncPackage
    {
        /// <summary>The package's unique id. The registration (pkgdef) matches on this.</summary>
        public const string PackageGuidString = "952c382f-7793-44ac-beab-e4c14cd9470c";

        /// <summary>
        /// Package initialization. After the base call we may switch to the main thread,
        /// but we do nothing here yet.
        /// </summary>
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Next step (local bridge): will be started here.
        }
    }
}
