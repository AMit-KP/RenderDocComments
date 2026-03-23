using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.PlatformUI;   // VSColorTheme
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;

namespace RenderDocComments
{
    /// <summary>
    /// Extension package.
    ///
    /// Responsibilities added beyond the original stub:
    ///  • Loads persisted <see cref="RenderDocOptions"/> at startup.
    ///  • Subscribes to <see cref="VSColorTheme.ThemeChanged"/> to auto-refresh
    ///    adornments when the VS colour theme changes (Premium feature 1).
    ///  • Registers the "Extensions > RenderDocOptions" menu command.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    // Tells VS to load this package when a CSharp/Basic/FSharp/C++ document is opened
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class RenderDocCommentsPackage : AsyncPackage
    {
        public const string PackageGuidString = "6381b007-68f8-48f1-9db5-f450f3a1a6b0";

        // ── Package initialisation ────────────────────────────────────────────────

        protected override async Task InitializeAsync(
            CancellationToken cancellationToken,
            IProgress<ServiceProgressData> progress)
        {
            // Background-thread work first
            await base.InitializeAsync(cancellationToken, progress);

            // Switch to the UI thread for everything that needs it
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);

            // 1. Load persisted settings
            RenderDocOptions.Instance.Load(this);

            // 2. Register the Extensions > RenderDocOptions menu command
            await RenderDocOptionsCommand.InitializeAsync(this);

            // 3. Subscribe to VS theme changes (Premium 1 — auto-refresh)
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
        }

        // ── Theme change handler ──────────────────────────────────────────────────

        /// <summary>
        /// Fired by VS when the user switches colour theme (e.g. Dark → Light).
        ///
        /// FREE behaviour  : do nothing — the user must reopen the file to see
        ///                   updated adornment colours (original behaviour).
        /// Premium behaviour   : broadcast <see cref="SettingsChangedBroadcast.RaiseSettingsChanged"/>
        ///                   so every live <see cref="DocCommentRenderer.DocCommentAdornmentTagger"/>
        ///                   invalidates its cache and rebuilds tags with the new theme colours.
        /// </summary>
        private void OnVsThemeChanged(ThemeChangedEventArgs e)
        {
            if (!RenderDocOptions.Instance.EffectiveAutoRefresh) return;

            // Must be on UI thread to raise the broadcast safely
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