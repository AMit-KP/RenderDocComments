using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Settings;
using System;
using System.Windows.Media;

namespace RenderDocComments
{
    /// <summary>
    /// Persisted settings for the RenderDocComments extension.
    /// All Premium-gated options are read by the rest of the extension through this class.
    /// Call <see cref="Load"/> once at package init; call <see cref="Save"/> after any change.
    /// </summary>
    public sealed class RenderDocOptions
    {
        // ── Singleton ─────────────────────────────────────────────────────────────
        public static readonly RenderDocOptions Instance = new RenderDocOptions();
        private RenderDocOptions()
        {
        }

        private const string CollectionPath = "RenderDocComments";

        // ── FREE settings (always active) ─────────────────────────────────────────

        /// <summary>Master switch — renders doc comments as adornments.</summary>
        public bool RenderEnabled { get; set; } = true;

        // ── Premium settings (disabled / ignored when PremiumUnlocked == false) ───────────

        /// <summary>
        /// When true the extension has a valid Premium licence and Premium features are active.
        /// Set this via <see cref="LicenseManager.Activate"/> — do not set directly.
        /// </summary>
        public bool PremiumUnlocked { get; private set; } = false;

        // ── Licence key storage (persisted, not used for gating) ─────────────────

        /// <summary>The raw licence key entered by the user.</summary>
        public string LicenseKey { get; set; } = null;

        /// <summary>The Dodo Payments instance ID returned at activation (used for deactivation and precise validation).</summary>
        public string LicenseInstanceId { get; set; } = null;

        // -- Theme auto-refresh (Premium 1) --
        /// <summary>
        /// Auto-refresh adornment colours when the VS colour theme changes,
        /// without requiring the user to reopen the file.
        /// Requires Premium.  Default free behaviour: user must reopen the file.
        /// </summary>
        public bool AutoRefreshOnThemeChange { get; set; } = false;

        // -- Glyph toggle button (Premium 2) --
        /// <summary>
        /// Show a margin glyph button that lets the user toggle the render on/off
        /// per-comment with a single click.
        /// Requires Premium.  Default free behaviour: caret-in/out hides the adornment.
        /// </summary>
        public bool GlyphToggleEnabled { get; set; } = false;

        // -- Font (Premium 3) --
        /// <summary>Font family name for the rendered view. Requires Premium.</summary>
        public string CustomFontFamily { get; set; } = "Segoe UI";

        // -- Border sides (Premium 4) --
        /// <summary>Show accent bar on the left side (default). Requires Premium.</summary>
        public bool BorderLeft { get; set; } = true;
        /// <summary>Show accent bar on the top side. Requires Premium.</summary>
        public bool BorderTop { get; set; } = false;
        /// <summary>Show accent bar on the right side. Requires Premium.</summary>
        public bool BorderRight { get; set; } = false;
        /// <summary>Show accent bar on the bottom side. Requires Premium.</summary>
        public bool BorderBottom { get; set; } = false;

        // -- Colours (Premium 5) --
        // Stored as ARGB int for easy serialisation.
        public int ColorCodeFg { get; set; } = unchecked((int)0xFFCE9178); // orange-ish
        public int ColorSummaryFg { get; set; } = unchecked((int)0xFFD4D4D4); // light grey
        public int ColorParamName { get; set; } = unchecked((int)0xFF9CDCFE); // light blue
        public int ColorLink { get; set; } = unchecked((int)0xFF569CD6); // VS blue
        public int ColorSectionLabel { get; set; } = unchecked((int)0xFF969696); // dim grey

        // Gradient bar: three stop colours (ARGB).
        public int GradientStop0 { get; set; } = unchecked((int)0xFFBE6EF0); // violet
        public int GradientStop1 { get; set; } = unchecked((int)0xC8784BC8); // purple
        public int GradientStop2 { get; set; } = unchecked((int)0x50502896); // dark purple

        // ── Helper: effective values (falls back to defaults when Premium is locked) ──

        // NOTE: All Effective* properties read LicenseManager.PremiumUnlocked — the single
        // source of truth for the licence state. RenderDocOptions.PremiumUnlocked is kept
        // only for persistence; the gate logic lives entirely in LicenseManager.
        private static bool Premium => LicenseManager.PremiumUnlocked;

        public bool EffectiveAutoRefresh => Premium && AutoRefreshOnThemeChange;
        public bool EffectiveGlyphToggle => Premium && GlyphToggleEnabled;
        public string EffectiveFontFamily => Premium ? CustomFontFamily : "Segoe UI";
        public bool EffectiveBorderLeft => !Premium || BorderLeft;
        public bool EffectiveBorderTop => Premium && BorderTop;
        public bool EffectiveBorderRight => Premium && BorderRight;
        public bool EffectiveBorderBottom => Premium && BorderBottom;

        public Color EffectiveColorCodeFg => Premium ? ToColor(ColorCodeFg) : Color.FromRgb(0xCE, 0x91, 0x78);
        public Color EffectiveColorSummaryFg => Premium ? ToColor(ColorSummaryFg) : Color.FromRgb(0xD4, 0xD4, 0xD4);
        public Color EffectiveColorParamName => Premium ? ToColor(ColorParamName) : Color.FromRgb(0x9C, 0xDC, 0xFE);
        public Color EffectiveColorLink => Premium ? ToColor(ColorLink) : Color.FromRgb(0x56, 0x9C, 0xD6);
        public Color EffectiveColorSectionLabel => Premium ? ToColor(ColorSectionLabel) : Color.FromRgb(0x96, 0x96, 0x96);
        public Color EffectiveGradientStop0 => Premium ? ToColor(GradientStop0) : Color.FromArgb(0xFF, 0xBE, 0x6E, 0xF0);
        public Color EffectiveGradientStop1 => Premium ? ToColor(GradientStop1) : Color.FromArgb(0xC8, 0x78, 0x4B, 0xC8);
        public Color EffectiveGradientStop2 => Premium ? ToColor(GradientStop2) : Color.FromArgb(0x50, 0x50, 0x28, 0x96);

        // ── Persistence ───────────────────────────────────────────────────────────

        public void Load(IServiceProvider serviceProvider)
        {
            try
            {
                var sm = new ShellSettingsManager(serviceProvider);
                var store = sm.GetReadOnlySettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(CollectionPath)) return;

                // Licence fields
                PremiumUnlocked = ReadBool(store, nameof(PremiumUnlocked), PremiumUnlocked);
                LicenseKey = store.PropertyExists(CollectionPath, nameof(LicenseKey))
                                ? store.GetString(CollectionPath, nameof(LicenseKey)) : LicenseKey;
                LicenseInstanceId = store.PropertyExists(CollectionPath, nameof(LicenseInstanceId))
                                ? store.GetString(CollectionPath, nameof(LicenseInstanceId)) : LicenseInstanceId;

                RenderEnabled = ReadBool(store, nameof(RenderEnabled), RenderEnabled);
                AutoRefreshOnThemeChange = ReadBool(store, nameof(AutoRefreshOnThemeChange), AutoRefreshOnThemeChange);
                GlyphToggleEnabled = ReadBool(store, nameof(GlyphToggleEnabled), GlyphToggleEnabled);
                CustomFontFamily = store.PropertyExists(CollectionPath, nameof(CustomFontFamily))
                                            ? store.GetString(CollectionPath, nameof(CustomFontFamily))
                                            : CustomFontFamily;
                BorderLeft = ReadBool(store, nameof(BorderLeft), BorderLeft);
                BorderTop = ReadBool(store, nameof(BorderTop), BorderTop);
                BorderRight = ReadBool(store, nameof(BorderRight), BorderRight);
                BorderBottom = ReadBool(store, nameof(BorderBottom), BorderBottom);

                ColorCodeFg = ReadInt(store, nameof(ColorCodeFg), ColorCodeFg);
                ColorSummaryFg = ReadInt(store, nameof(ColorSummaryFg), ColorSummaryFg);
                ColorParamName = ReadInt(store, nameof(ColorParamName), ColorParamName);
                ColorLink = ReadInt(store, nameof(ColorLink), ColorLink);
                ColorSectionLabel = ReadInt(store, nameof(ColorSectionLabel), ColorSectionLabel);
                GradientStop0 = ReadInt(store, nameof(GradientStop0), GradientStop0);
                GradientStop1 = ReadInt(store, nameof(GradientStop1), GradientStop1);
                GradientStop2 = ReadInt(store, nameof(GradientStop2), GradientStop2);
            }
            catch { /* non-critical */ }
        }

        public void Save(IServiceProvider serviceProvider)
        {
            try
            {
                var sm = new ShellSettingsManager(serviceProvider);
                var store = sm.GetWritableSettingsStore(SettingsScope.UserSettings);
                if (!store.CollectionExists(CollectionPath))
                    store.CreateCollection(CollectionPath);

                // Licence fields
                store.SetBoolean(CollectionPath, nameof(PremiumUnlocked), PremiumUnlocked);
                store.SetString(CollectionPath, nameof(LicenseKey), LicenseKey ?? string.Empty);
                store.SetString(CollectionPath, nameof(LicenseInstanceId), LicenseInstanceId ?? string.Empty);

                store.SetBoolean(CollectionPath, nameof(RenderEnabled), RenderEnabled);
                store.SetBoolean(CollectionPath, nameof(AutoRefreshOnThemeChange), AutoRefreshOnThemeChange);
                store.SetBoolean(CollectionPath, nameof(GlyphToggleEnabled), GlyphToggleEnabled);
                store.SetString(CollectionPath, nameof(CustomFontFamily), CustomFontFamily);
                store.SetBoolean(CollectionPath, nameof(BorderLeft), BorderLeft);
                store.SetBoolean(CollectionPath, nameof(BorderTop), BorderTop);
                store.SetBoolean(CollectionPath, nameof(BorderRight), BorderRight);
                store.SetBoolean(CollectionPath, nameof(BorderBottom), BorderBottom);

                store.SetInt32(CollectionPath, nameof(ColorCodeFg), ColorCodeFg);
                store.SetInt32(CollectionPath, nameof(ColorSummaryFg), ColorSummaryFg);
                store.SetInt32(CollectionPath, nameof(ColorParamName), ColorParamName);
                store.SetInt32(CollectionPath, nameof(ColorLink), ColorLink);
                store.SetInt32(CollectionPath, nameof(ColorSectionLabel), ColorSectionLabel);
                store.SetInt32(CollectionPath, nameof(GradientStop0), GradientStop0);
                store.SetInt32(CollectionPath, nameof(GradientStop1), GradientStop1);
                store.SetInt32(CollectionPath, nameof(GradientStop2), GradientStop2);
            }
            catch { /* non-critical */ }
        }

        // ── Internal helpers ──────────────────────────────────────────────────────

        private bool ReadBool(SettingsStore store, string name, bool fallback)
            => store.PropertyExists(CollectionPath, name)
               ? store.GetBoolean(CollectionPath, name)
               : fallback;

        private int ReadInt(SettingsStore store, string name, int fallback)
            => store.PropertyExists(CollectionPath, name)
               ? store.GetInt32(CollectionPath, name)
               : fallback;

        internal void SetPremiumUnlocked(bool value) => PremiumUnlocked = value;

        private static Color ToColor(int argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }

        public static int FromColor(Color c)
            => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
    }
}