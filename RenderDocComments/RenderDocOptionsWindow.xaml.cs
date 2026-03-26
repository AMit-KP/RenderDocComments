using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;  // ColorDialog — needs System.Windows.Forms ref
using System.Windows.Media;

namespace RenderDocComments
{
    /// <summary>
    /// Code-behind for the RenderDocComments options window.
    /// Opened via Extensions > RenderDocOptions command.
    /// </summary>
    public partial class RenderDocOptionsWindow : Window
    {
        private readonly IServiceProvider _serviceProvider;
        private bool _loading = true;

        private int _colorCodeFg, _colorSummaryFg, _colorParamName,
                    _colorLink, _colorSectionLabel,
                    _colorGrad0, _colorGrad1, _colorGrad2;

        public RenderDocOptionsWindow(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
            InitializeComponent();
            Loaded += OnWindowLoaded;
        }

        // ── Initialisation ────────────────────────────────────────────────────────

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

        private void RefreshPremiumPanelEnabled()
        {
            bool Premium = LicenseManager.PremiumUnlocked;
            PremiumOptionsPanel.IsEnabled = Premium;
            PremiumOptionsPanel.Opacity = Premium ? 1.0 : 0.45;
        }

        // ── Licence actions ───────────────────────────────────────────────────────

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

        private void OnDeactivateClicked(object sender, RoutedEventArgs e)
        {
            LicenseManager.Deactivate();
            RenderDocOptions.Instance.Save(_serviceProvider);
            RefreshLicenceBadge();
            RefreshPremiumPanelEnabled();
            ShowLicenseMessage("Premium licence deactivated.", isError: false);
        }

        private void ShowLicenseMessage(string msg, bool isError)
        {
            LicenseMessageText.Text = msg;
            LicenseMessageText.Foreground = isError
                ? new SolidColorBrush(Color.FromRgb(0xF4, 0x87, 0x71))
                : new SolidColorBrush(Color.FromRgb(0x6A, 0x99, 0x55));
            LicenseMessageText.Visibility = Visibility.Visible;
        }

        // ── Generic setting change ────────────────────────────────────────────────

        private void OnSettingChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            ApplyToOptions();
        }

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

        private void OnToggleModeChanged(object sender, RoutedEventArgs e)
        {
            if (_loading) return;
            RenderDocOptions.Instance.GlyphToggleEnabled = GlyphModeRadio.IsChecked == true;
            OnSettingChanged(sender, e);
        }

        // ── Font ──────────────────────────────────────────────────────────────────

        private void OnFontFamilyChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading) return;
            UpdateFontPreview();
            OnSettingChanged(sender, null);
        }

        private void UpdateFontPreview()
        {
            var name = FontFamilyCombo.SelectedItem?.ToString() ?? "Segoe UI";
            try { FontPreviewText.FontFamily = new FontFamily(name); }
            catch { FontPreviewText.FontFamily = new FontFamily("Segoe UI"); }
        }

        // ── Swatches ──────────────────────────────────────────────────────────────

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

        private static void SetSwatchColor(System.Windows.Controls.Button btn, int argb)
        {
            var c = ArgbToWpf(argb);
            btn.Background = new SolidColorBrush(c);
        }

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

        private void OnSaveClicked(object sender, RoutedEventArgs e)
        {
            ApplyToOptions();
            RenderDocOptions.Instance.Save(_serviceProvider);
            SettingsChangedBroadcast.RaiseSettingsChanged();
            Close();
        }

        private void OnCloseClicked(object sender, RoutedEventArgs e) => Close();

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