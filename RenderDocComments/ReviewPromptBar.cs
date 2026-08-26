using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace RenderDocComments
{
    /// <summary>
    /// Shows a Visual Studio InfoBar (the thin banner docked directly below the<br/>
    /// main toolbar) asking the user to leave a Marketplace rating/review.<br/>
    /// Same pattern used by popular extensions such as EditorConfig.
    /// </summary>
    /// <remarks>
    /// <para>Scheduling rules:</para>
    /// <list type="bullet">
    /// <item><description>A first-run timestamp is recorded on the very first launch.</description></item>
    /// <item><description>The banner appears once <see cref="GracePeriodDays"/> days have elapsed since that timestamp.</description></item>
    /// <item><description><b>Leave a review</b> opens the Marketplace review page and never asks again.</description></item>
    /// <item><description><b>No thanks</b> and the banner's ✕ button both snooze the prompt for <see cref="SnoozeDays"/> days.</description></item>
    /// </list>
    /// <para>All state persists under the package's <see cref="AsyncPackage.UserRegistryRoot"/><br/>
    /// so it survives VS restarts and extension updates.</para>
    /// </remarks>
    internal static class ReviewPromptBar
    {
        private const string RegistrySubKeyName = "ReviewPrompt";
        private const int GracePeriodDays = 3;
        private const int SnoozeDays = 7;
        private const string ReviewUrl =
            "https://marketplace.visualstudio.com/items?itemName=AMit-KP.RenderDocComments&ssr=false#review-details";

        // ── Entry point ───────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluates the scheduling rules and, when due, displays the review banner<br/>
        /// in the Visual Studio main window. Designed to be called fire-and-forget<br/>
        /// from package initialization; every failure path is swallowed so the nag<br/>
        /// bar can never break IDE startup.
        /// </summary>
        /// <param name="package">
        /// The hosting <see cref="RenderDocCommentsPackage"/>.
        /// </param>
        public static async Task TryShowAsync(RenderDocCommentsPackage package)
        {
            try
            {
                var state = ReadState(package);
                DateTime nowUtc = DateTime.UtcNow;

                if (!state.FirstRunUtc.HasValue)
                {
                    WriteState(package, "FirstRunUtcTicks", nowUtc.Ticks);
                    return;
                }
                if ((nowUtc - state.FirstRunUtc.Value).TotalDays < GracePeriodDays) return;
                if (state.PermanentlyDismissed) return;
                if (state.SnoozeUntilUtc.HasValue && nowUtc < state.SnoozeUntilUtc.Value) return;

                // Give the IDE time to finish starting up before showing anything.
                await Task.Delay(TimeSpan.FromSeconds(15), package.DisposalToken);

                await package.JoinableTaskFactory.SwitchToMainThreadAsync(package.DisposalToken);
                await ShowAsync(package);
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
                    new InfoBarTextSpan("Enjoying Render Doc Comments? "),
                    new InfoBarTextSpan("A quick ★★★★★ rating helps other developers find the extension."),
                },
                new[]
                {
                    new InfoBarHyperlink("Leave a review", "review"),
                    new InfoBarHyperlink("No thanks", "dismiss"),
                },
                KnownMonikers.StatusInformation,
                isCloseButtonVisible: true);

            IVsInfoBarUIElement bar = factory.CreateInfoBar(model);
            bar.Advise(new ReviewInfoBarEvents(package), out _);

            if (!(await package.GetServiceAsync(typeof(SVsShell)) is IVsShell shell)) return;
            if (ErrorHandler.Failed(shell.GetProperty((int)__VSSPROPID7.VSSPROPID_MainWindowInfoBarHost, out object hostObj))) return;
            if (hostObj is IVsInfoBarHost host)
                host.AddInfoBar(bar);
        }

        /// <summary>
        /// Opens the Visual Studio Marketplace review page in the default browser.
        /// </summary>
        private static void OpenReviewPage()
        {
            try
            {
                Process.Start(new ProcessStartInfo { FileName = ReviewUrl, UseShellExecute = true });
            }
            catch { /* browser launch failure is non-fatal */ }
        }

        /// <summary>
        /// Handles clicks on the banner's hyperlinks and ✕ button, persisting the<br/>
        /// corresponding dismissal choice before closing the banner.
        /// </summary>
        private sealed class ReviewInfoBarEvents : IVsInfoBarUIEvents
        {
            private readonly RenderDocCommentsPackage _package;
            private bool _handled;

            /// <summary>
            /// Initializes the event sink.
            /// </summary>
            /// <param name="package">The hosting package, used for state writes.</param>
            public ReviewInfoBarEvents(RenderDocCommentsPackage package)
            {
                _package = package;
            }

            /// <summary>
            /// Handles hyperlink clicks: "Leave a review" opens the Marketplace page<br/>
            /// and permanently dismisses; "No thanks" snoozes the prompt.
            /// </summary>
            public void OnActionItemClicked(IVsInfoBarUIElement infoBarUIElement, IVsInfoBarActionItem actionItem)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_handled) return;
                _handled = true;

                if (Equals(actionItem.ActionContext, "review"))
                {
                    OpenReviewPage();
                    PermanentlyDismiss(_package);
                }
                else
                {
                    Snooze(_package);
                }

                infoBarUIElement.Close();
            }

            /// <summary>
            /// Handles the ✕ close button — treated the same as "No thanks".
            /// </summary>
            public void OnClosed(IVsInfoBarUIElement infoBarUIElement)
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                if (_handled) return;
                _handled = true;

                Snooze(_package);
            }
        }

        // ── State persistence ─────────────────────────────────────────────────────

        /// <summary>Snapshot of the persisted prompt scheduling state.</summary>
        private sealed class State
        {
            /// <summary>UTC timestamp of the first-ever launch, if recorded.</summary>
            public DateTime? FirstRunUtc;

            /// <summary>UTC timestamp until which the prompt is snoozed, if any.</summary>
            public DateTime? SnoozeUntilUtc;

            /// <summary><c>true</c> when the user chose to never be asked again.</summary>
            public bool PermanentlyDismissed;
        }

        /// <summary>
        /// Reads the prompt state from the package's user registry root,<br/>
        /// returning defaults when no state exists yet.
        /// </summary>
        private static State ReadState(AsyncPackage package)
        {
            var result = new State();
            try
            {
                using (var key = package.UserRegistryRoot.OpenSubKey(RegistrySubKeyName))
                {
                    if (key == null) return result;

                    long firstRun = Convert.ToInt64(key.GetValue("FirstRunUtcTicks", 0L));
                    if (firstRun > 0) result.FirstRunUtc = new DateTime(firstRun, DateTimeKind.Utc);

                    long snooze = Convert.ToInt64(key.GetValue("SnoozeUntilUtcTicks", 0L));
                    if (snooze > 0) result.SnoozeUntilUtc = new DateTime(snooze, DateTimeKind.Utc);

                    result.PermanentlyDismissed = Convert.ToInt32(key.GetValue("PermanentlyDismissed", 0)) != 0;
                }
            }
            catch { /* fall back to defaults */ }
            return result;
        }

        /// <summary>
        /// Writes a single value into the prompt's registry sub-key.
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
        /// Snoozes the prompt for another <see cref="SnoozeDays"/> days.
        /// </summary>
        private static void Snooze(AsyncPackage package) =>
            WriteState(package, "SnoozeUntilUtcTicks", DateTime.UtcNow.AddDays(SnoozeDays).Ticks);

        /// <summary>
        /// Permanently suppresses the prompt (user clicked "Leave a review").
        /// </summary>
        private static void PermanentlyDismiss(AsyncPackage package) =>
            WriteState(package, "PermanentlyDismissed", 1);

        /// <summary>
        /// Logs an exception to the VS activity log; never throws.
        /// </summary>
        private static void Log(Exception ex)
        {
            try { ActivityLog.LogError("RenderDocComments", ex.ToString()); } catch { }
        }
    }
}
