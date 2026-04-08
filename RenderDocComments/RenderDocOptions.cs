using System;
using System.Windows.Media;
using Microsoft.VisualStudio.Settings;
using Microsoft.VisualStudio.Shell.Settings;

namespace RenderDocComments
{
    /// <summary>
    /// Persisted settings for the RenderDocComments extension, managing both free and Premium features.<br/>
    /// All Premium-gated options are read by the rest of the extension through the <c>Effective*</c> properties<br/>
    /// of this class's singleton instance. Call <see cref="Load"/> once at package initialization and<br/>
    /// call <see cref="Save"/> after any user-initiated change.
    /// </summary>
    /// <remarks>
    /// <para>This class serves as the single source of truth for extension configuration, handling:</para>
    /// <list type="bullet">
    /// <item><description><b>Licensing state:</b> Premium unlock status, license key, and instance ID.</description></item>
    /// <item><description><b>Free features:</b> Global render toggle (<see cref="RenderEnabled"/>).</description></item>
    /// <item><description><b>Premium features:</b> Theme auto-refresh, glyph toggle, custom font, border sides, and custom colors.</description></item>
    /// <item><description><b>Persistence:</b> Storage and retrieval via Visual Studio's <see cref="SettingsStore"/> mechanism.</description></item>
    /// </list>
    /// <para>The class uses a singleton pattern (<see cref="Instance"/>) to ensure consistent state across all components.<br/>
    /// Premium settings are gated by <see cref="LicenseManager.PremiumUnlocked"/> — when the license is not active,<br/>
    /// the <c>Effective*</c> properties return sensible defaults matching the free tier behavior.</para>
    /// </remarks>
    public sealed class RenderDocOptions
    {
        // ── Singleton ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Gets the singleton instance of <see cref="RenderDocOptions"/> used throughout the extension.<br/>
        /// All components should access settings through this instance to ensure consistent state.
        /// </summary>
        public static readonly RenderDocOptions Instance = new RenderDocOptions();

        /// <summary>
        /// Private constructor enforcing the singleton pattern.
        /// </summary>
        private RenderDocOptions()
        {
        }

        /// <summary>
        /// The Visual Studio settings collection path under which all extension settings are stored.
        /// </summary>
        /// <remarks>
        /// Value: <c>"RenderDocComments"</c>. This collection is created in the user settings store<br/>
        /// (<see cref="SettingsScope.UserSettings"/>) and persists across Visual Studio sessions.
        /// </remarks>
        private const string CollectionPath = "RenderDocComments";

        // ── FREE settings (always active) ─────────────────────────────────────────

        /// <summary>
        /// Gets or sets the global rendering toggle. When <c>false</c>, all documentation comment<br/>
        /// rendering is disabled regardless of Premium status. Default is <c>true</c>.
        /// </summary>
        /// <remarks>
        /// This is a free-tier setting — always active regardless of license status.<br/>
        /// When disabled, the <see cref="DocCommentRenderer.DocCommentAdornmentTagger.GetTags"/> method<br/>
        /// yields no tags, effectively hiding all rendered documentation comments.
        /// </remarks>
        public bool RenderEnabled { get; set; } = true;

        // ── Premium settings (disabled / ignored when PremiumUnlocked == false) ───────────

        /// <summary>
        /// Gets a value indicating whether the Premium tier is currently unlocked.<br/>
        /// This property is set by <see cref="LicenseManager"/> during activation and persisted across sessions.
        /// </summary>
        /// <remarks>
        /// <para>The setter is private — external code should use <see cref="SetPremiumUnlocked(bool)"/><br/>
        /// to modify this value (typically during license activation/deactivation).</para>
        /// <para>When <c>false</c>, all Premium features are disabled and the <c>Effective*</c> properties<br/>
        /// return free-tier defaults. When <c>true</c>, user-configured Premium settings take effect.</para>
        /// </remarks>
        public bool PremiumUnlocked { get; private set; } = false;

        // ── Licence key storage (persisted, not used for gating) ─────────────────

        /// <summary>The raw licence key entered by the user during activation.</summary>
        /// <remarks>
        /// <para>This property is persisted to the Visual Studio settings store but is not directly<br/>
        /// used for gating Premium features. The gating decision is based on <see cref="PremiumUnlocked"/>,<br/>
        /// which is determined by <see cref="LicenseManager"/> after validating the key with the activation server.</para>
        /// <para>The key is stored as a plain string and may be <c>null</c> if no key has been entered.</para>
        /// </remarks>
        public string LicenseKey { get; set; } = null;

        /// <summary>
        /// The Dodo Payments instance ID returned at activation time.<br/>
        /// Used for license deactivation and precise server-side validation.
        /// </summary>
        /// <remarks>
        /// <para>This identifier uniquely identifies the activated license instance on the Dodo Payments platform.<br/>
        /// It is used when:</para>
        /// <list type="bullet">
        /// <item><description><b>Deactivating:</b> Sending a deactivation request to the license server.</description></item>
        /// <item><description><b>Re-validating:</b> Checking license status during periodic validation (<see cref="LicenseManager.RevalidateOnStartupAsync"/>).</description></item>
        /// </list>
        /// <para>May be <c>null</c> if no license has been activated.</para>
        /// </remarks>
        public string LicenseInstanceId { get; set; } = null;

        // -- Theme auto-refresh (Premium 1) --

        /// <summary>
        /// Gets or sets whether adornment colors should automatically refresh when the Visual Studio<br/>
        /// color theme changes, without requiring the user to reopen the file. Requires Premium.
        /// </summary>
        /// <remarks>
        /// <para>When enabled and Premium is unlocked, the extension subscribes to <see cref="Microsoft.VisualStudio.PlatformUI.VSColorTheme.ThemeChanged"/><br/>
        /// and triggers a full tag rebuild with the new theme colors. When disabled (or Premium is locked),<br/>
        /// the user must reopen the file to see updated colors.</para>
        /// <para>Default value: <c>false</c> (free behavior — manual refresh required).</para>
        /// <para>The effective value is exposed via <see cref="EffectiveAutoRefresh"/>, which returns <c>false</c><br/>
        /// when Premium is locked regardless of this property's value.</para>
        /// </remarks>
        public bool AutoRefreshOnThemeChange { get; set; } = false;

        // -- Glyph toggle button (Premium 2) --

        /// <summary>
        /// Gets or sets whether a margin glyph button should be shown for each documentation comment block,<br/>
        /// allowing the user to toggle rendering on/off per-comment with a single click. Requires Premium.
        /// </summary>
        /// <remarks>
        /// <para>When enabled and Premium is unlocked, the <see cref="DocCommentRenderer.DocCommentGlyphFactory"/><br/>
        /// renders a toggle button in the editor's glyph margin next to each documentation comment.<br/>
        /// Clicking the button hides or shows the rendered comment for that specific block.</para>
        /// <para>When disabled (or Premium is locked), the extension uses caret-based visibility:<br/>
        /// the rendered comment hides when the caret enters the comment region and reappears when the caret leaves.</para>
        /// <para>Default value: <c>false</c> (free behavior — caret-based auto-hide only).</para>
        /// <para>The effective value is exposed via <see cref="EffectiveGlyphToggle"/>.</para>
        /// </remarks>
        public bool GlyphToggleEnabled { get; set; } = false;

        // -- Font (Premium 3) --

        /// <summary>
        /// Gets or sets the font family name used for rendered documentation text. Requires Premium.
        /// </summary>
        /// <remarks>
        /// <para>Default value: <c>"Segoe UI"</c> — the Windows default UI font.</para>
        /// <para>When Premium is locked, the effective font family is always <c>"Segoe UI"</c> regardless<br/>
        /// of this property's stored value. The effective value is exposed via <see cref="EffectiveFontFamily"/>.</para>
        /// <para>The font family string should be a valid WPF font family name (e.g., <c>"Consolas"</c>,<br/>
        /// <c>"Cascadia Code"</c>, <c>"Segoe UI"</c>). WPF font fallback chains are supported<br/>
        /// (e.g., <c>"Cascadia Mono, Consolas, Courier New"</c>).</para>
        /// </remarks>
        public string CustomFontFamily { get; set; } = "Segoe UI";

        // -- Border sides (Premium 4) --

        /// <summary>
        /// Gets or sets whether to show the gradient accent bar on the left side of the documentation block.<br/>
        /// Default: <c>true</c> (always shown, even in free tier).
        /// </summary>
        /// <remarks>
        /// <para>The left border is the default accent side and is always available in the free tier.<br/>
        /// When Premium is unlocked, the user can toggle this off. The effective value is exposed via<br/>
        /// <see cref="EffectiveBorderLeft"/>, which returns <c>true</c> when Premium is locked.</para>
        /// </remarks>
        public bool BorderLeft { get; set; } = true;

        /// <summary>
        /// Gets or sets whether to show the gradient accent bar on the top side of the documentation block.<br/>
        /// Requires Premium. Default: <c>false</c>.
        /// </summary>
        public bool BorderTop { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to show the gradient accent bar on the right side of the documentation block.<br/>
        /// Requires Premium. Default: <c>false</c>.
        /// </summary>
        public bool BorderRight { get; set; } = false;

        /// <summary>
        /// Gets or sets whether to show the gradient accent bar on the bottom side of the documentation block.<br/>
        /// Requires Premium. Default: <c>false</c>.
        /// </summary>
        public bool BorderBottom { get; set; } = false;

        // -- Colours (Premium 5) --

        /// <summary>
        /// Gets or sets the ARGB color value for code block text (inline and block code).<br/>
        /// Stored as a 32-bit signed integer with bytes in ARGB order. Default: <c>#CE9178</c> (orange-ish).
        /// </summary>
        public int ColorCodeFg { get; set; } = unchecked((int)0xFFCE9178);

        /// <summary>
        /// Gets or sets the ARGB color value for summary text. Requires Premium.<br/>
        /// Stored as a 32-bit signed integer with bytes in ARGB order. Default: <c>#D4D4D4</c> (light grey).
        /// </summary>
        public int ColorSummaryFg { get; set; } = unchecked((int)0xFFD4D4D4);

        /// <summary>
        /// Gets or sets the ARGB color value for parameter names in the parameter grid. Requires Premium.<br/>
        /// Stored as a 32-bit signed integer with bytes in ARGB order. Default: <c>#9CDCFE</c> (light blue).
        /// </summary>
        public int ColorParamName { get; set; } = unchecked((int)0xFF9CDCFE);

        /// <summary>
        /// Gets or sets the ARGB color value for hyperlinks and cref navigation targets. Requires Premium.<br/>
        /// Stored as a 32-bit signed integer with bytes in ARGB order. Default: <c>#569CD6</c> (VS blue).
        /// </summary>
        public int ColorLink { get; set; } = unchecked((int)0xFF569CD6);

        /// <summary>
        /// Gets or sets the ARGB color value for section labels (e.g., "Parameters:", "Returns:"). Requires Premium.<br/>
        /// Stored as a 32-bit signed integer with bytes in ARGB order. Default: <c>#969696</c> (dim grey).
        /// </summary>
        public int ColorSectionLabel { get; set; } = unchecked((int)0xFF969696);

        // Gradient bar: three stop colours (ARGB).

        /// <summary>
        /// Gets or sets the ARGB color for the first gradient stop (start) of the accent bar. Requires Premium.<br/>
        /// Default: <c>#FFBE6EF0</c> (violet).
        /// </summary>
        public int GradientStop0 { get; set; } = unchecked((int)0xFFBE6EF0);

        /// <summary>
        /// Gets or sets the ARGB color for the second gradient stop (midpoint) of the accent bar. Requires Premium.<br/>
        /// Default: <c>#C8784BC8</c> (purple, semi-transparent).
        /// </summary>
        public int GradientStop1 { get; set; } = unchecked((int)0xC8784BC8);

        /// <summary>
        /// Gets or sets the ARGB color for the third gradient stop (end) of the accent bar. Requires Premium.<br/>
        /// Default: <c>#50502896</c> (dark purple, more transparent).
        /// </summary>
        public int GradientStop2 { get; set; } = unchecked((int)0x50502896);

        // ── Helper: effective values (falls back to defaults when Premium is locked) ──

        /// <summary>
        /// Gets a value indicating whether Premium features are currently unlocked.<br/>
        /// Shorthand for <see cref="LicenseManager.PremiumUnlocked"/>.
        /// </summary>
        private static bool Premium => LicenseManager.PremiumUnlocked;

        /// <summary>
        /// Gets the effective value for theme auto-refresh, respecting the Premium gate.<br/>
        /// Returns <c>true</c> only if Premium is unlocked AND <see cref="AutoRefreshOnThemeChange"/> is enabled.
        /// </summary>
        public bool EffectiveAutoRefresh => Premium && AutoRefreshOnThemeChange;

        /// <summary>
        /// Gets the effective value for the glyph toggle feature, respecting the Premium gate.<br/>
        /// Returns <c>true</c> only if Premium is unlocked AND <see cref="GlyphToggleEnabled"/> is enabled.
        /// </summary>
        public bool EffectiveGlyphToggle => Premium && GlyphToggleEnabled;

        /// <summary>
        /// Gets the effective font family for rendered documentation.<br/>
        /// Returns <see cref="CustomFontFamily"/> if Premium is unlocked; otherwise, returns <c>"Segoe UI"</c>.
        /// </summary>
        public string EffectiveFontFamily => Premium ? CustomFontFamily : "Segoe UI";

        /// <summary>
        /// Gets the effective value for the left accent bar visibility.<br/>
        /// Always returns <c>true</c> when Premium is locked (free tier always shows left bar);<br/>
        /// otherwise returns the user-configured <see cref="BorderLeft"/> value.
        /// </summary>
        public bool EffectiveBorderLeft => !Premium || BorderLeft;

        /// <summary>
        /// Gets the effective value for the top accent bar visibility, respecting the Premium gate.<br/>
        /// Returns <see cref="BorderTop"/> if Premium is unlocked; otherwise, returns <c>false</c>.
        /// </summary>
        public bool EffectiveBorderTop => Premium && BorderTop;

        /// <summary>
        /// Gets the effective value for the right accent bar visibility, respecting the Premium gate.<br/>
        /// Returns <see cref="BorderRight"/> if Premium is unlocked; otherwise, returns <c>false</c>.
        /// </summary>
        public bool EffectiveBorderRight => Premium && BorderRight;

        /// <summary>
        /// Gets the effective value for the bottom accent bar visibility, respecting the Premium gate.<br/>
        /// Returns <see cref="BorderBottom"/> if Premium is unlocked; otherwise, returns <c>false</c>.
        /// </summary>
        public bool EffectiveBorderBottom => Premium && BorderBottom;

        /// <summary>
        /// Gets the effective color for code block text, respecting the Premium gate.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="ColorCodeFg"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#CE9178</c> (orange-ish).
        /// </summary>
        public Color EffectiveColorCodeFg => Premium ? ToColor(ColorCodeFg) : Color.FromRgb(0xCE, 0x91, 0x78);

        /// <summary>
        /// Gets the effective color for summary text, respecting the Premium gate.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="ColorSummaryFg"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#D4D4D4</c> (light grey).
        /// </summary>
        public Color EffectiveColorSummaryFg => Premium ? ToColor(ColorSummaryFg) : Color.FromRgb(0xD4, 0xD4, 0xD4);

        /// <summary>
        /// Gets the effective color for parameter names, respecting the Premium gate.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="ColorParamName"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#9CDCFE</c> (light blue).
        /// </summary>
        public Color EffectiveColorParamName => Premium ? ToColor(ColorParamName) : Color.FromRgb(0x9C, 0xDC, 0xFE);

        /// <summary>
        /// Gets the effective color for hyperlinks, respecting the Premium gate.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="ColorLink"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#569CD6</c> (VS blue).
        /// </summary>
        public Color EffectiveColorLink => Premium ? ToColor(ColorLink) : Color.FromRgb(0x56, 0x9C, 0xD6);

        /// <summary>
        /// Gets the effective color for section labels, respecting the Premium gate.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="ColorSectionLabel"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#969696</c> (dim grey).
        /// </summary>
        public Color EffectiveColorSectionLabel => Premium ? ToColor(ColorSectionLabel) : Color.FromRgb(0x96, 0x96, 0x96);

        /// <summary>
        /// Gets the effective color for the first gradient stop (start) of the accent bar.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="GradientStop0"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#FFBE6EF0</c> (violet).
        /// </summary>
        public Color EffectiveGradientStop0 => Premium ? ToColor(GradientStop0) : Color.FromArgb(0xFF, 0xBE, 0x6E, 0xF0);

        /// <summary>
        /// Gets the effective color for the second gradient stop (midpoint) of the accent bar.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="GradientStop1"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#C8784BC8</c> (purple, semi-transparent).
        /// </summary>
        public Color EffectiveGradientStop1 => Premium ? ToColor(GradientStop1) : Color.FromArgb(0xC8, 0x78, 0x4B, 0xC8);

        /// <summary>
        /// Gets the effective color for the third gradient stop (end) of the accent bar.<br/>
        /// Returns <see cref="ToColor"/> of <see cref="GradientStop2"/> if Premium is unlocked;<br/>
        /// otherwise, returns the default <c>#50502896</c> (dark purple, more transparent).
        /// </summary>
        public Color EffectiveGradientStop2 => Premium ? ToColor(GradientStop2) : Color.FromArgb(0x50, 0x50, 0x28, 0x96);

        // ── Persistence ───────────────────────────────────────────────────────────

        /// <summary>
        /// Loads all extension settings from the Visual Studio user settings store.<br/>
        /// Called once during package initialization (<see cref="RenderDocCommentsPackage.InitializeAsync"/>).
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to create the <see cref="ShellSettingsManager"/><br/>
        /// and access the Visual Studio settings infrastructure.
        /// </param>
        /// <remarks>
        /// <para>The method reads settings from the <see cref="CollectionPath"/> collection in the user<br/>
        /// settings store (<see cref="SettingsScope.UserSettings"/>). For each property:</para>
        /// <list type="bullet">
        /// <item><description>Boolean properties are read via <see cref="ReadBool"/> with the current default as fallback.</description></item>
        /// <item><description>Integer properties (colors) are read via <see cref="ReadInt"/> with the current default as fallback.</description></item>
        /// <item><description>String properties (license key, font family) are read directly if the property exists.</description></item>
        /// </list>
        /// <para>If the collection doesn't exist (first run), the method returns immediately, leaving<br/>
        /// all properties at their default values. All exceptions are caught silently to prevent<br/>
        /// package initialization failures from crashing Visual Studio.</para>
        /// </remarks>
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

        /// <summary>
        /// Saves all extension settings to the Visual Studio user settings store.<br/>
        /// Called after the user modifies any option in the Render Doc Options dialog.
        /// </summary>
        /// <param name="serviceProvider">
        /// The <see cref="IServiceProvider"/> used to create the <see cref="ShellSettingsManager"/><br/>
        /// and access the Visual Studio settings infrastructure.
        /// </param>
        /// <remarks>
        /// <para>The method writes all properties to the <see cref="CollectionPath"/> collection in the user<br/>
        /// settings store (<see cref="SettingsScope.UserSettings"/>). If the collection doesn't exist,<br/>
        /// it is created first via <see cref="WritableSettingsStore.CreateCollection"/>.</para>
        /// <list type="bullet">
        /// <item><description>Boolean properties are written via <see cref="WritableSettingsStore.SetBoolean"/>.</description></item>
        /// <item><description>Integer properties (colors) are written via <see cref="WritableSettingsStore.SetInt32"/>.</description></item>
        /// <item><description>String properties (license key, font family) are written via <see cref="WritableSettingsStore.SetString"/>,<br/>
        /// with <c>null</c> values converted to empty strings.</description></item>
        /// </list>
        /// <para>All exceptions are caught silently to prevent save failures from disrupting the user experience.<br/>
        /// Settings that fail to save will be lost on the next Visual Studio restart.</para>
        /// </remarks>
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

        /// <summary>
        /// Reads a boolean property from the settings store, falling back to a default value<br/>
        /// if the property doesn't exist (first run or corrupted settings).
        /// </summary>
        /// <param name="store">
        /// The read-only settings store to read from.
        /// </param>
        /// <param name="name">
        /// The property name within the <see cref="CollectionPath"/> collection.
        /// </param>
        /// <param name="fallback">
        /// The default value to return if the property doesn't exist in the store.
        /// </param>
        /// <returns>
        /// The stored boolean value if the property exists; otherwise, <paramref name="fallback"/>.
        /// </returns>
        private bool ReadBool(SettingsStore store, string name, bool fallback)
            => store.PropertyExists(CollectionPath, name)
               ? store.GetBoolean(CollectionPath, name)
               : fallback;

        /// <summary>
        /// Reads a 32-bit integer property from the settings store, falling back to a default value<br/>
        /// if the property doesn't exist (first run or corrupted settings). Used for color values.
        /// </summary>
        /// <param name="store">
        /// The read-only settings store to read from.
        /// </param>
        /// <param name="name">
        /// The property name within the <see cref="CollectionPath"/> collection.
        /// </param>
        /// <param name="fallback">
        /// The default value to return if the property doesn't exist in the store.
        /// </param>
        /// <returns>
        /// The stored integer value if the property exists; otherwise, <paramref name="fallback"/>.
        /// </returns>
        private int ReadInt(SettingsStore store, string name, int fallback)
            => store.PropertyExists(CollectionPath, name)
               ? store.GetInt32(CollectionPath, name)
               : fallback;

        /// <summary>
        /// Sets the Premium unlocked state. Called by <see cref="LicenseManager"/> during<br/>
        /// license activation, deactivation, and validation.
        /// </summary>
        /// <param name="value">
        /// <c>true</c> to unlock Premium features; <c>false</c> to lock them.
        /// </param>
        /// <remarks>
        /// <para>This is the only external entry point for modifying <see cref="PremiumUnlocked"/>.<br/>
        /// After changing this value, the caller should call <see cref="Save"/> to persist the change<br/>
        /// and <see cref="SettingsChangedBroadcast.RaiseSettingsChanged"/> to notify subscribers<br/>
        /// that the effective values have changed.</para>
        /// </remarks>
        internal void SetPremiumUnlocked(bool value) => PremiumUnlocked = value;

        /// <summary>
        /// Converts a 32-bit ARGB integer to a <see cref="Color"/> structure.<br/>
        /// Used to convert stored color values to WPF brush-compatible format.
        /// </summary>
        /// <param name="argb">
        /// A 32-bit signed integer with bytes in ARGB order (alpha, red, green, blue).<br/>
        /// The <c>unchecked</c> cast is used when assigning literal hex values to <c>int</c> properties.
        /// </param>
        /// <returns>
        /// A <see cref="Color"/> with alpha, red, green, and blue channels extracted from the integer.
        /// </returns>
        /// <remarks>
        /// <para>The extraction uses bitwise operations:</para>
        /// <list type="bullet">
        /// <item><description>Alpha: <c>(argb &gt;&gt; 24) &amp; 0xFF</c></description></item>
        /// <item><description>Red: <c>(argb &gt;&gt; 16) &amp; 0xFF</c></description></item>
        /// <item><description>Green: <c>(argb &gt;&gt; 8) &amp; 0xFF</c></description></item>
        /// <item><description>Blue: <c>argb &amp; 0xFF</c></description></item>
        /// </list>
        /// </remarks>
        private static Color ToColor(int argb)
        {
            byte a = (byte)((argb >> 24) & 0xFF);
            byte r = (byte)((argb >> 16) & 0xFF);
            byte g = (byte)((argb >> 8) & 0xFF);
            byte b = (byte)(argb & 0xFF);
            return Color.FromArgb(a, r, g, b);
        }

        /// <summary>
        /// Converts a <see cref="Color"/> structure to a 32-bit ARGB integer.<br/>
        /// Used by the options dialog to display and edit color values in UI controls.
        /// </summary>
        /// <param name="c">
        /// The <see cref="Color"/> to convert.
        /// </param>
        /// <returns>
        /// A 32-bit signed integer with bytes in ARGB order (alpha, red, green, blue).
        /// </returns>
        /// <remarks>
        /// <para>The packing uses bitwise operations:</para>
        /// <c>(A &lt;&lt; 24) | (R &lt;&lt; 16) | (G &lt;&lt; 8) | B</c>
        /// </remarks>
        public static int FromColor(Color c)
            => (c.A << 24) | (c.R << 16) | (c.G << 8) | c.B;
    }
}