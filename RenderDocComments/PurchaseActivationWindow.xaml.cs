using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace RenderDocComments
{
    /// <summary>
    /// Code-behind for the Purchase Activation window, providing license purchase<br/>
    /// and activation functionality via the Dodo Payments platform.
    /// </summary>
    /// <remarks>
    /// <para>This window handles two activation flows:</para>
    /// <list type="bullet">
    /// <item>
    /// <description><b>Purchase flow:</b> User clicks "Buy Premium" → opens checkout in browser → local HTTP listener (<see cref="LicenseHttpListener"/>) receives the key → user clicks "Activate" to complete.</description>
    /// </item>
    /// <item>
    /// <description><b>Key-only flow:</b> User pastes an existing key into the text box → clicks "Activate" → key is validated and Premium is unlocked.</description>
    /// </item>
    /// </list>
    /// <para>The window raises the <see cref="LicenseActivated"/> event upon successful activation,<br/>
    /// allowing the parent window (<see cref="RenderDocOptionsWindow"/>) to update its UI.</para>
    /// </remarks>
    public partial class PurchaseActivationWindow : Window
    {
        /// <summary>
        /// The service provider used to save settings after successful activation.
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>Raised when a key is successfully activated.</summary>
        /// <remarks>
        /// <para>This event is raised after <see cref="LicenseManager.Activate"/> succeeds and<br/>
        /// the settings have been saved. Subscribers should update their UI to reflect<br/>
        /// the new Premium status (e.g., refresh the license badge, enable premium controls).</para>
        /// </remarks>
        public event EventHandler LicenseActivated;

        /// <summary>
        /// Initializes a new instance of the <see cref="PurchaseActivationWindow"/> class,<br/>
        /// pre-filling the key text box if a stored key exists.
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to persist settings after activation.
        /// </param>
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

        /// <summary>
        /// Handles receipt of a license key from the checkout callback via <see cref="LicenseHttpListener"/>.
        /// </summary>
        /// <param name="key">
        /// The license key received from the payment processor.
        /// </param>
        /// <remarks>
        /// <para>This method is called on a background thread by the HTTP listener.<br/>
        /// It marshals back to the UI thread via <see cref="Dispatcher.InvokeAsync"/> to:</para>
        /// <list type="number">
        /// <item><description>Populate the key text box with the received key.</description></item>
        /// <item><description>Display a status message instructing the user to click "Activate".</description></item>
        /// </list>
        /// <para>The key is NOT automatically activated — the user must still click the<br/>
        /// "Activate" button to complete the process, ensuring explicit consent.</para>
        /// </remarks>
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

        /// <summary>
        /// Handles the "Buy Premium" button click by starting the HTTP listener,<br/>
        /// opening the checkout page in the browser, and updating the button state.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The method performs the following steps:</para>
        /// <list type="number">
        /// <item><description>Starts <see cref="LicenseHttpListener"/> to listen for the checkout callback.</description></item>
        /// <item><description>Subscribes to <see cref="LicenseHttpListener.ListenerStopped"/> to reset the button state.</description></item>
        /// <item><description>Disables the button and changes its text to "Waiting for payment…".</description></item>
        /// <item><description>Calls <see cref="LicenseManager.OpenCheckoutPage"/> to open the payment page.</description></item>
        /// <item><description>If checkout fails, stops the listener and displays an error message.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Handles the <see cref="LicenseHttpListener.ListenerStopped"/> event, resetting<br/>
        /// the "Buy Premium" button to its enabled, default state.
        /// </summary>
        /// <remarks>
        /// <para>This method is called on a background thread by the HTTP listener.<br/>
        /// It marshals back to the UI thread via <see cref="Dispatcher.InvokeAsync"/> to:</para>
        /// <list type="number">
        /// <item><description>Re-enable the button.</description></item>
        /// <item><description>Restore the button's text to "Buy Premium — Open Checkout ↗".</description></item>
        /// <item><description>Unsubscribe from the <see cref="LicenseHttpListener.ListenerStopped"/> event.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Handles the "Activate" button click, validating the entered license key<br/>
        /// with the Dodo Payments API and unlocking Premium on success.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The method performs the following steps:</para>
        /// <list type="number">
        /// <item><description>Validates that the key text box is not empty.</description></item>
        /// <item><description>Disables the button and shows "Contacting licence server…".</description></item>
        /// <item><description>Calls <see cref="LicenseManager.Activate"/> with the trimmed key text.</description></item>
        /// <item><description>On success:
        ///   <list type="bullet">
        ///   <item><description>Saves settings via <see cref="RenderDocOptions.Save"/>.</description></item>
        ///   <item><description>Displays the success message.</description></item>
        ///   <item><description>Stops the HTTP listener (if running).</description></item>
        ///   <item><description>Changes the button to "Activated ✓" and disables it.</description></item>
        ///   <item><description>Raises the <see cref="LicenseActivated"/> event.</description></item>
        ///   </list>
        /// </description></item>
        /// <item><description>On failure: displays the error message in red.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Handles the "Close" button click, cleaning up event subscriptions and closing the window.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The method unsubscribes from <see cref="LicenseHttpListener.LicenseKeyReceived"/><br/>
        /// to prevent memory leaks, then calls the base <see cref="Window.OnClosed"/> and closes the window.</para>
        /// </remarks>
        private void OnCloseClicked(object sender, RoutedEventArgs e)
        {
            LicenseHttpListener.LicenseKeyReceived -= OnLicenseKeyReceived;
            base.OnClosed(e);
            Close();
        }

        // ── Helper ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Displays a status message in the status text block, colored green for success<br/>
        /// or red for errors.
        /// </summary>
        /// <param name="msg">
        /// The message text to display.
        /// </param>
        /// <param name="isError">
        /// <c>true</c> to display in red (error color <c>#F48771</c>);<br/>
        /// <c>false</c> to display in green (success color <c>#6A9955</c>).
        /// </param>
        private void ShowStatus(string msg, bool isError)
        {
            StatusText.Text = msg;
            StatusText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            StatusText.Visibility = Visibility.Visible;
        }

        /// <summary>
        /// Handles the "Copy" button click, copying the license key text to the clipboard<br/>
        /// and showing a temporary checkmark confirmation.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>If the key text box is empty, the method returns immediately without action.</para>
        /// <para>On successful copy:</para>
        /// <list type="number">
        /// <item><description>Disables the button temporarily.</description></item>
        /// <item><description>Replaces the button content with a checkmark (✓).</description></item>
        /// <item><description>Starts a 2-second <see cref="DispatcherTimer"/>.</description></item>
        /// <item><description>After 2 seconds, restores the copy icon and re-enables the button.</description></item>
        /// </list>
        /// </remarks>
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

        /// <summary>
        /// Builds the copy icon visual — two overlapping rounded rectangles<br/>
        /// representing the clipboard copy metaphor.
        /// </summary>
        /// <returns>
        /// A <see cref="Canvas"/> containing the two-rectangle copy icon.
        /// </returns>
        /// <remarks>
        /// <para>The icon consists of:</para>
        /// <list type="bullet">
        /// <item><description><b>Back rectangle:</b> 10×10, positioned at (0, 0) — represents the clipboard backing.</description></item>
        /// <item><description><b>Front rectangle:</b> 10×10, positioned at (3, 3) — represents the copied page.</description></item>
        /// </list>
        /// <para>Both rectangles use <see cref="SystemColors.GrayTextBrush"/> for stroke,<br/>
        /// 1.5px stroke thickness, transparent fill, and 1.5px corner radius.</para>
        /// </remarks>
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