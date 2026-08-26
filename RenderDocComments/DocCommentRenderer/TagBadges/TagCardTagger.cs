/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    TagCardTagger.cs
 *  Purpose: Scans plain (non-doc) comments for conventional tag keywords
 *           (TODO, FIXME, HACK, …) and replaces the tagged comment region with a
 *           compact rendered card — chip(s) + description — using full-span
 *           intra-text adornment tags.
 *
 *  Architecture Role:
 *    Implements ITagger<IntraTextAdornmentTag>, exploiting the editor's native
 *    behaviour of collapsing source text beneath an adornment whose tag SPAN is
 *    non-empty — identical mechanism to DocCommentAdornmentTagger. The span covers
 *    ONLY the comment portion of the line (never preceding code), and ends at the
 *    block closer when one shares the line, so surrounding code always survives.
 *
 *  Visibility Model:
 *    Caret-based hide (matching free-tier doc-card behaviour): when the caret
 *    enters the collapsed line the raw comment reappears for editing; moving away
 *    re-renders the card. There is deliberately no click-dismissal here.
 *
 *  Detection Pipeline:
 *    Identical scanner to the pill design — CollectCommentRanges walks every line
 *    once, skipping strings (quote-parity heuristic) and doc syntaxes
 *    (///, ''', /**, /*! , //!, (*$); matches come from a compiled UPPERCASE-only
 *    alternation; WARNING normalises onto WARN.
 *
 *  Key Classes:
 *    TagCardTagger — ITagger implementation with snapshot cache, caret tracking,
 *                    and two-phase settings rebuilds.
 *
 *  Dependencies:
 *    • TagBadgeCatalog.cs          — definitions, colours, contrast helpers.
 *    • RenderDocOptions.cs         — TagBadgesEnabled, EffectiveTag*, widths.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Cards appear on non-comments / miss comments — fix CollectCommentRanges.
 *    • Card styling — CreateCard.
 *    • Visibility quirks around the caret — GetTags / OnCaretPositionChanged.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderDocComments.DocCommentRenderer.TagBadges
{
    /// <summary>
    /// Replaces plain comments containing conventional tag keywords with compact
    /// rendered cards across C#, VB.NET, F#, and C++ buffers.
    /// </summary>
    /// <remarks>
    /// <para><b>Caching:</b> results are cached per snapshot and invalidated by a
    /// static settings-generation counter bumped on every
    /// <see cref="SettingsChangedBroadcast.SettingsChanged"/>. A two-phase
    /// clear/rebuild prevents stale-card flashes during transitions.</para>
    /// <para><b>Sizing:</b> cards are capped by
    /// <see cref="RenderDocOptions.EffectiveWidth"/>, honouring the Premium fixed
    /// width option exactly as doc-comment cards do.</para>
    /// </remarks>
    internal sealed class TagCardTagger
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
        /// Raised when the set of cards changes so the editor re-queries tags.
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Creates a tagger bound to a specific view/buffer pair and subscribes to
        /// buffer changes, caret movement, layout updates, view closure, and
        /// settings broadcasts.
        /// </summary>
        /// <param name="buffer">The text buffer to scan.</param>
        /// <param name="view">The WPF view hosting rendered cards.</param>
        public TagCardTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Yields card tags intersecting the requested spans after applying
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
                // Caret-based hide: raw text returns while the user edits the line.
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

        /// <summary>
        /// Maximum rendered description lines before trimming kicks in.
        /// </summary>
        private const double MaxDescriptionLines = 3.0;

        // ── BuildTags ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Single full pass: collect comment ranges, match tags, group matches by
        /// containing line, and emit one card per qualifying comment region.
        /// </summary>
        /// <remarks>
        /// <para><b>Span safety rules:</b></para>
        /// <list type="bullet">
        /// <item><description>The span starts at the comment opener — code before an
        /// inline comment (<c>int x; // TODO</c>) remains visible.</description></item>
        /// <item><description>The span ends at the range end, which is the block closer
        /// for closed blocks — code after <c>*/</c> on the same line remains visible.</description></item>
        /// <item><description>In multi-line blocks, only the line containing the match
        /// collapses; sibling lines stay raw.</description></item>
        /// </list>
        /// </remarks>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;

            var lang = GetLanguage(_buffer);
            var ranges = new List<CommentRange>();
            CollectCommentRanges(snapshot, lang, ranges);

            // Per-line accumulation: ordered unique canonical names + tooltip tail +
            // the outermost span covering all matches on that line.
            var namesByLine = new Dictionary<int, List<string>>();
            var tailByLine = new Dictionary<int, string>();
            var spanByLine = new Dictionary<int, (int start, int end)>();

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
                    var line = snapshot.GetLineFromLineNumber(lineNo);

                    if (!namesByLine.TryGetValue(lineNo, out var names))
                        namesByLine[lineNo] = names = new List<string>();
                    if (!names.Contains(canonical))
                        names.Add(canonical);

                    if (!tailByLine.ContainsKey(lineNo))
                        tailByLine[lineNo] = ExtractTail(text, m.Index + m.Length);

                    // Collapse only the comment region on this line.
                    int s = Math.Max(range.Start, line.Start.Position);
                    int e = Math.Min(range.End, line.End.Position);
                    if (spanByLine.TryGetValue(lineNo, out var cur))
                        spanByLine[lineNo] = (Math.Min(cur.start, s), Math.Max(cur.end, e));
                    else
                        spanByLine[lineNo] = (s, e);
                }
            }

            foreach (var kvp in spanByLine)
            {
                var (s, e) = kvp.Value;
                if (e <= s) continue;

                var panel = CreateCard(namesByLine[kvp.Key], tailByLine[kvp.Key]);
                if (panel == null) continue;

                var span = new SnapshotSpan(snapshot, s, e - s);
                var tag = new IntraTextAdornmentTag(panel, null, PositionAffinity.Predecessor);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
            }

            return result;
        }

        /// <summary>
        /// Extracts the human-readable remainder of a comment following a matched tag,
        /// trimming an optional colon and whitespace, capped at
        /// <see cref="MaxTooltipTailLength"/> characters.
        /// </summary>
        private static string ExtractTail(string rangeText, int tailStart)
        {
            if (tailStart >= rangeText.Length) return string.Empty;
            var tail = rangeText.Substring(tailStart).TrimStart(':', ' ', '\t');
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
        /// <item><description>doc-comment syntaxes owned by the doc renderer:
        ///   <c>///</c>, <c>'''</c>, <c>/** … */</c>, <c>/*! … */</c>, <c>//!</c>;</description></item>
        /// <item><description>everything after a plain comment opener to end of line.</description></item>
        /// </list>
        /// Block ranges terminate at their closer, so trailing code on the closing
        /// line is never collapsed.
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
        /// card consideration because their content is tooling metadata, not prose.
        /// </summary>
        private static bool IsLinterAnnotation(string t, int openParenIndex)
        {
            int j = openParenIndex + 2;
            return j < t.Length && t[j] == '$';
        }

        // ── Card construction ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the card visual: theme-matched rounded container, coloured accent
        /// bar, one chip per unique tag, and a wrapping (≤3-line) description.
        /// </summary>
        /// <param name="canonicalNames">Ordered unique tag names for the line.</param>
        /// <param name="tooltipTail">Comment text following the (first) tag.</param>
        /// <returns>The card element, or null when nothing should render.</returns>
        private UIElement CreateCard(List<string> canonicalNames, string tooltipTail)
        {
            if (canonicalNames == null || canonicalNames.Count == 0) return null;
            var opts = RenderDocOptions.Instance;

            // ── Theme colours from VS format map ─────────────────────────────────
            Brush themeFg = new SolidColorBrush(Color.FromRgb(212, 212, 212));
            Brush themeBg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            try
            {
                var formatMap = _view.Properties.GetProperty<IEditorFormatMap>(
                    typeof(IEditorFormatMap));
                if (formatMap != null)
                {
                    var bgProps = formatMap.GetProperties("TextView Background");
                    if (bgProps != null &&
                        bgProps.Contains(EditorFormatDefinition.BackgroundBrushId))
                        themeBg = (Brush)bgProps[EditorFormatDefinition.BackgroundBrushId];

                    var fgProps = formatMap.GetProperties("Plain Text");
                    if (fgProps != null &&
                        fgProps.Contains(EditorFormatDefinition.ForegroundBrushId))
                        themeFg = (Brush)fgProps[EditorFormatDefinition.ForegroundBrushId];
                }
            }
            catch { }

            // ── Font ─────────────────────────────────────────────────────────────
            var fontFamily = new FontFamily(opts.EffectiveFontFamily);
            double fontSize = 13.0;
            try
            {
                var tp = _view.FormattedLineSource?.DefaultTextProperties;
                if (tp != null && tp.FontRenderingEmSize > 0)
                    fontSize = tp.FontRenderingEmSize;
            }
            catch { }
            double chipFontSize = fontSize * 0.85;

            // ── Width budget ─────────────────────────────────────────────────────
            double indent = MeasureIndent();
            double maxW = opts.EffectiveWidth(_view.ViewportWidth, indent);
            double lineHeight = _view.LineHeight > 0 ? _view.LineHeight : fontSize * 1.4;

            // ── Accent bar (first tag's colour) ──────────────────────────────────
            Color accentColor = opts.EffectiveTagColor(canonicalNames[0]);
            var accentBrush = new SolidColorBrush(accentColor);
            accentBrush.Freeze();

            var bar = new Border
            {
                Width = 3,
                CornerRadius = new CornerRadius(2),
                Background = accentBrush,
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Stretch,
            };

            // ── Chips ────────────────────────────────────────────────────────────
            var chips = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
            };
            foreach (var name in canonicalNames)
            {
                Color bg = opts.EffectiveTagColor(name);
                var fgBrush = new SolidColorBrush(TagBadgeCatalog.GetAdaptiveForeground(bg));
                fgBrush.Freeze();
                var bgBrush = new SolidColorBrush(bg);
                bgBrush.Freeze();

                chips.Children.Add(new Border
                {
                    Background = bgBrush,
                    CornerRadius = new CornerRadius(3),
                    Padding = new Thickness(5, 0, 5, 0),
                    Margin = new Thickness(0, 0, 7, 0),
                    Child = new TextBlock
                    {
                        Text = name,
                        Foreground = fgBrush,
                        FontSize = chipFontSize,
                        VerticalAlignment = VerticalAlignment.Center,
                    },
                });
            }

            // ── Description ──────────────────────────────────────────────────────
            var desc = new TextBlock
            {
                Text = string.IsNullOrEmpty(tooltipTail)
                    ? TagBadgeCatalog.GetDescription(canonicalNames[0])
                    : tooltipTail,
                Foreground = themeFg,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = MaxDescriptionLines * lineHeight,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(bar);
            row.Children.Add(chips);
            row.Children.Add(desc);

            // Hairline outline derived from the theme foreground (adapts to light/dark).
            Color borderColor = themeFg is SolidColorBrush scb
                ? scb.Color
                : Color.FromRgb(0x80, 0x80, 0x80);
            var borderBrush = new SolidColorBrush(borderColor);
            borderBrush.Opacity = 0.25;

            var card = new Border
            {
                Background = themeBg,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 3, 8, 3),
                Margin = new Thickness(0, 1, 0, 1),
                MaxWidth = maxW,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = row,
            };

            // Tooltip: meanings + full comment text.
            string tip = string.Join(", ", canonicalNames.Select(n =>
                $"{n} — {TagBadgeCatalog.GetDescription(n)}"));
            card.ToolTip = string.IsNullOrEmpty(tooltipTail) ? tip : $"{tip}\n{tooltipTail}";

            return card;
        }

        /// <summary>
        /// Calculates the indentation width (pixels) of the current caret line —
        /// matching the heuristic used by the doc-card tagger.
        /// </summary>
        private double MeasureIndent()
        {
            int spaces = 0;
            try
            {
                var lineText = _view.Caret.Position.BufferPosition
                    .GetContainingLine().GetText();
                foreach (char c in lineText)
                {
                    if (c == ' ') { spaces++; continue; }
                    if (c == '\t') { spaces += 4; continue; }
                    break;
                }
            }
            catch { }

            try
            {
                var cw = _view.FormattedLineSource?.ColumnWidth;
                if (cw.HasValue && cw.Value > 0) return spaces * cw.Value;
            }
            catch { }
            return spaces * 7.2;
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

        /// <summary>
        /// Invalidates the cache when viewport dimensions change — card widths adapt
        /// to the viewport unless Premium fixed-width mode makes them irrelevant.
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            bool widthChanged = e.NewViewState.ViewportWidth != e.OldViewState.ViewportWidth;
            bool heightChanged = e.NewViewState.ViewportHeight != e.OldViewState.ViewportHeight;

            if (widthChanged && RenderDocOptions.Instance.EffectiveUseFixedWidth)
                widthChanged = false;

            if (widthChanged || heightChanged)
            {
                _cachedSnapshot = null;
                _cachedTags = null;
                var snap = _buffer.CurrentSnapshot;
                TagsChanged?.Invoke(this,
                    new SnapshotSpanEventArgs(new SnapshotSpan(snap, 0, snap.Length)));
            }
        }

        /// <summary>Clears caret tracking and cached state on view closure.</summary>
        private void OnViewClosed(object sender, EventArgs e)
        {
            _caretLine = -1;
            _cachedSnapshot = null;
            _cachedTags = null;
        }

        /// <summary>
        /// Caret-hide bookkeeping: re-queries the affected lines when the caret moves,
        /// letting <see cref="GetTags"/> suppress/restore the collapsed card.
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
        /// then rebuild on a dispatcher callback — preventing stale-card flashes.
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
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
