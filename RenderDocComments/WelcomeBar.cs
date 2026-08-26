using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RenderDocComments
{
    /// <summary>
    /// Shows a one-time Visual Studio InfoBar (the thin banner docked directly<br/>
    /// below the main toolbar) welcoming new users and pointing them to the<br/>
    /// "Extensions &gt; Render Doc Options" menu entry.
    /// </summary>
    /// <remarks>
    /// <para>Behaviour:</para>
    /// <list type="bullet">
    /// <item><description>Displayed exactly once — on the first launch after installation.</description></item>
    /// <item><description><b>Open options now</b> dismisses the banner and opens the options dialog directly.</description></item>
    /// <item><description>The ✕ button dismisses the banner permanently.</description></item>
    /// </list>
    /// <para>The "shown" flag persists under the package's <see cref="AsyncPackage.UserRegistryRoot"/><br/>
    /// so returning users are never nagged again.</para>
    /// </remarks>
    internal static class WelcomeBar
    {
        private const string RegistrySubKeyName = "WelcomeNotice";
        private const int StartupDelaySeconds = 10;

        // ── Entry point ───────────────────────────────────────────────────────────

        /// <summary>
        /// Shows the welcome banner when it has never been shown before.<br/>
        /// Designed to be called fire-and-forget from package initialization;<br/>
        /// every failure path is swallowed so it can never break IDE startup.
        /// </summary>
        /// <param name="package">
        /// The hosting <see cref="RenderDocCommentsPackage"/>.
        /// </param>
        public static async Task TryShowAsync(RenderDocCommentsPackage package)
        {
            try
            {
                if (HasAlreadyShown(package)) return;

                // Give the IDE time to finish starting up before showing anything.
                await Task.Delay(TimeSpan.FromSeconds(StartupDelaySeconds), package.DisposalToken);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                await ShowAsync(package);

                WriteState(package, "Shown", 1);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Log(ex);
            }
        }

        // ── InfoBar plumbing ──────────────────────────────────────────────────────

        /// <summary>
        /// Creates the InfoBar UI element and attaches it to the main window's<br/>
        /// InfoBar host (the strip between the toolbar and the document tabs).
        /// </summary>
        /// <param name="package">
        /// The hosting <see cref="RenderDocCommentsPackage"/>.
        /// </param>
        private static async Task ShowAsync(RenderDocCommentsPackage package)
        {
            await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);

            var factory = await package.GetServiceAsync(typeof(SVsInfoBarUIFactory)) as IVsInfoBarUIFactory;
            if (factory == null) return;

            var model = new InfoBarModel(
                new[]
                {
                    new InfoBarTextSpan("Welcome to Render Doc Comments! "),
                    new InfoBarTextSpan("All settings live under Extensions → Render Doc Options."),
                },
                new[]
                {
                    new InfoBarHyperlink("Open options now", "open"),
                },
                KnownMonikers.Settings,
                isCloseButtonVisible: true);

            IVsInfoBarUIElement bar = factory.CreateInfoBar(model);
            bar.Advise(new WelcomeInfoBarEvents(package), out _);

            if (!(await package.GetServiceAsync(typeof(SVsShell)) is IVsShell shell)) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))) return;
            if (hostObj is IVsInfoBarHost host)
                host.AddInfoBar(bar);
        }

        /// <summary>
        /// Handles clicks on the banner's hyperlink and ✕ button. Both paths<br/>
        /// simply close; "Open options now" additionally launches the dialog.
        /// </summary>
        private sealed class WelcomeInfoBarEvents : IVsInfoBarUIEvents
        {
            private readonly RenderDocCommentsPackage _package;
            private bool _handled;

            /// <summary>
            /// Initializes the event sink.
            /// </summary>
            /// <param name="package">The hosting package.</param>
            public WelcomeInfoBarEvents(RenderDocCommentsPackage package)
            {
                _package = package;
            }

            /// <summary>
            /// Handles the "Open options now" hyperlink by opening the options dialog.
            /// </summary>
            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_handled) return;
                _handled = true;

                if (Equals(actionItem.ActionContext, "open") && RenderDocOptionsCommand.Instance != null)
                    RenderDocOptionsCommand.Instance.ShowOptionsDialog();

                infoBarUIElement.Close();
            }

            /// <summary>
            /// Handles the ✕ close button — nothing to persist here because the<br/>
            /// "Shown" flag was already written when the banner appeared.
            /// </summary>
            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
            }
        }

        // ── State persistence ─────────────────────────────────────────────────────

        /// <summary>
        /// Checks whether the welcome banner has already been shown once.
        /// </summary>
        private static bool HasAlreadyShown(AsyncPackage package)
        {
            try
            {
                using (var key = package.UserRegistryRoot.OpenSubKey(RegistrySubKeyName))
                {
                    return key != null && Convert.ToInt32(key.GetValue("Shown", 0)) != 0;
                }
            }
            catch
            {
                return true;   // on any error, err on the side of not nagging
            }
        }

        /// <summary>
        /// Writes a single value into the welcome notice's registry sub-key.
        /// </summary>
        private static void WriteState(AsyncPackage package, string valueName, object value)
        {
            try
            {
                using (var key = package.UserRegistryRoot.CreateSubKey(RegistrySubKeyName))
                {
                    key.SetValue(valueName, value);
                }
            }
            catch { /* persistence failure is non-fatal */ }
        }

        /// <summary>
        /// Logs an exception to the VS activity log; never throws.
        /// </summary>
        private static void Log(Exception ex)
        {
            try { ActivityLog.LogError("RenderDocComments", ex.ToString()); } catch { }
        }
    }
}
