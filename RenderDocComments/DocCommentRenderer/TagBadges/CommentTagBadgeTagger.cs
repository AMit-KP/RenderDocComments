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
 *    1. CommentTagLocator     — shared detection pipeline (TagScan/): comment-
 *       range walk (single char-pass per line, skipping string literals via the
 *       quote-parity heuristic and doc-comment syntax ///, ''', /**, /*! , //!,
 *       (*$) owned by the card renderer), anchored UPPERCASE-only alternation,
 *       WARNING→WARN normalisation, and tooltip-tail extraction.
 *    2. Pill construction     — one stadium pill per match, spanning the token
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
 *    • TagScan/CommentTagLocator.cs — shared tag detection pipeline.
 *    • TagBadgeCatalog.cs          — definitions, colours, contrast helpers.
 *    • TagBadgeToggleState.cs      — click-dismissal store.
 *    • RenderDocOptions.cs         — TagBadgesEnabled, EffectiveTag* gating.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Badges appear on non-comments / miss comments — fix CommentTagLocator's
 *      CollectCommentRanges (shared with the card/box/tick features).
 *    • A tag is not recognised — extend CommentTagLocator's regex and
 *      TagBadgeCatalog.Tags together.
 *    • Pill styling changes — CreatePill.
 *    • Dismissal behaves oddly after edits — see TagBadgeToggleState key choice.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using RenderDocComments.DocCommentRenderer.TagScan;
using RenderDocComments.Options;

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
        private readonly System.Windows.Threading.DispatcherTimer _debounceTimer;

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

            _debounceTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _debounceTimer.Tick += (s, ev) =>
            {
                _debounceTimer.Stop();
                _cachedSnapshot = null;
                _cachedTags = null;
                var snap = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
            };

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
            if (RenderDocOptions.Instance.EffectiveTagStyle != "Pills") yield break;

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

        // ── BuildTags ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Single full pass: collect comment ranges (shared locator), match tags,
        /// and emit one in-place pill per eligible match — spanning the token (plus
        /// an immediately following colon) so the editor collapses the word and
        /// renders the pill exactly where it stood.
        /// </summary>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;

            var lang = CommentTagLocator.GetLanguage(_buffer);
            var ranges = new List<CommentTagLocator.CommentRange>();
            CommentTagLocator.CollectCommentRanges(snapshot, lang, ranges);

            foreach (var range in ranges)
            {
                int len = range.End - range.Start;
                if (len <= 0) continue;
                string text;
                try { text = snapshot.GetText(range.Start, len); }
                catch { continue; }

                // Multi-line ranges (block comments) are owned by the comment-box
                // feature — a pill inside a boxed block would overlap its adornment.
                if (snapshot.GetLineNumberFromPosition(range.Start) !=
                    snapshot.GetLineNumberFromPosition(range.End - 1))
                    continue;

                foreach (var m in CommentTagLocator.Locate(text, range.PrefixLen))
                {
                    if (!opts.EffectiveTagEnabled(m.CanonicalTag)) continue;

                    var line = snapshot.GetLineFromLineNumber(
                        snapshot.GetLineNumberFromPosition(range.Start + m.MatchIndex));
                    var trimmedLine = line.GetText().Trim();

                    // Respect click-dismissal (text-keyed; survives edits above).
                    if (TagBadgeToggleState.IsHidden(trimmedLine)) continue;

                    // Swallow an immediately following colon: "// TODO: fix" →
                    // "// [pill] fix" rather than "// [pill]: fix".
                    int tokenLen = m.MatchLength;
                    if (m.MatchIndex + tokenLen < text.Length && text[m.MatchIndex + tokenLen] == ':')
                        tokenLen++;

                    var panel = CreatePill(m.CanonicalTag, m.TailText, trimmedLine);
                    if (panel == null) continue;

                    int start = range.Start + m.MatchIndex;
                    var span = new SnapshotSpan(snapshot, start, tokenLen);
                    var tag = new IntraTextAdornmentTag(panel, null, PositionAffinity.Predecessor);
                    result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
                }
            }

            return result;
        }

        // ── Pill construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the font family for tag visuals: the Premium tag font when
        /// configured, otherwise the editor's current typeface.
        /// </summary>
        private FontFamily ResolveTagFontFamily()
        {
            string fam = RenderDocOptions.Instance.EffectiveTagFontFamily;
            if (!string.IsNullOrWhiteSpace(fam))
            {
                try { return new FontFamily(fam); }
                catch { }
            }

            FontFamily editor = null;
            try
            {
                var tp = _view.FormattedLineSource?.DefaultTextProperties;
                if (tp != null) editor = tp.Typeface.FontFamily;
            }
            catch { }
            if (editor != null && !string.IsNullOrEmpty(editor.Source)) return editor;
            return new FontFamily("Consolas");
        }

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
            var fontFamily = ResolveTagFontFamily();
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

            // Softly rounded ends — noticeably pill-like without bulging into ovals.
            double cornerRadius = Math.Min(6.0, Math.Max(3.0, fontSize * 0.45));

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
                CornerRadius = new CornerRadius(cornerRadius),
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
                    FontWeight = FontWeights.SemiBold,
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
            if (!RenderDocOptions.Instance.TagBadgesEnabled)
            {
                _cachedSnapshot = null;
                _cachedTags = null;
                return;
            }

            var snap = e.After;
            if (_cachedTags != null)
            {
                var translated = new List<TagSpan<IntraTextAdornmentTag>>(_cachedTags.Count);
                foreach (var tag in _cachedTags)
                {
                    var newSpan = tag.Span.TranslateTo(snap, SpanTrackingMode.EdgeExclusive);
                    if (newSpan.Length > 0)
                        translated.Add(new TagSpan<IntraTextAdornmentTag>(newSpan, tag.Tag));
                }
                _cachedTags = translated;
                _cachedSnapshot = snap;
            }

            bool touchesComments = false;
            foreach (var change in e.Changes)
            {
                if (change.OldText.Contains("//") || change.NewText.Contains("//") ||
                    change.OldText.Contains("/*") || change.NewText.Contains("/*") ||
                    change.OldText.Contains("*/") || change.NewText.Contains("*/") ||
                    change.OldText.Contains("'")  || change.NewText.Contains("'"))
                {
                    touchesComments = true;
                    break;
                }
                if (change.NewPosition < snap.Length)
                {
                    var line = snap.GetLineFromPosition(change.NewPosition);
                    var t = line.GetText();
                    if (t.Contains("//") || t.Contains("/*") || t.Contains("*/") || t.Contains("'"))
                    {
                        touchesComments = true;
                        break;
                    }
                }
                if (change.OldPosition < e.Before.Length)
                {
                    var line = e.Before.GetLineFromPosition(change.OldPosition);
                    var t = line.GetText();
                    if (t.Contains("//") || t.Contains("/*") || t.Contains("*/") || t.Contains("'"))
                    {
                        touchesComments = true;
                        break;
                    }
                }
            }

            if (!touchesComments) return;

            _debounceTimer.Stop();
            _debounceTimer.Start();
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

            _ = RenderDocCommentsPackage.SharedJoinableTaskFactory.RunAsync(async () =>
            {
                await RenderDocCommentsPackage.SharedJoinableTaskFactory
                    .WithPriority(_view.VisualElement.Dispatcher,
                        System.Windows.Threading.DispatcherPriority.Normal)
                    .SwitchToMainThreadAsync();

                _forceEmpty = false;
                var snap2 = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snap2, 0, snap2.Length)));
            });
        }

        /// <summary>Unsubscribes all events; called when the editor disposes the tagger.</summary>
        public void Dispose()
        {
            _debounceTimer?.Stop();
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
