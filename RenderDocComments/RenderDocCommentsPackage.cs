using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio.PlatformUI;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Task = System.Threading.Tasks.Task;

namespace RenderDocComments
{
    /// <summary>
    /// The main Visual Studio extension package for Render Doc Comments.<br/>
    /// This class is the entry point for the VSIX, responsible for initialization,<br/>
    /// menu registration, license management, and theme change handling.
    /// </summary>
    /// <remarks>
    /// <para>The package is configured with the following attributes:</para>
    /// <list type="bullet">
    /// <item><description><see cref="PackageRegistrationAttribute"/> — Registers as a managed-only package with background loading support.</description></item>
    /// <item><description><see cref="GuidAttribute"/> — Unique package identifier (<c>6381b007-68f8-48f1-9db5-f450f3a1a6b0</c>).</description></item>
    /// <item><description><see cref="ProvideMenuResourceAttribute"/> — Provides the menu resource (<c>Menus.ctmenu</c>) for the options command.</description></item>
    /// <item><description><see cref="ProvideAutoLoadAttribute"/> — Auto-loads on both <see cref="UIContextGuids.NoSolution"/> and <see cref="UIContextGuids.SolutionExists"/> contexts for immediate availability.</description></item>
    /// </list>
    /// </remarks>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [Guid(PackageGuidString)]
    [ProvideMenuResource("Menus.ctmenu", 1)]
    [ProvideAutoLoad(UIContextGuids.NoSolution, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(UIContextGuids.SolutionExists, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class RenderDocCommentsPackage : AsyncPackage
    {
        /// <summary>
        /// The unique GUID string identifying this package in the Visual Studio registry.<br/>
        /// Must match the GUID declared in the <c>.vsct</c> file and <c>source.extension.vsixmanifest</c>.
        /// </summary>
        public const string PackageGuidString = "6381b007-68f8-48f1-9db5-f450f3a1a6b0";

        /// <summary>
        /// Asynchronously initializes the package, loading settings, registering commands,<br/>
        /// validating the license, and subscribing to theme change events.
        /// </summary>
        /// <param name="cancellationToken">
        /// A <see cref="CancellationToken"/> used to signal that initialization should be cancelled<br/>
        /// (e.g., if Visual Studio is shutting down during load).
        /// </param>
        /// <param name="progress">
        /// A progress reporter for reporting initialization progress to the IDE.
        /// </param>
        /// <returns>
        /// A <see cref="Task"/> representing the asynchronous initialization operation.
        /// </returns>
        /// <remarks>
        /// <para>The initialization sequence executes the following steps:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Base initialization:</b> Calls <see cref="AsyncPackage.InitializeAsync"/> to complete the standard package setup.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Main thread switch:</b> Switches to the UI thread via <see cref="JoinableTaskFactory.SwitchToMainThreadAsync"/> for operations requiring UI access.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Settings load:</b> Calls <see cref="RenderDocOptions.Load"/> to restore user preferences from the Visual Studio settings store.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Command registration:</b> Calls <see cref="RenderDocOptionsCommand.InitializeAsync"/> to register the "Extensions &gt; Render Doc Options" menu command.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>License re-validation:</b> Calls <see cref="LicenseManager.RevalidateOnStartupAsync"/> to verify the stored license key with the activation server.<br/>
        /// If the key has been revoked or refunded, Premium access is silently removed.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Periodic validation:</b> Calls <see cref="LicenseManager.StartPeriodicValidation"/> to start a 12-hour background timer that re-validates the license.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Theme subscription:</b> Subscribes to <see cref="VSColorTheme.ThemeChanged"/> to detect Visual Studio theme switches.
        /// </description>
        /// </item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Handles Visual Studio theme change events, broadcasting a settings change<br/>
        /// to trigger adornment color refresh when Premium auto-refresh is enabled.
        /// </summary>
        /// <param name="e">
        /// A <see cref="ThemeChangedEventArgs"/> containing information about the new theme.
        /// </param>
        /// <remarks>
        /// <para>The method checks <see cref="RenderDocOptions.EffectiveAutoRefresh"/> first. If auto-refresh<br/>
        /// is disabled (or Premium is locked), the handler returns immediately — requiring the user<br/>
        /// to reopen files to see updated colors.</para>
        /// <para>When auto-refresh is enabled, the method:</para>
        /// <list type="number">
        /// <item><description>Switches to the main thread via <see cref="JoinableTaskFactory.RunAsync"/>.</description></item>
        /// <item><description>Raises <see cref="SettingsChangedBroadcast.RaiseSettingsChanged"/> to notify all taggers<br/>
        /// that they should rebuild their cached tags with the new theme colors.</description></item>
        /// </list>
        /// <para>The asynchronous fire-and-forget pattern (<c>_ = ...</c>) ensures the theme change<br/>
        /// handler doesn't block the IDE while the rebuild is in progress.</para>
        /// </remarks>
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

        /// <summary>
        /// Releases resources used by the package, including unsubscribing from theme change events.
        /// </summary>
        /// <param name="disposing">
        /// <c>true</c> if the method is called from <see cref="IDisposable.Dispose"/>;<br/>
        /// <c>false</c> if called from the finalizer (which this class doesn't have).
        /// </param>
        /// <remarks>
        /// <para>When <paramref name="disposing"/> is <c>true</c>, the method:</para>
        /// <list type="bullet">
        /// <item><description>Unsubscribes from <see cref="VSColorTheme.ThemeChanged"/> to prevent memory leaks and dangling event references.</description></item>
        /// </list>
        /// <para>The base class <see cref="AsyncPackage.Dispose(bool)"/> is always called to ensure<br/>
        /// proper cleanup of the Visual Studio package infrastructure.</para>
        /// </remarks>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
                VSColorTheme.ThemeChanged -= OnVsThemeChanged;
            base.Dispose(disposing);
        }
    }
}