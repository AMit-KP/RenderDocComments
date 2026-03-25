using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RenderDocComments
{
    public partial class PurchaseActivationWindow : Window
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly DispatcherTimer _pendingKeyTimer;

        /// <summary>Raised when a key is successfully activated.</summary>
        public event EventHandler LicenseActivated;

        public PurchaseActivationWindow(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            InitializeComponent();

            // Pre-fill if a key was already stored (e.g. re-opening window)
            var stored = RenderDocOptions.Instance.LicenseKey;
            if (!string.IsNullOrEmpty(stored))
                KeyBox.Text = stored;

            // Poll for a pending license key written by the OS URI handler instance.
            // When the browser redirects to renderdoccomments://, the OS launches a new
            // VS instance which writes the key to a temp file and does nothing else.
            // We pick it up here within 1 second and fill the key box automatically.
            _pendingKeyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _pendingKeyTimer.Tick += OnPendingKeyTimerTick;
            _pendingKeyTimer.Start();
        }

        private void OnPendingKeyTimerTick(object sender, EventArgs e)
        {
            var key = LicenseManager.ReadAndClearPendingLicenseKey();
            if (string.IsNullOrEmpty(key)) return;

            KeyBox.Text = key;
            ShowStatus("Licence key received from checkout. Click Activate to complete.", isError: false);
            _pendingKeyTimer.Stop();
        }

        // ── Called by the package when the custom URI scheme fires ────────────────

        /// <summary>
        /// Parse the redirect URI returned by Dodo after a successful purchase and
        /// pre-fill the key box.
        /// Expected format: renderdoccomments://activate?license_key=XXXX-XXXX-...
        /// </summary>
        public void PreFillFromRedirectUri(string uri)
        {
            try
            {
                var idx = uri.IndexOf('?');
                if (idx < 0) return;

                var query = uri.Substring(idx + 1);
                var pairs = query.Split('&');
                foreach (var pair in pairs)
                {
                    var kv = pair.Split(new char[] { '=' }, 2);
                    if (kv.Length == 2 &&
                        string.Equals(kv[0], "license_key", StringComparison.OrdinalIgnoreCase))
                    {
                        var value = Uri.UnescapeDataString(kv[1]);
                        if (!string.IsNullOrEmpty(value) && !value.StartsWith("{"))
                        {
                            KeyBox.Text = value;
                            ShowStatus("Licence key received from checkout. Click Activate to complete.", isError: false);
                            _pendingKeyTimer.Stop();
                        }
                        return;
                    }
                }
            }
            catch { /* malformed URI — user can paste manually */ }
        }

        // ── Buy ───────────────────────────────────────────────────────────────────

        private void OnBuyNowClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                var (success, message) = LicenseManager.OpenCheckoutPage();
                if (!success)
                    ShowStatus($"Could not open browser: {message}", isError: true);
            }
            catch (Exception ex)
            {
                ShowStatus($"Could not open browser: {ex.Message}", isError: true);
            }
        }

        // ── Activate ──────────────────────────────────────────────────────────────

        private void OnActivateClicked(object sender, RoutedEventArgs e)
        {
            var key = KeyBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrEmpty(key))
            {
                ShowStatus("Please enter your licence key.", isError: true);
                return;
            }

            ActivateBtn.IsEnabled = false;
            ShowStatus("Contacting licence server…", isError: false);

            var (success, message) = LicenseManager.Activate(key);
            ActivateBtn.IsEnabled = true;

            if (success)
            {
                RenderDocOptions.Instance.Save(_serviceProvider);
                ShowStatus(message, isError: false);
                _pendingKeyTimer.Stop();
                LicenseActivated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ShowStatus(message, isError: true);
            }
        }

        // ── Close ─────────────────────────────────────────────────────────────────

        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            _pendingKeyTimer.Stop();
            Close();
        }

        // ── Helper ────────────────────────────────────────────────────────────────

        private void ShowStatus(string msg, bool isError)
        {
            StatusText.Text = msg;
            StatusText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            StatusText.Visibility = Visibility.Visible;
        }
    }
}