/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    CommentTagBadgeTagger.cs
 *  Purpose: Scans plain (non-doc) comments for conventional tag keywords
 *           (TODO, FIXME, HACK, …) and replaces each tag token IN PLACE with a
 *           rounded stadium pill — "// TODO: fix" renders as "// [TODO] fix".
 *
 *  Architecture Role:
 *    Implements ITagger<IntraTextAdornmentTag> — the same contract as the
 *    documentation-card tagger. Each tag token's SPAN is collapsed by the editor
 *    and the pill renders exactly where the word stood; surrounding comment text
 *    and code are untouched. Instantiated once per VIEW by
 *    CommentTagBadgeTaggerProvider; subscribed to buffer changes, caret movement,
 *    view closure, and settings broadcasts.
 *
 *  Detection Pipeline (per snapshot rebuild):
 *    1. CollectCommentRanges  — single char-walk over every line classifying
 *       plain line-comment and block-comment regions per language, skipping
 *       string literals (quote-parity heuristic) and doc-comment syntax
 *       (///, ''', /**, /*! , //!, (*$) which the card renderer owns.
 *    2. Tag regex             — compiled UPPERCASE-only alternation matched
 *       inside each collected range; WARNING normalises onto WARN.
 *    3. Pill construction     — one stadium pill per match, spanning the token
 *       (plus an immediately following colon); adaptive label contrast from
 *       TagBadgeCatalog; click toggles dismissal via TagBadgeToggleState +
 *       settings broadcast.
 *
 *  Visibility Model:
 *    Caret-based hide at line granularity: when the caret is on a pill's line the
 *    raw token reappears for editing; moving away re-renders the pill.
 *
 *  Key Classes:
 *    CommentTagBadgeTagger — ITagger implementation with snapshot cache.
 *
 *  Dependencies:
 *    • TagBadgeCatalog.cs          — definitions, colours, contrast helpers.
 *    • TagBadgeToggleState.cs      — click-dismissal store.
 *    • RenderDocOptions.cs         — TagBadgesEnabled, EffectiveTag* gating.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Badges appear on non-comments / miss comments — fix CollectCommentRanges.
 *    • A tag is not recognised — check _tagRegex against TagBadgeCatalog.Tags.
 *    • Pill styling changes — CreatePill.
 *    • Dismissal behaves oddly after edits — see TagBadgeToggleState key choice.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// Replaces tag keywords inside plain comments with rounded stadium pills
    /// across C#, VB.NET, F#, and C++ buffers.
    /// </summary>
    /// <remarks>
    /// <para><b>In-place replacement:</b> each tag token's span (plus an immediately
    /// following colon, if present) is collapsed and the pill renders exactly where
    /// the word stood — <c>// TODO: fix</c> becomes <c>// [TODO] fix</c>.</para>
    /// <para><b>Caching:</b> identical strategy to <c>DocCommentAdornmentTagger</c> —
    /// results are cached per snapshot and invalidated by a static settings-generation
    /// counter that is bumped whenever <see cref="SettingsChangedBroadcast.SettingsChanged"/>
    /// fires. A two-phase clear/rebuild prevents stale-pill flashes during transitions.</para>
    /// </remarks>
    internal sealed class CommentTagBadgeTagger
        : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;

        private ITextSnapshot _cachedSnapshot;
        private int _cachedSettingsGen = -1;
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> _cachedTags;

        private static int _settingsGeneration = 0;
        private bool _forceEmpty = false;
        private volatile int _caretLine = -1;

        /// <summary>
        /// Raised when the set of badges changes so the editor re-queries tags.
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Creates a tagger bound to a specific view/buffer pair and subscribes to
        /// buffer changes, caret movement, view closure, and settings broadcasts.
        /// </summary>
        /// <param name="buffer">The text buffer to scan.</param>
        /// <param name="view">The WPF view hosting rendered pills (font metrics source).</param>
        public CommentTagBadgeTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Yields pill tags intersecting the requested spans after applying
        /// master-toggle, per-file-override, force-empty, caret-hide, and
        /// intersection filters.
        /// </summary>
        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (_forceEmpty) yield break;
            if (!RenderDocOptions.Instance.TagBadgesEnabled) yield break;

            if (_buffer.Properties.TryGetProperty("RenderDocComments_Disabled", out bool disabled) && disabled)
                yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);

            foreach (var tag in tags)
            {
                // Caret-based hide: the raw token returns while the user edits its line.
                if (_caretLine >= 0)
                {
                    int s = snapshot.GetLineNumberFromPosition(tag.Span.Start);
                    int e = snapshot.GetLineNumberFromPosition(tag.Span.End);
                    if (_caretLine >= s && _caretLine <= e) continue;
                }

                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tag.Span)))
                    yield return tag;
            }
        }

        /// <summary>
        /// Returns cached tags for the snapshot or rebuilds them when either the
        /// snapshot or the settings generation has changed.
        /// </summary>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> GetOrBuildTags(ITextSnapshot snapshot)
        {
            if (_cachedSnapshot == snapshot &&
                _cachedSettingsGen == _settingsGeneration &&
                _cachedTags != null)
                return _cachedTags;

            _cachedSnapshot = snapshot;
            _cachedSettingsGen = _settingsGeneration;
            _cachedTags = BuildTags(snapshot);
            return _cachedTags;
        }

        // ── Language detection ────────────────────────────────────────────────────

        /// <summary>Supported buffer languages (mirrors the card renderer's mapping).</summary>
        private enum BufferLanguage { CSharp, VBNet, FSharp, Cpp }

        /// <summary>
        /// Maps the buffer's content type onto a <see cref="BufferLanguage"/>.
        /// Unknown content types default to C# semantics (<c>//</c> comments).
        /// </summary>
        private static BufferLanguage GetLanguage(ITextBuffer buffer)
        {
            try
            {
                var ct = buffer.ContentType;
                if (ct.IsOfType("C/C++")) return BufferLanguage.Cpp;
                if (ct.IsOfType("Basic")) return BufferLanguage.VBNet;
                if (ct.IsOfType("F#") || ct.IsOfType("FSharp")) return BufferLanguage.FSharp;
                return BufferLanguage.CSharp;
            }
            catch { return BufferLanguage.CSharp; }
        }

        // ── Tag matching ──────────────────────────────────────────────────────────

        /// <summary>
        /// UPPERCASE-only tag alternation. Case-sensitivity is intentional: it mirrors
        /// the community convention and prevents false positives on ordinary words
        /// ("note", "assume") inside prose. <c>WARNING</c> precedes <c>WARN</c> so the
        /// longer alias wins, and both collapse onto the canonical WARN entry.
        /// </summary>
        private static readonly Regex _tagRegex = new Regex(
            @"\b(TODO|FIXME|HACK|NOTE|BUG|REVIEW|OPTIMIZE|TEMP|WARNING|WARN|" +
            @"DEPRECATED|CHANGED|SAFETY|INVARIANT|ASSUME|MAGIC)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// Upper bound on comment text captured into tooltips, keeping WPF tooltips fast.
        /// </summary>
        private const int MaxTooltipTailLength = 200;

        /// <summary>Newline characters used to cut multi-line block tails.</summary>
        private static readonly char[] _newlineChars = { '\r', '\n' };

        /// <summary>
        /// Matches a trailing block-comment closer (and surrounding whitespace) so
        /// single-line tails like <c>"broken */"</c> end cleanly at the prose.
        /// </summary>
        private static readonly Regex _trailingCloser = new Regex(
            @"\s*\*/\s*$", RegexOptions.Compiled);

        // ── BuildTags ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Single full pass: collect comment ranges, match tags, and emit one
        /// in-place pill per match — spanning the token (plus an immediately
        /// following colon) so the editor collapses the word and renders the
        /// pill exactly where it stood.
        /// </summary>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;

            var lang = GetLanguage(_buffer);
            var ranges = new List<CommentRange>();
            CollectCommentRanges(snapshot, lang, ranges);

            foreach (var range in ranges)
            {
                int len = range.End - range.Start;
                if (len <= 0) continue;
                string text;
                try { text = snapshot.GetText(range.Start, len); }
                catch { continue; }

                foreach (Match m in _tagRegex.Matches(text))
                {
                    if (!TagBadgeCatalog.TryNormalize(m.Value, out var canonical)) continue;
                    if (!opts.EffectiveTagEnabled(canonical)) continue;

                    var line = snapshot.GetLineFromLineNumber(
                        snapshot.GetLineNumberFromPosition(range.Start + m.Index));
                    var trimmedLine = line.GetText().Trim();

                    // Respect click-dismissal (text-keyed; survives edits above).
                    if (TagBadgeToggleState.IsHidden(trimmedLine)) continue;

                    // Swallow an immediately following colon: "// TODO: fix" →
                    // "// [pill] fix" rather than "// [pill]: fix".
                    int tokenLen = m.Length;
                    if (m.Index + tokenLen < text.Length && text[m.Index + tokenLen] == ':')
                        tokenLen++;

                    var panel = CreatePill(canonical, ExtractTail(text, m.Index + m.Length), trimmedLine);
                    if (panel == null) continue;

                    int start = range.Start + m.Index;
                    var span = new SnapshotSpan(snapshot, start, tokenLen);
                    var tag = new IntraTextAdornmentTag(panel, null, PositionAffinity.Predecessor);
                    result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
                }
            }

            return result;
        }

        /// <summary>
        /// Extracts the human-readable remainder of a comment following a matched tag:<br/>
        /// cut at the first newline (block ranges span lines — following lines must
        /// never leak into the tooltip), trailing <c>*/</c> closer removed,
        /// optional colon and whitespace trimmed, capped at
        /// <see cref="MaxTooltipTailLength"/> characters.
        /// </summary>
        private static string ExtractTail(string rangeText, int tailStart)
        {
            if (tailStart >= rangeText.Length) return string.Empty;
            var tail = rangeText.Substring(tailStart);

            int nl = tail.IndexOfAny(_newlineChars);
            if (nl >= 0) tail = tail.Substring(0, nl);

            tail = _trailingCloser.Replace(tail, string.Empty);
            tail = tail.TrimStart(':', ' ', '\t');

            if (tail.Length > MaxTooltipTailLength)
                tail = tail.Substring(0, MaxTooltipTailLength) + "…";
            return tail;
        }

        // ── Comment-range collection ──────────────────────────────────────────────

        /// <summary>A contiguous comment region in buffer coordinates.</summary>
        private struct CommentRange
        {
            public int Start;
            public int End;
            public CommentRange(int start, int end) { Start = start; End = end; }
        }

        /// <summary>
        /// Walks every line once, recording plain comment regions while skipping:
        /// <list type="bullet">
        /// <item><description>double-quoted string contents (quote-parity heuristic;</description></item>
        /// <item><description>doc-comment syntaxes owned by the card renderer:
        ///   <c>///</c>, <c>'''</c>, <c>/** … */</c>, <c>/*! … */</c>, <c>//!</c>;</description></item>
        /// <item><description>everything after a plain comment opener to end of line.</description></item>
        /// </list>
        /// </summary>
        /// <param name="snapshot">Snapshot to scan.</param>
        /// <param name="lang">Detected buffer language.</param>
        /// <param name="ranges">Output list of comment ranges.</param>
        /// <remarks>
        /// <para>The walk maintains two pieces of cross-position state: whether we are
        /// currently inside a plain block comment (<c>/* … */</c> or F# <c>(* … *)</c>)
        /// and whether we are inside a double-quoted string (reset at each newline —
        /// multi-line verbatim/interpolated strings may confuse the heuristic, which is
        /// an accepted trade-off shared by comparable extensions).</para>
        /// <para>Nested F# block comments are treated as non-nested; the outer closer
        /// ends the region. VB.NET has no block comments.</para>
        /// </remarks>
        private static void CollectCommentRanges(
            ITextSnapshot snapshot, BufferLanguage lang, List<CommentRange> ranges)
        {
            bool isVb = lang == BufferLanguage.VBNet;
            bool isFSharp = lang == BufferLanguage.FSharp;

            bool inBlock = false;     // inside /* … */ or (* … *)
            bool inDocBlock = false;  // inside /** … */ or /*! … */ (skip silently)
            char close1 = '/';        // block closer first char  ('/' or '*')
            char close2 = '/';        // block closer second char
            int blockStartAbs = -1;   // buffer position of the active block opener

            int lineCount = snapshot.LineCount;
            for (int ln = 0; ln < lineCount; ln++)
            {
                var line = snapshot.GetLineFromLineNumber(ln);
                string t = line.GetText();
                int n = t.Length;
                bool inString = false;

                int i = 0;
                while (i < n)
                {
                    char c = t[i];

                    // ── Inside a block comment: only look for the closer ──────────
                    if (inBlock || inDocBlock)
                    {
                        if (i + 1 < n && t[i] == close1 && t[i + 1] == close2)
                        {
                            if (inBlock)
                                ranges.Add(new CommentRange(
                                    blockStartAbs,
                                    line.Start.Position + i + 2));
                            inBlock = false;
                            inDocBlock = false;
                            i += 2;
                            continue;
                        }
                        i++;
                        continue;
                    }

                    // ── String literal toggle (double quotes, escape-aware) ───────
                    if (c == '"')
                    {
                        if (!(inString && i > 0 && t[i - 1] == '\\'))
                            inString = !inString;
                        i++;
                        continue;
                    }
                    if (inString) { i++; continue; }

                    if (isVb)
                    {
                        // ── VB: apostrophe starts a comment (''' is XML-doc) ──────
                        if (c == '\'')
                        {
                            if (i + 2 < n && t[i + 1] == '\'' && t[i + 2] == '\'')
                                break; // doc comment — skip rest of line entirely
                            ranges.Add(new CommentRange(line.Start.Position + i,
                                                         line.End.Position));
                            break;     // rest of line consumed by the comment
                        }
                        i++;
                        continue;
                    }

                    // ── Slash languages: C#, F#, C++ ──────────────────────────────
                    if (c == '/' && i + 1 < n)
                    {
                        char d = t[i + 1];

                        if (d == '/')
                        {
                            bool isDoc = i + 2 < n && (t[i + 2] == '/' || t[i + 2] == '!');
                            if (isDoc) break;               // /// //// //! — skip line
                            ranges.Add(new CommentRange(line.Start.Position + i,
                                                         line.End.Position));
                            break;
                        }

                        if (d == '*')
                        {
                            bool isDoc = i + 2 < n && (t[i + 2] == '*' || t[i + 2] == '!');

                            if (isDoc)
                            {
                                inDocBlock = true; close1 = '*'; close2 = '/';
                            }
                            else
                            {
                                inBlock = true; close1 = '*'; close2 = '/';
                                blockStartAbs = line.Start.Position + i;
                            }
                            i += 2;
                            continue;
                        }
                    }

                    if (isFSharp && c == '(' && i + 1 < n && t[i + 1] == '*'
                        && !IsLinterAnnotation(t, i))
                    {
                        inBlock = true; close1 = '*'; close2 = ')';
                        blockStartAbs = line.Start.Position + i;
                        i += 2;
                        continue;
                    }

                    i++;
                }
            }
        }

        /// <summary>
        /// Distinguishes F# linter directives such as <c>(*$ ... *)</c> — kept out of
        /// badge consideration because their content is tooling metadata, not prose.
        /// </summary>
        private static bool IsLinterAnnotation(string t, int openParenIndex)
        {
            int j = openParenIndex + 2;
            return j < t.Length && t[j] == '$';
        }

        // ── Pill construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds a single stadium-shaped (fully rounded) clickable pill that
        /// replaces the tag token in the comment text.
        /// </summary>
        /// <param name="canonicalName">Canonical tag name (e.g., <c>"TODO"</c>).</param>
        /// <param name="tooltipTail">Comment text following the tag.</param>
        /// <param name="trimmedLineText">Dismissal key captured at build time.</param>
        /// <returns>The pill element.</returns>
        private UIElement CreatePill(
            string canonicalName, string tooltipTail, string trimmedLineText)
        {
            var opts = RenderDocOptions.Instance;

            // Editor-matched typography at ~85 % so the pill reads as annotation.
            var fontFamily = new FontFamily(opts.EffectiveFontFamily);
            double baseSize = 13.0;
            try
            {
                var tp = _view.FormattedLineSource?.DefaultTextProperties;
                if (tp != null && tp.FontRenderingEmSize > 0)
                    baseSize = tp.FontRenderingEmSize;
            }
            catch { }
            double fontSize = baseSize * 0.85;

            // Never exceed the current line box — keeps line heights stable.
            double maxH = _view.LineHeight > 0 ? _view.LineHeight : fontSize * 1.5;

            Color bg = opts.EffectiveTagColor(canonicalName);

            var fgBrush = new SolidColorBrush(TagBadgeCatalog.GetAdaptiveForeground(bg));
            fgBrush.Freeze();
            var bgBrush = new SolidColorBrush(bg);
            bgBrush.Freeze();

            var pill = new Border
            {
                Background = bgBrush,
                BorderBrush = TagBadgeCatalog.GetAdaptiveBorderBrush(bg),
                BorderThickness = new Thickness(1),
                // Oversized uniform radius is normalised by WPF into a perfect
                // stadium shape regardless of the final rendered height.
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(2, 0, 2, 0),
                MaxHeight = maxH,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = canonicalName,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Foreground = fgBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            string desc = TagBadgeCatalog.GetDescription(canonicalName);
            pill.ToolTip = string.IsNullOrEmpty(tooltipTail)
                ? $"{canonicalName} — {desc}"
                : $"{canonicalName} — {desc}\n{tooltipTail}";

            pill.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                TagBadgeToggleState.Toggle(trimmedLineText);
                SettingsChangedBroadcast.RaiseSettingsChanged();
            };

            return pill;
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        /// <summary>Invalidates the cache and re-queries tags after any edit.</summary>
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = e.After;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
        }

        /// <summary>Clears cached state on view closure.</summary>
        private void OnViewClosed(object sender, EventArgs e)
        {
            _caretLine = -1;
            _cachedSnapshot = null;
            _cachedTags = null;
        }

        /// <summary>
        /// Caret-hide bookkeeping: re-queries the affected lines when the caret moves,
        /// letting <see cref="GetTags"/> suppress/restore pills on those lines.
        /// </summary>
        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            int newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (newLine == _caretLine) return;
            int old = _caretLine;
            _caretLine = newLine;

            var snap = _buffer.CurrentSnapshot;
            var cached = _cachedTags;

            void Invalidate(int ln)
            {
                if (ln < 0 || ln >= snap.LineCount) return;
                if (cached != null)
                {
                    foreach (var ts in cached)
                    {
                        int s = snap.GetLineNumberFromPosition(ts.Span.Start);
                        int en = snap.GetLineNumberFromPosition(ts.Span.End);
                        if (ln >= s && ln <= en)
                        {
                            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(ts.Span));
                            return;
                        }
                    }
                }
                var l = snap.GetLineFromLineNumber(ln);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snap, l.Start, l.LengthIncludingLineBreak)));
            }

            Invalidate(old);
            Invalidate(newLine);
        }

        /// <summary>
        /// Two-phase invalidation on settings changes: first clear (suppressing tags),
        /// then rebuild on a dispatcher callback — preventing stale-pill flashes.
        /// </summary>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _settingsGeneration);
            _cachedSnapshot = null;
            _cachedTags = null;

            var snap = _buffer.CurrentSnapshot;
            _forceEmpty = true;
            TagsChanged?.Invoke(this,
                new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));

            _view.VisualElement.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Normal,
                new Action(() =>
                {
                    _forceEmpty = false;
                    var snap2 = _buffer.CurrentSnapshot;
                    TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                        new SnapshotSpan(snap2, 0, snap2.Length)));
                }));
        }

        /// <summary>Unsubscribes all events; called when the editor disposes the tagger.</summary>
        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
