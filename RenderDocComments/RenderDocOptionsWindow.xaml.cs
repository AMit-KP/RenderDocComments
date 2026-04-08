using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;  // ColorDialog — needs System.Windows.Forms ref
using System.Windows.Media;

namespace RenderDocComments
{
    /// <summary>
    /// Code-behind for the Render Doc Options window, opened via the<br/>
    /// Extensions &gt; Render Doc Options menu command.<br/>
    /// Manages settings UI binding, license management, and color picker interactions.
    /// </summary>
    /// <remarks>
    /// <para>This window provides the following functionality:</para>
    /// <list type="bullet">
    /// <item><description><b>Free settings:</b> Global render toggle.</description></item>
    /// <item><description><b>Premium settings:</b> Theme auto-refresh, glyph toggle mode, custom font, border sides, and custom colors (disabled when Premium is locked).</description></item>
    /// <item><description><b>License management:</b> Get Premium activation button and deactivation button.</description></item>
    /// <item><description><b>Color customization:</b> Interactive color swatches with Windows color dialog, gradient presets, and live preview.</description></item>
    /// <item><description><b>Reset:</b> Reset all settings to factory defaults.</description></item>
    /// </list>
    /// <para>Settings are applied to <see cref="RenderDocOptions.Instance"/> in real-time as the user<br/>
    /// interacts with the UI, but are only persisted to disk when the user clicks "Save".</para>
    /// </remarks>
    public partial class RenderDocOptionsWindow : Window
    {
        /// <summary>
        /// The service provider used to save settings to the Visual Studio settings store.
        /// </summary>
        private readonly IServiceProvider _serviceProvider;

        /// <summary>
        /// Flag indicating whether the window is currently loading initial values.<br/>
        /// Used to suppress <see cref="OnSettingChanged"/> during initialization.
        /// </summary>
        private bool _loading = true;

        /// <summary>Backing fields for the color swatch values (ARGB integers).</summary>
        private int _colorCodeFg, _colorSummaryFg, _colorParamName,
                    _colorLink, _colorSectionLabel,
                    _colorGrad0, _colorGrad1, _colorGrad2;

        /// <summary>
        /// Initializes a new instance of the <see cref="RenderDocOptionsWindow"/> class.
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to persist settings when the user saves.
        /// </param>
        public RenderDocOptionsWindow(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            InitializeComponent();
            Loaded += OnWindowLoaded;
        }

        // ── Initialisation ────────────────────────────────────────────────────────

        /// <summary>
        /// Handles the window's Loaded event, populating all UI controls with<br/>
        /// the current settings values and configuring the license status display.
        /// </summary>
        /// <param name="sender">
        /// The window that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The initialization sequence:</para>
        /// <list type="number">
        /// <item><description>Sets <see cref="_loading"/> to <c>true</c> to suppress setting change events.</description></item>
        /// <item><description>Populates the font family combo box with all available system fonts (sorted alphabetically).</description></item>
        /// <item><description>Calls <see cref="LoadFromOptions"/> to populate all controls from <see cref="RenderDocOptions.Instance"/>.</description></item>
        /// <item><description>Calls <see cref="RefreshLicenceBadge"/> to update the license status indicator.</description></item>
        /// <item><description>Calls <see cref="RefreshPremiumPanelEnabled"/> to disable/dim premium controls if not licensed.</description></item>
        /// <item><description>Resets <see cref="_loading"/> to <c>false</c> to enable user interactions.</description></item>
        /// </list>
        /// </remarks>
        private void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            _loading = true;

            FontFamilyCombo.Items.Clear();
            foreach (var ff in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
                FontFamilyCombo.Items.Add(ff.Source);

            LoadFromOptions();
            RefreshLicenceBadge();
            RefreshPremiumPanelEnabled();

            _loading = false;
        }

        /// <summary>
        /// Populates all UI controls with the current values from <see cref="RenderDocOptions.Instance"/>.
        /// </summary>
        /// <remarks>
        /// <para>This method synchronizes the UI with the stored settings without persisting<br/>
        /// any changes. It is called during window initialization and is not triggered<br/>
        /// by user interactions.</para>
        /// <para>Color values are loaded into local backing fields (<c>_color*</c>) and<br/>
        /// displayed via <see cref="RefreshAllSwatches"/>.</para>
        /// </remarks>
        private void LoadFromOptions()
        {
            var o = RenderDocOptions.Instance;

            RenderEnabledCheck.IsChecked = o.RenderEnabled;
            AutoRefreshCheck.IsChecked = o.AutoRefreshOnThemeChange;
            GlyphModeRadio.IsChecked = o.GlyphToggleEnabled;
            CaretModeRadio.IsChecked = !o.GlyphToggleEnabled;

            int idx = FontFamilyCombo.Items.IndexOf(o.CustomFontFamily);
            FontFamilyCombo.SelectedIndex = idx >= 0 ? idx : 0;
            UpdateFontPreview();

            BorderLeftCheck.IsChecked = o.BorderLeft;
            BorderTopCheck.IsChecked = o.BorderTop;
            BorderRightCheck.IsChecked = o.BorderRight;
            BorderBottomCheck.IsChecked = o.BorderBottom;

            _colorCodeFg = o.ColorCodeFg;
            _colorSummaryFg = o.ColorSummaryFg;
            _colorParamName = o.ColorParamName;
            _colorLink = o.ColorLink;
            _colorSectionLabel = o.ColorSectionLabel;
            _colorGrad0 = o.GradientStop0;
            _colorGrad1 = o.GradientStop1;
            _colorGrad2 = o.GradientStop2;

            RefreshAllSwatches();
        }

        /// <summary>
        /// Updates the license status badge (green "Activated" or grey "Free")<br/>
        /// and shows/hides the Get Premium and Deactivate buttons accordingly.
        /// </summary>
        /// <remarks>
        /// <para>The badge styling:</para>
        /// <list type="bullet">
        /// <item><description><b>Premium active:</b> Green background (<c>#3CB371</c>), white text "✔ Premium Activated".</description></item>
        /// <item><description><b>Free tier:</b> Grey background (<c>#808080</c>), white text "Free".</description></item>
        /// </list>
        /// <para>Button visibility:</para>
        /// <list type="bullet">
        /// <item><description><b>Get Premium:</b> Visible only when Premium is NOT activated.</description></item>
        /// <item><description><b>Deactivate:</b> Visible only when Premium IS activated.</description></item>
        /// </list>
        /// </remarks>
        private void RefreshLicenceBadge()
        {
            bool premium = LicenseManager.PremiumUnlocked;
            PremiumStatusBadge.Background = premium
                ? new SolidColorBrush(Color.FromRgb(0x3C, 0xB3, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80));
            PremiumStatusText.Text = premium ? "✔ Premium Activated" : "Free";
            PremiumStatusText.Foreground = Brushes.White;

            GetPremiumButton.Visibility = premium ? Visibility.Collapsed : Visibility.Visible;
            DeactivateButton.Visibility = premium ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>
        /// Enables or disables the premium options panel based on the current license status.<br/>
        /// When Premium is locked, the panel is disabled and dimmed (45% opacity).
        /// </summary>
        /// <remarks>
        /// <para>This method is called during window initialization and after any license<br/>
        /// state change (activation/deactivation) to ensure the UI reflects the current<br/>
        /// license status.</para>
        /// </remarks>
        private void RefreshPremiumPanelEnabled()
        {
            bool Premium = LicenseManager.PremiumUnlocked;
            PremiumOptionsPanel.IsEnabled = Premium;
            PremiumOptionsPanel.Opacity = Premium ? 1.0 : 0.45;
        }

        // ── Licence actions ───────────────────────────────────────────────────────

        /// <summary>
        /// Handles the "Get Premium" button click by opening the <see cref="PurchaseActivationWindow"/> dialog.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>When the license is successfully activated (via the <see cref="PurchaseActivationWindow.LicenseActivated"/> event):</para>
        /// <list type="number">
        /// <item><description>Refreshes the license badge to show "Activated".</description></item>
        /// <item><description>Enables the premium options panel.</description></item>
        /// <item><description>Displays a success message in green text.</description></item>
        /// </list>
        /// </remarks>
        private void OnGetPremiumClicked(object sender, RoutedEventArgs e)
        {
            var win = new PurchaseActivationWindow(_serviceProvider) { Owner = this };
            win.LicenseActivated += (s, args) =>
            {
                RefreshLicenceBadge();
                RefreshPremiumPanelEnabled();
                ShowLicenseMessage("Premium activated — Thank you ❤️ for your purchase!", isError: false);
            };
            win.ShowDialog();
        }

        /// <summary>
        /// Handles the "Deactivate" button click by calling <see cref="LicenseManager.Deactivate"/><br/>
        /// and updating the UI to reflect the deactivated state.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>After deactivation, the method:</para>
        /// <list type="number">
        /// <item><description>Saves the updated settings (which now have <see cref="RenderDocOptions.PremiumUnlocked"/> set to <c>false</c>).</description></item>
        /// <item><description>Refreshes the license badge to show "Free".</description></item>
        /// <item><description>Disables and dims the premium options panel.</description></item>
        /// <item><description>Displays a confirmation message in green text.</description></item>
        /// </list>
        /// </remarks>
        private void OnDeactivateClicked(object sender, RoutedEventArgs e)
        {
            LicenseManager.Deactivate();
            RenderDocOptions.Instance.Save(_serviceProvider);
            RefreshLicenceBadge();
            RefreshPremiumPanelEnabled();
            ShowLicenseMessage("Premium licence deactivated.", isError: false);
        }

        /// <summary>
        /// Displays a license-related status message in the message text block,<br/>
        /// colored green for success or red for errors.
        /// </summary>
        /// <param name="msg">
        /// The message text to display.
        /// </param>
        /// <param name="isError">
        /// <c>true</c> to display the message in red (error color <c>#F48771</c>);<br/>
        /// <c>false</c> to display it in green (success color <c>#6A9955</c>).
        /// </param>
        private void ShowLicenseMessage(string msg, bool isError)
        {
            LicenseMessageText.Text = msg;
            LicenseMessageText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            LicenseMessageText.Visibility = Visibility.Visible;
        }

        // ── Generic setting change ────────────────────────────────────────────────

        /// <summary>
        /// Handles any user interaction with a settings control, applying the current<br/>
        /// UI values to <see cref="RenderDocOptions.Instance"/> (without persisting to disk).
        /// </summary>
        /// <param name="sender">
        /// The control that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>This method is called in real-time as the user changes settings, ensuring<br/>
        /// the <see cref="RenderDocOptions.Instance"/> is always in sync with the UI.<br/>
        /// Actual persistence to the Visual Studio settings store happens only when the<br/>
        /// user clicks the "Save" button.</para>
        /// <para>The method returns immediately if <see cref="_loading"/> is <c>true</c> to prevent<br/>
        /// spurious saves during window initialization.</para>
        /// </remarks>
        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ApplyToOptions();
        }

        /// <summary>
        /// Copies all current UI control values to <see cref="RenderDocOptions.Instance"/>.
        /// </summary>
        /// <remarks>
        /// <para>This method maps each UI control to its corresponding option property:</para>
        /// <list type="bullet">
        /// <item><description><see cref="RenderEnabledCheck"/> → <see cref="RenderDocOptions.RenderEnabled"/>.</description></item>
        /// <item><description><see cref="AutoRefreshCheck"/> → <see cref="RenderDocOptions.AutoRefreshOnThemeChange"/>.</description></item>
        /// <item><description><see cref="GlyphModeRadio"/> → <see cref="RenderDocOptions.GlyphToggleEnabled"/>.</description></item>
        /// <item><description><see cref="FontFamilyCombo"/> → <see cref="RenderDocOptions.CustomFontFamily"/>.</description></item>
        /// <item><description>Border checkboxes → <see cref="RenderDocOptions.BorderLeft"/>, <see cref="RenderDocOptions.BorderTop"/>, etc.</description></item>
        /// <item><description>Color backing fields → <see cref="RenderDocOptions.ColorCodeFg"/>, etc.</description></item>
        /// </list>
        /// </remarks>
        private void ApplyToOptions()
        {
            var o = RenderDocOptions.Instance;
            o.RenderEnabled = RenderEnabledCheck.IsChecked == true;
            o.AutoRefreshOnThemeChange = AutoRefreshCheck.IsChecked == true;
            o.GlyphToggleEnabled = GlyphModeRadio.IsChecked == true;
            o.CustomFontFamily = FontFamilyCombo.SelectedItem?.ToString() ?? "Segoe UI";
            o.BorderLeft = BorderLeftCheck.IsChecked == true;
            o.BorderTop = BorderTopCheck.IsChecked == true;
            o.BorderRight = BorderRightCheck.IsChecked == true;
            o.BorderBottom = BorderBottomCheck.IsChecked == true;
            o.ColorCodeFg = _colorCodeFg;
            o.ColorSummaryFg = _colorSummaryFg;
            o.ColorParamName = _colorParamName;
            o.ColorLink = _colorLink;
            o.ColorSectionLabel = _colorSectionLabel;
            o.GradientStop0 = _colorGrad0;
            o.GradientStop1 = _colorGrad1;
            o.GradientStop2 = _colorGrad2;
        }

        /// <summary>
        /// Handles changes to the mode radio buttons (Glyph Mode vs. Caret Mode),<br/>
        /// updating <see cref="RenderDocOptions.GlyphToggleEnabled"/> and triggering a settings save.
        /// </summary>
        /// <param name="sender">
        /// The radio button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>Unlike <see cref="OnSettingChanged"/>, this handler directly sets the<br/>
        /// <see cref="RenderDocOptions.GlyphToggleEnabled"/> property before calling<br/>
        /// <see cref="OnSettingChanged"/> to ensure the change is applied immediately.</para>
        /// </remarks>
        private void OnToggleModeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            RenderDocOptions.Instance.GlyphToggleEnabled = GlyphModeRadio.IsChecked == true;
            OnSettingChanged(sender, e);
        }

        // ── Font ──────────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles font family combo selection changes, updating the preview text<br/>
        /// and triggering a settings save.
        /// </summary>
        /// <param name="sender">
        /// The combo box that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateFontPreview();
            OnSettingChanged(sender, null);
        }

        /// <summary>
        /// Updates the font family of the preview text block to match the selected<br/>
        /// font in the combo box, falling back to Segoe UI if the selected font is unavailable.
        /// </summary>
        /// <remarks>
        /// <para>The method attempts to create a <see cref="FontFamily"/> from the selected name.<br/>
        /// If the font family constructor throws (e.g., for a corrupted or missing font),<br/>
        /// the method falls back to "Segoe UI" to prevent a crash.</para>
        /// </remarks>
        private void UpdateFontPreview()
        {
            var name = FontFamilyCombo.SelectedItem?.ToString() ?? "Segoe UI";
            try { FontPreviewText.FontFamily = new FontFamily(name); }
            catch { FontPreviewText.FontFamily = new FontFamily("Segoe UI"); }
        }

        // ── Swatches ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Handles a color swatch button click, opening the Windows color dialog<br/>
        /// to let the user pick a new color for the corresponding setting.
        /// </summary>
        /// <param name="sender">
        /// The button that was clicked.
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>The method uses the button's <see cref="Button.Tag"/> property to identify<br/>
        /// which color to edit (e.g., "CodeFg", "SummaryFg", "Grad0", etc.).</para>
        /// <para>If the user picks a color and confirms the dialog, the method:</para>
        /// <list type="number">
        /// <item><description>Updates the corresponding backing field via <see cref="SetColorByTag"/>.</description></item>
        /// <item><description>Refreshes all swatches and the gradient preview.</description></item>
        /// <item><description>Triggers <see cref="OnSettingChanged"/> to apply the change.</description></item>
        /// </list>
        /// </remarks>
        private void OnSwatchClicked(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn)) return;
            var tag = btn.Tag?.ToString() ?? string.Empty;

            int current = GetColorByTag(tag);
            var picked = PickColor(current);
            if (!picked.HasValue) return;

            SetColorByTag(tag, picked.Value);
            RefreshAllSwatches();
            OnSettingChanged(sender, null);
        }

        /// <summary>
        /// Retrieves the current color value for a given tag from the local backing fields.
        /// </summary>
        /// <param name="tag">
        /// The color identifier tag (e.g., "CodeFg", "SummaryFg", "Grad0").
        /// </param>
        /// <returns>
        /// The ARGB integer value for the specified color, or <c>0xFFFFFFFF</c> (white) for unknown tags.
        /// </returns>
        private int GetColorByTag(string tag)
        {
            switch (tag)
            {
                case "CodeFg": return _colorCodeFg;
                case "SummaryFg": return _colorSummaryFg;
                case "ParamName": return _colorParamName;
                case "Link": return _colorLink;
                case "SectionLabel": return _colorSectionLabel;
                case "Grad0": return _colorGrad0;
                case "Grad1": return _colorGrad1;
                case "Grad2": return _colorGrad2;
                default: return unchecked((int)0xFFFFFFFF);
            }
        }

        /// <summary>
        /// Sets the local backing field for a given color tag to the specified ARGB value.
        /// </summary>
        /// <param name="tag">
        /// The color identifier tag (e.g., "CodeFg", "Grad0").
        /// </param>
        /// <param name="value">
        /// The new ARGB integer value to store.
        /// </param>
        private void SetColorByTag(string tag, int value)
        {
            switch (tag)
            {
                case "CodeFg": _colorCodeFg = value; break;
                case "SummaryFg": _colorSummaryFg = value; break;
                case "ParamName": _colorParamName = value; break;
                case "Link": _colorLink = value; break;
                case "SectionLabel": _colorSectionLabel = value; break;
                case "Grad0": _colorGrad0 = value; break;
                case "Grad1": _colorGrad1 = value; break;
                case "Grad2": _colorGrad2 = value; break;
            }
        }

        /// <summary>Opens the Windows colour dialog and returns the picked ARGB int, or null.</summary>
        /// <param name="currentArgb">
        /// The currently selected color, used as the initial color in the dialog.
        /// </param>
        /// <returns>
        /// The picked ARGB integer if the user confirms the dialog; <c>null</c> if the dialog is cancelled.
        /// </returns>
        private static int? PickColor(int currentArgb)
        {
            using (var dlg = new ColorDialog())
            {
                dlg.Color = System.Drawing.Color.FromArgb(currentArgb);
                dlg.FullOpen = true;
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return null;
                var c = dlg.Color;
                return (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
            }
        }

        /// <summary>
        /// Updates all color swatch buttons to reflect the current backing field values.
        /// </summary>
        /// <remarks>
        /// <para>After updating the swatches, the method calls <see cref="RefreshGradientPreview"/> to<br/>
        /// update the gradient preview bar with the current gradient stop colors.</para>
        /// </remarks>
        private void RefreshAllSwatches()
        {
            SetSwatchColor(SwatchCodeFg, _colorCodeFg);
            SetSwatchColor(SwatchSummaryFg, _colorSummaryFg);
            SetSwatchColor(SwatchParamName, _colorParamName);
            SetSwatchColor(SwatchLink, _colorLink);
            SetSwatchColor(SwatchSectionLabel, _colorSectionLabel);
            SetSwatchColor(SwatchGrad0, _colorGrad0);
            SetSwatchColor(SwatchGrad1, _colorGrad1);
            SetSwatchColor(SwatchGrad2, _colorGrad2);
            RefreshGradientPreview();
        }

        /// <summary>
        /// Opens the Visual Studio Marketplace review page in the default browser.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        private void RatingButton_Click(object sender, RoutedEventArgs e)
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://marketplace.visualstudio.com/items?itemName=AMit-KP.RenderDocComments&ssr=false#review-details",
                UseShellExecute = true
            });
        }

        /// <summary>
        /// Sets the background color of a swatch button to the specified ARGB value.
        /// </summary>
        /// <param name="btn">
        /// The button to color.
        /// </param>
        /// <param name="argb">
        /// The ARGB integer value.
        /// </param>
        private static void SetSwatchColor(System.Windows.Controls.Button btn, int argb)
        {
            var c = ArgbToWpf(argb);
            btn.Background = new SolidColorBrush(c);
        }

        /// <summary>
        /// Updates the gradient preview bar to reflect the current gradient stop colors.
        /// </summary>
        /// <remarks>
        /// <para>Creates a horizontal <see cref="LinearGradientBrush"/> with three stops:</para>
        /// <list type="bullet">
        /// <item><description>Stop 0: <see cref="_colorGrad0"/> at offset 0.0.</description></item>
        /// <item><description>Stop 1: <see cref="_colorGrad1"/> at offset 0.4.</description></item>
        /// <item><description>Stop 2: <see cref="_colorGrad2"/> at offset 1.0.</description></item>
        /// </list>
        /// </remarks>
        private void RefreshGradientPreview()
        {
            var brush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(ArgbToWpf(_colorGrad0), 0.0),
                    new GradientStop(ArgbToWpf(_colorGrad1), 0.4),
                    new GradientStop(ArgbToWpf(_colorGrad2), 1.0),
                },
                new Point(0, 0), new Point(1, 0));
            GradientPreviewBar.Background = brush;
        }

        // ── Gradient presets ──────────────────────────────────────────────────────

        /// <summary>
        /// Handles a gradient preset button click, applying a predefined set of<br/>
        /// gradient stop colors to the backing fields.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event. The button's <see cref="Button.Tag"/><br/>
        /// determines which preset to apply ("Purple", "Ocean", "Sunset", "Forest", "RoseGold").
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>Available presets:</para>
        /// <list type="bullet">
        /// <item><description><b>Purple:</b> Violet → Purple → Dark Purple (default).</description></item>
        /// <item><description><b>Ocean:</b> Blue → Dark Blue → Navy.</description></item>
        /// <item><description><b>Sunset:</b> Orange → Red → Dark Red.</description></item>
        /// <item><description><b>Forest:</b> Green → Dark Green → Very Dark Green.</description></item>
        /// <item><description><b>RoseGold:</b> Rose → Warm Brown → Dark Brown.</description></item>
        /// </list>
        /// <para>After applying the preset, the method refreshes all swatches and triggers a settings change.</para>
        /// </remarks>
        private void OnGradientPresetClicked(object sender, RoutedEventArgs e)
        {
            if (!(sender is System.Windows.Controls.Button btn)) return;
            switch (btn.Tag?.ToString())
            {
                case "Purple":
                    _colorGrad0 = unchecked((int)0xFFBE6EF0);
                    _colorGrad1 = unchecked((int)0xC8784BC8);
                    _colorGrad2 = unchecked((int)0x50502896);
                    break;
                case "Ocean":
                    _colorGrad0 = unchecked((int)0xFF1A8FD1);
                    _colorGrad1 = unchecked((int)0xC8106090);
                    _colorGrad2 = unchecked((int)0x50082050);
                    break;
                case "Sunset":
                    _colorGrad0 = unchecked((int)0xFFFF6B35);
                    _colorGrad1 = unchecked((int)0xC8C0392B);
                    _colorGrad2 = unchecked((int)0x50600015);
                    break;
                case "Forest":
                    _colorGrad0 = unchecked((int)0xFF2ECC71);
                    _colorGrad1 = unchecked((int)0xC8229955);
                    _colorGrad2 = unchecked((int)0x50104020);
                    break;
                case "RoseGold":
                    _colorGrad0 = unchecked((int)0xFFE8A598);
                    _colorGrad1 = unchecked((int)0xC8C07060);
                    _colorGrad2 = unchecked((int)0x50703020);
                    break;
            }
            RefreshAllSwatches();
            OnSettingChanged(sender, null);
        }

        // ── Footer buttons ────────────────────────────────────────────────────────

        /// <summary>
        /// Handles the "Save" button click, persisting all current settings to disk<br/>
        /// and broadcasting the change to all subscribers before closing the window.
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
        /// <item><description>Calls <see cref="ApplyToOptions"/> to sync the UI to <see cref="RenderDocOptions.Instance"/>.</description></item>
        /// <item><description>Calls <see cref="RenderDocOptions.Save"/> to persist settings to the Visual Studio settings store.</description></item>
        /// <item><description>Raises <see cref="SettingsChangedBroadcast.RaiseSettingsChanged"/> to notify all taggers to rebuild.</description></item>
        /// <item><description>Closes the window.</description></item>
        /// </list>
        /// </remarks>
        private void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            ApplyToOptions();
            RenderDocOptions.Instance.Save(_serviceProvider);
            SettingsChangedBroadcast.RaiseSettingsChanged();
            Close();
        }

        /// <summary>
        /// Handles the "Close" button click, closing the window without saving changes.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>Note: Changes are already applied to <see cref="RenderDocOptions.Instance"/> in real-time<br/>
        /// via <see cref="OnSettingChanged"/> as the user interacts with controls. Only the<br/>
        /// persistence to disk (via <see cref="RenderDocOptions.Save"/>) is deferred until Save is clicked.<br/>
        /// Therefore, closing without saving means the in-memory options revert to the<br/>
        /// last saved state on next Visual Studio restart.</para>
        /// </remarks>
        private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// Handles the "Reset" button click, resetting all settings to their default values<br/>
        /// after confirming with the user via a confirmation dialog.
        /// </summary>
        /// <param name="sender">
        /// The button that raised the event (unused).
        /// </param>
        /// <param name="e">
        /// The event arguments (unused).
        /// </param>
        /// <remarks>
        /// <para>If the user confirms, the method:</para>
        /// <list type="number">
        /// <item><description>Creates a fresh <see cref="RenderDocOptions"/> instance via reflection (to get default constructor values).</description></item>
        /// <item><description>Copies all default values to the singleton <see cref="RenderDocOptions.Instance"/>.</description></item>
        /// <item><description>Reloads the UI via <see cref="LoadFromOptions"/>.</description></item>
        /// </list>
        /// <para>Note: The reset does NOT persist to disk — the user must click "Save" to make<br/>
        /// the defaults permanent. This allows the user to preview the defaults before committing.</para>
        /// </remarks>
        private void OnResetClicked(object sender, RoutedEventArgs e)
        {
            var result = System.Windows.MessageBox.Show(
                "Reset all settings to their defaults?",
                "Reset Settings", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;

            var fresh = typeof(RenderDocOptions)
                .GetConstructor(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance,
                                null, Type.EmptyTypes, null)
                ?.Invoke(null);

            var o = RenderDocOptions.Instance;
            o.RenderEnabled = true;
            o.AutoRefreshOnThemeChange = false;
            o.GlyphToggleEnabled = false;
            o.CustomFontFamily = "Segoe UI";
            o.BorderLeft = true; o.BorderTop = false; o.BorderRight = false; o.BorderBottom = false;
            o.ColorCodeFg = unchecked((int)0xFFCE9178);
            o.ColorSummaryFg = unchecked((int)0xFFD4D4D4);
            o.ColorParamName = unchecked((int)0xFF9CDCFE);
            o.ColorLink = unchecked((int)0xFF569CD6);
            o.ColorSectionLabel = unchecked((int)0xFF969696);
            o.GradientStop0 = unchecked((int)0xFFBE6EF0);
            o.GradientStop1 = unchecked((int)0xC8784BC8);
            o.GradientStop2 = unchecked((int)0x50502896);

            _loading = true;
            LoadFromOptions();
            _loading = false;
        }

        // ── Helper ────────────────────────────────────────────────────────────────

        /// <summary>
        /// Converts an ARGB integer to a WPF <see cref="Color"/> structure.
        /// </summary>
        /// <param name="argb">
        /// The ARGB integer with bytes in order (alpha, red, green, blue).
        /// </param>
        /// <returns>
        /// A WPF <see cref="Color"/> with the same channel values.
        /// </returns>
        private static Color ArgbToWpf(int argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }
    }
}