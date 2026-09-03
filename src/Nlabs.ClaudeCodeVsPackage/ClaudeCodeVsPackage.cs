using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace Nlabs.ClaudeCodeVsPackage
{
    /// <summary>
    /// Eklentinin giriş noktası.
    ///
    /// Solution açıldığında arka planda yüklenir (AllowsBackgroundLoading) ve
    /// yüklenme UI iş parçacığını bloke etmez. Bu iskelet sürümde ağır iş yoktur;
    /// ilerideki adımlarda yerel köprü (yalnızca 127.0.0.1 dinleyen WebSocket
    /// sunucusu) burada başlatılacak.
    ///
    /// UseManagedResourcesOnly = true: paket kaynakları yönetilen (managed)
    /// tarafta aranır. Menü tablosu (.ctmenu) sonraki adımda eklenince bu, doğru
    /// kaynak akışının bulunmasını gerektirecek; şimdilik komut yok.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    [Guid(PackageGuidString)]
    public sealed class ClaudeCodeVsPackage : AsyncPackage
    {
        /// <summary>Paketin benzersiz kimliği. Kayıt (pkgdef) bununla eşleşir.</summary>
        public const string PackageGuidString = "952c382f-7793-44ac-beab-e4c14cd9470c";

        /// <summary>
        /// Paket başlatma. base çağrısından sonra artık ana iş parçacığına
        /// geçilebilir; ama burada henüz bir şey yapmıyoruz.
        /// </summary>
        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);

            // Sonraki adım (yerel köprü): burada başlatılacak.
        }
    }
}
