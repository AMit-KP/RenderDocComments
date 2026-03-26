using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RenderDocComments
{
    public partial class PurchaseActivationWindow : Window
    {
        private readonly IServiceProvider _serviceProvider;

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

            LicenseHttpListener.LicenseKeyReceived += OnLicenseKeyReceived;
        }

        private void OnLicenseKeyReceived(string key)
        {
            // Jump back to the UI thread
            Dispatcher.InvokeAsync(() =>
            {
                KeyBox.Text = key;
                ShowStatus("Licence key received from checkout. Click Activate to complete.", isError: false);
            });
        }

        // ── Buy ───────────────────────────────────────────────────────────────────

        private void OnBuyNowClicked(object sender, RoutedEventArgs e)
        {
            LicenseHttpListener.Start();
            LicenseHttpListener.ListenerStopped += OnListenerStopped;

            BuyNowButton.IsEnabled = false;
            BuyNowButton.Content = "Waiting for payment…";

            var (success, message) = LicenseManager.OpenCheckoutPage();
            if (!success)
            {
                LicenseHttpListener.Stop();
                ShowStatus($"Could not open browser: {message}", isError: true);
            }
        }

        private void OnListenerStopped()
        {
            Dispatcher.InvokeAsync(() =>
            {
                BuyNowButton.IsEnabled = true;
                BuyNowButton.Content = "Buy Premium — Open Checkout ↗";
                LicenseHttpListener.ListenerStopped -= OnListenerStopped;
            });
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
                LicenseHttpListener.Stop();

                ActivateBtn.IsEnabled = false;
                ActivateBtn.Content = "Activated ✓";

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
            LicenseHttpListener.LicenseKeyReceived -= OnLicenseKeyReceived;
            base.OnClosed(e);
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
        private void OnCopyClicked(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(KeyBox.Text)) return;

            Clipboard.SetText(KeyBox.Text);

            CopyBtn.IsEnabled = false;

            var check = new TextBlock
            {
                Text = "✓",
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            CopyBtn.Content = check;

            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            timer.Tick += (s, _) =>
            {
                CopyBtn.Content = BuildCopyIcon();
                CopyBtn.IsEnabled = true;
                timer.Stop();
            };
            timer.Start();
        }

        private static Canvas BuildCopyIcon()
        {
            var canvas = new Canvas { Width = 16, Height = 16 };

            var back = new Rectangle
            {
                Width = 10,
                Height = 10,
                Stroke = SystemColors.GrayTextBrush,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetTop(back, 0);

            var front = new Rectangle
            {
                Width = 10,
                Height = 10,
                Stroke = SystemColors.GrayTextBrush,
                StrokeThickness = 1.5,
                Fill = Brushes.Transparent,
                RadiusX = 1.5,
                RadiusY = 1.5
            };
            Canvas.SetLeft(front, 3);
            Canvas.SetTop(front, 3);

            canvas.Children.Add(back);
            canvas.Children.Add(front);
            return canvas;
        }
    }
}