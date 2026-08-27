/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    TagBadgeToggleState.cs
 *  Purpose: Session-scoped store of comment lines whose tag badges the user has
 *           dismissed by clicking the pill.
 *
 *  Architecture Role:
 *    Static state shared by every view. Lines are identified by their trimmed
 *    text content rather than a buffer position — positions shift whenever edits
 *    occur above a line, whereas text keys survive them. Because any edit to the
 *    tagged line itself rebuilds the pill (with freshly captured text), a stale
 *    key can never mis-dismiss a different line.
 *
 *  Key Classes:
 *    TagBadgeToggleState — HashSet-backed IsHidden / Toggle / Clear surface.
 *
 *  Dependencies:
 *    • None (plain string keys) — callers pass the trimmed line text.
 *    • CommentTagBadgeTagger — reads IsHidden while building tags; pill click
 *      handlers call Toggle then raise a settings broadcast to force a rebuild.
 *
 *  When to Edit:
 *    • Persisting dismissal across sessions — replace the HashSet with serialised
 *      storage keyed by file path + line text hash.
 *    • Adding per-tag dismissal granularity — widen the stored key to include the
 *      canonical tag name alongside the line text.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System.Collections.Generic;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// Tracks which comment lines have had their tag badges dismissed via click.<br/>
    /// Dismissal is session-scoped: it survives edits and view switches but resets
    /// when Visual Studio exits or <see cref="Clear"/> is called.
    /// </summary>
    /// <remarks>
    /// <para><b>Key choice:</b> the trimmed text of the line. Buffer-position keys go
    /// stale the moment an edit above the line shifts it; text keys do not. When the
    /// tagged line itself is edited, the badge tagger rebuilds and re-registers the
    /// pill with the new text, so a dismissed key can never silently attach to
    /// unrelated content.</para>
    /// <para>Thread safety: all access happens on the UI thread (editor events and
    /// WPF click handlers), so no additional synchronisation is required.</para>
    /// </remarks>
    public static class TagBadgeToggleState
    {
        /// <summary>
        /// Trimmed line texts whose badges are currently dismissed.
        /// </summary>
        private static readonly HashSet<string> _hiddenLineTexts =
            new HashSet<string>(System.StringComparer.Ordinal);

        /// <summary>
        /// Determines whether the badge(s) on a line are currently dismissed.
        /// </summary>
        /// <param name="trimmedLineText">The line's text with surrounding whitespace removed.</param>
        /// <returns><c>true</c> if the user has dismissed this line's badges.</returns>
        public static bool IsHidden(string trimmedLineText)
            => _hiddenLineTexts.Contains(trimmedLineText);

        /// <summary>
        /// Toggles dismissal for a line:<br/>
        /// dismissed lines become visible again and vice versa.
        /// </summary>
        /// <param name="trimmedLineText">The line's text with surrounding whitespace removed.</param>
        /// <remarks>
        /// Callers should raise <see cref="SettingsChangedBroadcast.RaiseSettingsChanged"/>
        /// afterwards so the badge tagger rebuilds and the change becomes visible.
        /// The pill click handler performs both steps.
        /// </remarks>
        public static void Toggle(string trimmedLineText)
        {
            if (!_hiddenLineTexts.Add(trimmedLineText))
                _hiddenLineTexts.Remove(trimmedLineText);
        }

        /// <summary>
        /// Clears every dismissal, restoring all badges. Called on settings resets.
        /// </summary>
        public static void Clear() => _hiddenLineTexts.Clear();
    }
}
