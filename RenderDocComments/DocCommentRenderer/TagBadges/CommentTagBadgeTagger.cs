/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    CommentTagBadgeTagger.cs
 *  Purpose: Scans plain (non-doc) comments for conventional tag keywords
 *           (TODO, FIXME, HACK, …) and produces zero-width intra-text adornment
 *           tags that render coloured pills at the end of the tagged line.
 *
 *  Architecture Role:
 *    Implements ITagger<IntraTextAdornmentTag> — the same contract as the
 *    documentation-card tagger — but additive rather than replacing: the pill
 *    occupies no buffer space and never hides source text. Instantiated once per
 *    VIEW by CommentTagBadgeTaggerProvider; subscribed to buffer changes, view
 *    closure, and settings broadcasts. Caret movement is deliberately ignored —
 *    badges remain visible while the user edits inside a comment.
 *
 *  Detection Pipeline (per snapshot rebuild):
 *    1. CollectCommentRanges  — single char-walk over every line classifying
 *       plain line-comment and block-comment regions per language, skipping
 *       string literals (quote-parity heuristic) and doc-comment syntax
 *       (///, ''', /**, /*! , //!, (*$) which the card renderer owns.
 *    2. Tag regex             — compiled UPPERCASE-only alternation matched
 *       inside each collected range; WARNING normalises onto WARN.
 *    3. Per-line grouping     — matches grouped by containing line, deduped by
 *       canonical name, filtered by Premium enable-flags and dismissal state.
 *    4. Pill construction     — one horizontal StackPanel of Border pills per
 *       line; adaptive label contrast from TagBadgeCatalog; click toggles
 *       dismissal via TagBadgeToggleState + settings broadcast.
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
 *    • Pill styling changes — CreateBadgeStack.
 *    • Dismissal behaves oddly after edits — see TagBadgeToggleState key choice.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// Produces end-of-line badge adornments for plain comments containing
    /// conventional tag keywords across C#, VB.NET, F#, and C++ buffers.
    /// </summary>
    /// <remarks>
    /// <para>The tagger emits <b>zero-width</b> <see cref="IntraTextAdornmentTag"/>s
    /// positioned immediately after the last non-whitespace character of each tagged
    /// line, so pills occupy empty space and never obscure code or comment text.</para>
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

        /// <summary>
        /// Raised when the set of badges changes so the editor re-queries tags.
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Creates a tagger bound to a specific view/buffer pair and subscribes to
        /// buffer changes, view closure, and settings broadcasts.
        /// </summary>
        /// <param name="buffer">The text buffer to scan.</param>
        /// <param name="view">The WPF view hosting rendered pills (font metrics source).</param>
        public CommentTagBadgeTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Yields badge adornment tags intersecting the requested spans after applying
        /// master-toggle, per-file-override, force-empty, and intersection filters.
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
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tag.Span)))
                    yield return tag;
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
        /// Single full pass: collect comment ranges, match tags, group per line,
        /// and build one pill-stack adornment per qualifying line.
        /// </summary>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;

            var lang = GetLanguage(_buffer);
            var ranges = new List<CommentRange>();
            CollectCommentRanges(snapshot, lang, ranges);

            // Accumulate ordered unique canonical names + tooltip tails per line.
            var namesByLine = new Dictionary<int, List<string>>();
            var tailByLine = new Dictionary<int, string>();

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

                    int absPos = range.Start + m.Index;
                    int lineNo = snapshot.GetLineNumberFromPosition(absPos);

                    if (!namesByLine.TryGetValue(lineNo, out var names))
                        namesByLine[lineNo] = names = new List<string>();

                    if (!names.Contains(canonical))
                        names.Add(canonical);

                    if (!tailByLine.ContainsKey(lineNo))
                        tailByLine[lineNo] = ExtractTail(text, m.Index + m.Length);
                }
            }

            if (namesByLine.Count == 0) return result;

            foreach (var kvp in namesByLine)
            {
                var line = snapshot.GetLineFromLineNumber(kvp.Key);
                var lineText = line.GetText();
                var trimmed = lineText.Trim();

                // Respect click-dismissal (text-keyed; survives edits above).
                if (TagBadgeToggleState.IsHidden(trimmed)) continue;

                var panel = CreateBadgeStack(kvp.Value, tailByLine[kvp.Key], trimmed);
                if (panel == null) continue;

                // Zero-width span just past the last non-whitespace character so the
                // pill lands in free space even when the line has trailing padding.
                int anchor = LastNonWhitespaceEnd(lineText, line.Start);

                var span = new SnapshotSpan(snapshot, anchor, 0);
                var tag = new IntraTextAdornmentTag(panel, null, PositionAffinity.Predecessor);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
            }

            return result;
        }

        /// <summary>
        /// Computes the buffer position directly after the final non-whitespace
        /// character of <paramref name="lineText"/>, falling back to the raw line end.
        /// </summary>
        private static int LastNonWhitespaceEnd(string lineText, int lineStart)
        {
            int end = lineStart + lineText.Length;
            for (int i = lineText.Length - 1; i >= 0; i--)
            {
                if (!char.IsWhiteSpace(lineText[i]))
                {
                    end = lineStart + i + 1;
                    break;
                }
            }
            return end;
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
        /// Builds a horizontal stack of clickable pills, one per unique canonical tag.
        /// </summary>
        /// <param name="canonicalNames">Ordered unique tag names for the line.</param>
        /// <param name="tooltipTail">Comment text following the (first) tag.</param>
        /// <param name="trimmedLineText">Dismissal key captured at build time.</param>
        /// <returns>The stacked element, or null when nothing should render.</returns>
        private UIElement CreateBadgeStack(
            List<string> canonicalNames, string tooltipTail, string trimmedLineText)
        {
            if (canonicalNames == null || canonicalNames.Count == 0) return null;

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

            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                MaxHeight = maxH,
                IsHitTestVisible = true,
            };
            TextElement.SetFontFamily(panel, fontFamily);
            TextElement.SetFontSize(panel, fontSize);

            foreach (var name in canonicalNames)
            {
                Color bg = opts.EffectiveTagColor(name);

                var fgBrush = new SolidColorBrush(TagBadgeCatalog.GetAdaptiveForeground(bg));
                fgBrush.Freeze();
                var bgBrush = new SolidColorBrush(bg);
                bgBrush.Freeze();

                var label = new TextBlock
                {
                    Text = name,
                    Foreground = fgBrush,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                var pill = new Border
                {
                    Background = bgBrush,
                    BorderBrush = TagBadgeCatalog.GetAdaptiveBorderBrush(bg),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(6, 0, 0, 0),
                    Cursor = Cursors.Hand,
                    Child = label,
                    VerticalAlignment = VerticalAlignment.Center,
                };

                string desc = TagBadgeCatalog.GetDescription(name);
                pill.ToolTip = string.IsNullOrEmpty(tooltipTail)
                    ? $"{name} — {desc}"
                    : $"{name} — {desc}\n{tooltipTail}";

                pill.MouseLeftButtonUp += (s, e) =>
                {
                    e.Handled = true;
                    TagBadgeToggleState.Toggle(trimmedLineText);
                    SettingsChangedBroadcast.RaiseSettingsChanged();
                };

                panel.Children.Add(pill);
            }

            return panel;
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
            _cachedSnapshot = null;
            _cachedTags = null;
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
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
