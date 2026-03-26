using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace RenderDocComments
{
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class RenderDocCommentsPackage : AsyncPackage
    {
        public const string PackageGuidString = "6381b007-68f8-48f1-9db5-f450f3a1a6b0";

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            RenderDocOptions.Instance.Load(this);

            await RenderDocOptionsCommand.InitializeAsync(this);

            await LicenseManager.RevalidateOnStartupAsync(this).ConfigureAwait(true);

            LicenseManager.StartPeriodicValidation(this);

            VSColorTheme.ThemeChanged += OnVsThemeChanged;
        }


        // ── Theme change handler ──────────────────────────────────────────────────

        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            if (!RenderDocOptions.Instance.EffectiveAutoRefresh) return;

            _ = JoinableTaskFactory.RunAsync(async () =>
            {
                await JoinableTaskFactory.SwitchToMainThreadAsync();
                SettingsChangedBroadcast.RaiseSettingsChanged();
            });
        }

        // ── Cleanup ───────────────────────────────────────────────────────────────

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                VSColorTheme.ThemeChanged -= OnVsThemeChanged;
            base.Dispose(disposing);
        }
    }
}