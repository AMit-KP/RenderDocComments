using System;
using System.Linq;
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

            // 1. Load persisted settings
            RenderDocOptions.Instance.Load(this);

            // 2. Register menu command
            await RenderDocOptionsCommand.InitializeAsync(this);

            // 3. Register renderdoccomments:// URI scheme in HKCU (no admin needed)
            LicenseManager.EnsureUriSchemeRegistered();

            // 4. Silently re-validate stored licence key
            await LicenseManager.RevalidateOnStartupAsync(this).ConfigureAwait(true);

            // 5. Start 12-hour background revalidation
            LicenseManager.StartPeriodicValidation(this);

            // 6. If this VS instance was launched by the OS URI handler (browser redirect),
            //    write the license key to a temp file for the existing VS instance's
            //    PurchaseActivationWindow to pick up, then do nothing else.
            //    The existing instance's window polls the file every second.
            HandleUriRedirectIfPresent();

            // 7. Subscribe to VS theme changes
            VSColorTheme.ThemeChanged += OnVsThemeChanged;
        }

        // ── URI redirect handler ──────────────────────────────────────────────────

        private static void HandleUriRedirectIfPresent()
        {
            try
            {
                var url = Environment.GetCommandLineArgs()
                    .FirstOrDefault(a => a.StartsWith("renderdoccomments://",
                        StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrEmpty(url)) return;

                var licenseKey = ParseQueryParam(url, "license_key");
                if (string.IsNullOrEmpty(licenseKey)) return;

                // Write to temp file — PurchaseActivationWindow polls this every second
                LicenseManager.WritePendingLicenseKey(licenseKey);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[RenderDocComments] URI redirect handling failed: {ex.Message}");
            }
        }

        private static string ParseQueryParam(string url, string param)
        {
            int q = url.IndexOf('?');
            if (q < 0) return null;

            string result = null;
            foreach (var pair in url.Substring(q + 1).Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq < 0) continue;

                var key = Uri.UnescapeDataString(pair.Substring(0, eq).Trim());
                var value = Uri.UnescapeDataString(pair.Substring(eq + 1).Trim());

                if (string.Equals(key, param, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(value)
                    && !value.StartsWith("{"))
                    result = value; // keep last match, skip unreplaced placeholders
            }

            return result;
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