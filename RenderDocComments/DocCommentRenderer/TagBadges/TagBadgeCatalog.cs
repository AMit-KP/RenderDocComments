/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    TagBadgeCatalog.cs
 *  Purpose: Central registry of recognised comment tags (TODO, FIXME, HACK, …),
 *           their default badge colours, human-readable descriptions, and the
 *           adaptive-contrast helpers used when painting pills.
 *
 *  Architecture Role:
 *    Pure data + colour math. Consumed by CommentTagBadgeTagger (detection and
 *    pill construction) and by the options window (per-tag rows). Deliberately
 *    free of Visual Studio SDK types so it stays trivially testable.
 *
 *  Key Types:
 *    TagBadgeDefinition — Immutable record: canonical name, description,
 *                         default ARGB colour.
 *    TagBadgeCatalog    — Static lookup: ordered tag list, WARNING→WARN alias
 *                         normalisation, default colours, adaptive foreground /
 *                         border brushes computed from perceived luminance.
 *
 *  Dependencies:
 *    • System.Windows.Media (Color, Brush, SolidColorBrush) — WPF colour types.
 *
 *  When to Edit:
 *    • Adding a new recognisable tag — append to _tags and (optionally) to
 *      _aliases if it has alternative spellings.
 *    • Changing a default colour — edit the hex literal in _tags.
 *    • Changing adaptive-contrast behaviour — edit LuminanceThreshold /
 *      GetAdaptiveForeground / GetAdaptiveBorderBrush.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// Immutable definition of one recognised comment tag.<br/>
    /// Groups the canonical uppercase name, a short tooltip description, and the<br/>
    /// factory-default badge colour used when no premium override exists.
    /// </summary>
    public sealed class TagBadgeDefinition
    {
        /// <summary>
        /// Gets the canonical uppercase tag name (e.g., <c>"TODO"</c>).<br/>
        /// Aliases such as <c>WARNING</c> never appear here — they resolve to a
        /// canonical entry via <see cref="TagBadgeCatalog.TryNormalize"/>.
        /// </summary>
        public string Name { get; }

        /// <summary>
        /// Gets the short description shown in the badge tooltip<br/>
        /// (e.g., <c>"Broken — needs fixing"</c>).
        /// </summary>
        public string Description { get; }

        /// <summary>
        /// Gets the factory-default badge background colour as a 32-bit ARGB integer.<br/>
        /// Used whenever the user has not applied a Premium colour override.
        /// </summary>
        public int DefaultColorArgb { get; }

        /// <summary>
        /// Initializes a new <see cref="TagBadgeDefinition"/>.
        /// </summary>
        /// <param name="name">Canonical uppercase tag name.</param>
        /// <param name="description">Short tooltip description.</param>
        /// <param name="defaultColorArgb">Default ARGB badge colour.</param>
        public TagBadgeDefinition(string name, string description, int defaultColorArgb)
        {
            Name = name;
            Description = description;
            DefaultColorArgb = defaultColorArgb;
        }
    }

    /// <summary>
    /// Static registry of every comment tag the badge feature recognises, plus<br/>
    /// colour helpers implementing theme-independent adaptive contrast for pills.
    /// </summary>
    /// <remarks>
    /// <para>The catalogue deliberately excludes <c>NOSONAR</c>: it is a tool
    /// suppression directive rather than a note for humans, so badging it would add
    /// clutter exactly where developers intentionally silence warnings.</para>
    /// <para><b>Adaptive contrast:</b> the tag colour is painted as the pill's
    /// <i>background</i>; the label colour is chosen automatically from the
    /// background's perceived luminance (<see cref="PerceivedLuminance"/>). Light
    /// backgrounds receive near-black labels, everything else receives near-white.
    /// This guarantees readability for both the built-in palette and any Premium
    /// user-picked colour, on any editor theme.</para>
    /// </remarks>
    public static class TagBadgeCatalog
    {
        // ── Definitions ───────────────────────────────────────────────────────────

        /// <summary>
        /// Ordered list of all canonical tag definitions.<br/>
        /// The order drives both detection precedence (irrelevant — matching is by
        /// alternation) and the row order in the options window.
        /// </summary>
        private static readonly TagBadgeDefinition[] _tags =
        {
            // ── Action needed ─────────────────────────────────────────────────────
            new TagBadgeDefinition("TODO",       "Something to do later",              unchecked((int)0xFF4FC1FF)),
            new TagBadgeDefinition("FIXME",      "Broken — needs fixing",              unchecked((int)0xFFF97583)),
            new TagBadgeDefinition("BUG",        "Known bug",                          unchecked((int)0xFFF14C4C)),
            new TagBadgeDefinition("REVIEW",     "Needs a second pair of eyes",        unchecked((int)0xFFC586C0)),
            new TagBadgeDefinition("OPTIMIZE",   "Works but could be faster",          unchecked((int)0xFF4EC9B0)),

            // ── Caution ───────────────────────────────────────────────────────────
            new TagBadgeDefinition("HACK",       "Works but it's ugly",                unchecked((int)0xFFDCDCAA)),
            new TagBadgeDefinition("WARN",       "Tread carefully here",               unchecked((int)0xFFCE9178)),
            new TagBadgeDefinition("TEMP",       "Temporary code — remove later",      unchecked((int)0xFFD7BA7D)),

            // ── Information ───────────────────────────────────────────────────────
            new TagBadgeDefinition("NOTE",       "Important information",              unchecked((int)0xFF9CDCFE)),
            new TagBadgeDefinition("CHANGED",    "Tracks what was modified and why",   unchecked((int)0xFF29B8DB)),
            new TagBadgeDefinition("DEPRECATED", "Don't use this anymore",             unchecked((int)0xFF808080)),

            // ── Reasoning / formal ────────────────────────────────────────────────
            new TagBadgeDefinition("SAFETY",     "Explains why unsafe code is okay",   unchecked((int)0xFF6A9955)),
            new TagBadgeDefinition("INVARIANT",  "Documents a condition that must hold", unchecked((int)0xFFB48EFF)),
            new TagBadgeDefinition("ASSUME",     "Documents an assumption being made", unchecked((int)0xFF7FB4D8)),
            new TagBadgeDefinition("MAGIC",      "Explains a magic number or value",   unchecked((int)0xFFDDB0FF)),
        };

        /// <summary>
        /// Alias map from alternative spellings to canonical names.<br/>
        /// Currently only <c>WARNING</c> → <c>WARN</c>.
        /// </summary>
        private static readonly Dictionary<string, string> _aliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "WARNING", "WARN" },
            };

        /// <summary>
        /// Read-only view over <see cref="_tags"/> for consumers that need to iterate
        /// every known tag (options window rows, settings serialisation round-trips).
        /// </summary>
        public static IReadOnlyList<TagBadgeDefinition> Tags => _tags;

        // ── Lookup helpers ────────────────────────────────────────────────────────

        /// <summary>
        /// Fast lookup from canonical name → definition.
        /// </summary>
        private static readonly Dictionary<string, TagBadgeDefinition> _byName =
            _tags.ToDictionary(t => t.Name, StringComparer.Ordinal);

        /// <summary>
        /// Attempts to normalise a raw matched token into its canonical tag name.<br/>
        /// Matching is case-sensitive by design: only UPPERCASE spellings are treated
        /// as tags, which mirrors the community convention and avoids false positives
        /// on ordinary words like <c>note</c> or <c>assume</c> inside prose.
        /// </summary>
        /// <param name="raw">The token as matched in source text (e.g., <c>"WARNING"</c>).</param>
        /// <param name="canonical">
        /// The canonical name (<paramref name="raw"/> itself, or its alias target).
        /// </param>
        /// <returns>
        /// <c>true</c> if <paramref name="raw"/> is a known tag or alias; otherwise <c>false</c>.
        /// </returns>
        public static bool TryNormalize(string raw, out string canonical)
        {
            canonical = null;
            if (string.IsNullOrEmpty(raw)) return false;
            if (_byName.ContainsKey(raw)) { canonical = raw; return true; }
            return _aliases.TryGetValue(raw, out canonical);
        }

        /// <summary>
        /// Gets the factory-default badge background colour for a canonical tag name.
        /// </summary>
        /// <param name="canonicalName">Canonical tag name (post-<see cref="TryNormalize"/>).</param>
        /// <returns>The default <see cref="Color"/>, or grey if the name is unknown.</returns>
        public static Color GetDefaultColor(string canonicalName)
            => _byName.TryGetValue(canonicalName, out var def)
               ? Color.FromArgb(
                     (byte)((def.DefaultColorArgb >> 24) & 0xFF),
                     (byte)((def.DefaultColorArgb >> 16) & 0xFF),
                     (byte)((def.DefaultColorArgb >> 8) & 0xFF),
                     (byte)(def.DefaultColorArgb & 0xFF))
               : Colors.Gray;

        /// <summary>
        /// Gets the short tooltip description for a canonical tag name.
        /// </summary>
        /// <param name="canonicalName">Canonical tag name.</param>
        /// <returns>The description, or an empty string if the name is unknown.</returns>
        public static string GetDescription(string canonicalName)
            => _byName.TryGetValue(canonicalName, out var def) ? def.Description : string.Empty;

        // ── Adaptive contrast helpers ─────────────────────────────────────────────

        /// <summary>
        /// Luminance decision boundary (0–255 perceived scale) separating backgrounds
        /// that need dark labels from those that need light labels.
        /// </summary>
        private const double LuminanceThreshold = 140.0;

        /// <summary>
        /// Computes the perceived brightness of a colour on a 0–255 scale using the
        /// standard NTSC weighting (green dominates human brightness perception).
        /// </summary>
        /// <param name="c">The colour to measure.</param>
        /// <returns>Approximately 0 (black) to 255 (white).</returns>
        public static double PerceivedLuminance(Color c)
            => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;

        /// <summary>
        /// Chooses a label colour that is readable on top of the given background:<br/>
        /// near-black for light backgrounds, near-white for everything else.
        /// </summary>
        /// <param name="background">The pill's background colour.</param>
        /// <returns>An opaque foreground colour with guaranteed contrast.</returns>
        public static Color GetAdaptiveForeground(Color background)
            => PerceivedLuminance(background) > LuminanceThreshold
               ? Color.FromRgb(0x1E, 0x1E, 0x1E)   // near-black label
               : Color.FromRgb(0xF1, 0xF1, 0xF1);  // near-white label

        /// <summary>
        /// Builds a hairline border brush that contrasts subtly with the pill
        /// background, giving the badge definition on both dark and light themes.
        /// </summary>
        /// <param name="background">The pill's background colour.</param>
        /// <returns>A semi-transparent black or white single-pixel brush.</returns>
        public static Brush GetAdaptiveBorderBrush(Color background)
        {
            var brush = PerceivedLuminance(background) > LuminanceThreshold
                ? Color.FromArgb(0x40, 0x00, 0x00, 0x00)   // faint dark outline
                : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);  // faint light outline
            var b = new SolidColorBrush(brush);
            b.Freeze();
            return b;
        }
    }
}
