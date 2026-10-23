/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    CommentBoxTagger.cs
 *  Purpose: Scans plain (non-doc) comments — inline/trailing line comments, full
 *           line-comment blocks, and block comments — and replaces each comment
 *           region with a thin bordered box containing the cleaned comment text.
 *
 *  Architecture Role:
 *    Implements ITagger<IntraTextAdornmentTag> — the same contract as the doc-card
 *    and badge taggers. Each comment region's SPAN is collapsed by the editor and
 *    the box renders exactly where the comment stood; code before an inline
 *    comment (and after a block closer) always survives. Instantiated once per
 *    VIEW by CommentBoxTaggerProvider; subscribed to buffer changes, caret
 *    movement, layout updates, view closure, and settings broadcasts.
 *
 *  Detection Pipeline (per snapshot rebuild):
 *    1. CollectRawRanges — single char-walk over every line classifying plain
 *       line-comment and block-comment regions per language, skipping string
 *       literals (quote-parity heuristic) and doc-comment syntax (///, ''',
 *       /**, /*! , //!, (*$) which the card renderer owns. Each range records
 *       whether the comment is a full-line comment (only whitespace before the
 *       opener) and its opener/closer tokens.
 *    2. Tag-aware skip    — ranges containing renderable tag keywords (TODO,
 *       FIXME, … per the active pill/card style's eligibility rules) are left
 *       to the comment-tag feature and act as grouping barriers, so a box and
 *       a pill/card never overlap.
 *    3. Grouping          — remaining consecutive full-line line comments on
 *       adjacent lines merge into one box; trailing (inline) comments and block
 *       comments always stand alone so surrounding code is never collapsed.
 *    4. Box construction — comment opener/closer/decorator tokens are stripped
 *       per line and the cleaned lines render inside a 1px-bordered Border whose
 *       border + text default to the theme's comment colour (customisable), and
 *       whose background always follows the editor background.
 *
 *  Colour / Font Model:
 *    Default: border and text use the IDE's current "Comment" classification
 *    colour; background = editor background ("TextView Background"); font =
 *    editor font. Premium can override border/text colours
 *    (CommentBoxThemeColors = false) and set a custom font family.
 *
 *  Visibility Model:
 *    Caret-based hide at line granularity: when the caret is on a boxed comment's
 *    line the raw comment reappears for editing; moving away re-renders the box.
 *
 *  Key Classes:
 *    CommentBoxTagger — ITagger implementation with snapshot cache.
 *
 *  Dependencies:
 *    • RenderDocOptions.cs         — EffectiveCommentBoxesEnabled, EffectiveCommentBox*.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Boxes appear on non-comments / miss comments — fix CollectRawRanges.
 *    • Box styling changes — CreateBox.
 *    • Grouping of consecutive comment lines — BuildTags.
 *    • Visibility quirks around the caret — GetTags / OnCaretPositionChanged.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using RenderDocComments.DocCommentRenderer.TagBadges;
using RenderDocComments.Options;

namespace RenderDocComments.DocCommentRenderer.CommentBoxes
{
    /// <summary>
    /// Replaces plain (non-doc) comments with thin bordered boxes across C#,
    /// VB.NET, F#, and C++ buffers.
    /// </summary>
    /// <remarks>
    /// <para><b>In-place replacement:</b> the whole comment region (from opener to
    /// closer — never the code before an inline comment) is collapsed and the box
    /// renders exactly where the comment stood. Consecutive full-line
    /// <c>//</c>-style comments on adjacent lines merge into a single box.</para>
    /// <para><b>Caching:</b> identical strategy to the other adornment taggers —
    /// results are cached per snapshot and invalidated by a static
    /// settings-generation counter that is bumped whenever
    /// <see cref="SettingsChangedBroadcast.SettingsChanged"/> fires. A two-phase
    /// clear/rebuild prevents stale-box flashes during transitions.</para>
    /// </remarks>
    internal sealed class CommentBoxTagger
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
        /// Raised when the set of boxes changes so the editor re-queries tags.
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Creates a tagger bound to a specific view/buffer pair and subscribes to
        /// buffer changes, caret movement, layout updates, view closure, and
        /// settings broadcasts.
        /// </summary>
        /// <param name="buffer">The text buffer to scan.</param>
        /// <param name="view">The WPF view hosting rendered boxes (theme/font source).</param>
        public CommentBoxTagger(ITextBuffer buffer, IWpfTextView view)
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
        /// Yields box tags intersecting the requested spans after applying the
        /// premium-gated master toggle, per-file-override, force-empty, caret-hide,
        /// and intersection filters.
        /// </summary>
        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            // Box construction touches WPF visuals — main-thread-affine.
            ThreadHelper.ThrowIfNotOnUIThread();

            if (spans.Count == 0) yield break;
            if (_forceEmpty) yield break;
            if (!RenderDocOptions.Instance.EffectiveCommentBoxesEnabled) yield break;

            if (_buffer.Properties.TryGetProperty("RenderDocComments_Disabled", out bool disabled) && disabled)
                yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);

            foreach (var tag in tags)
            {
                // Caret-based hide: the raw comment returns while the user edits its line.
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

        /// <summary>Supported buffer languages (mirrors the other taggers' mapping).</summary>
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

        // ── Raw comment ranges ────────────────────────────────────────────────────

        /// <summary>
        /// One contiguous plain-comment region in buffer coordinates.<br/>
        /// <see cref="IsFullLine"/> is meaningful only for line comments and records
        /// whether everything before the opener on its start line is whitespace
        /// (full-line comments may merge with neighbours; trailing ones may not).
        /// <see cref="HasTag"/> marks ranges the comment-tag feature will render
        /// (pill/card) — they are never boxed and act as grouping barriers.
        /// </summary>
        private struct RawRange
        {
            public int Start;
            public int End;
            public int StartLine;
            public int EndLine;
            public bool IsBlock;
            public bool IsFullLine;
            public bool HasTag;
            public string Opener;   // "//" or "'" or "/*" or "(*"
            public string Closer;   // "" (line) or "*/" or "*)"
        }

        /// <summary>
        /// Walks every line once, recording plain comment regions while skipping:
        /// <list type="bullet">
        /// <item><description>double-quoted string contents (quote-parity heuristic);</description></item>
        /// <item><description>doc-comment syntaxes owned by the doc renderer:
        ///   <c>///</c>, <c>'''</c>, <c>/** … */</c>, <c>/*! … */</c>, <c>//!</c>;</description></item>
        /// <item><description>everything after a plain comment opener to end of line.</description></item>
        /// </list>
        /// </summary>
        /// <param name="snapshot">Snapshot to scan.</param>
        /// <param name="lang">Detected buffer language.</param>
        /// <param name="ranges">Output list of raw ranges (ordered by position).</param>
        /// <remarks>
        /// <para>The walk maintains two pieces of cross-position state: whether we are
        /// currently inside a plain block comment (<c>/* … */</c> or F# <c>(* … *)</c>)
        /// and whether we are inside a double-quoted string (reset at each newline —
        /// multi-line verbatim/interpolated strings may confuse the heuristic, which is
        /// an accepted trade-off shared by comparable extensions).</para>
        /// <para>Nested F# block comments are treated as non-nested; the outer closer
        /// ends the region. VB.NET has no block comments. Unterminated block comments
        /// never emit a range (matching the badge scanner's behaviour).</para>
        /// </remarks>
        private static void CollectRawRanges(
            ITextSnapshot snapshot, BufferLanguage lang, List<RawRange> ranges)
        {
            bool isVb = lang == BufferLanguage.VBNet;
            bool isFSharp = lang == BufferLanguage.FSharp;

            bool inBlock = false;     // inside /* … */ or (* … *)
            bool inDocBlock = false;  // inside /** … */ or /*! … */ (skip silently)
            string closer = "*/";     // active block closer ("*/" or "*)")
            string opener = "/*";     // active block opener
            int blockStartAbs = -1;   // buffer position of the active block opener
            int blockStartLine = -1;  // line number of the active block opener

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
                        if (i + closer.Length <= n &&
                            string.CompareOrdinal(t, i, closer, 0, closer.Length) == 0)
                        {
                            if (inBlock)
                            {
                                ranges.Add(new RawRange
                                {
                                    Start = blockStartAbs,
                                    End = line.Start.Position + i + closer.Length,
                                    StartLine = blockStartLine,
                                    EndLine = ln,
                                    IsBlock = true,
                                    IsFullLine = false,
                                    Opener = opener,
                                    Closer = closer,
                                });
                            }
                            inBlock = false;
                            inDocBlock = false;
                            i += closer.Length;
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
                            ranges.Add(new RawRange
                            {
                                Start = line.Start.Position + i,
                                End = line.End.Position,
                                StartLine = ln,
                                EndLine = ln,
                                IsBlock = false,
                                IsFullLine = IsAllWhitespaceBefore(t, i),
                                Opener = "'",
                                Closer = string.Empty,
                            });
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
                            ranges.Add(new RawRange
                            {
                                Start = line.Start.Position + i,
                                End = line.End.Position,
                                StartLine = ln,
                                EndLine = ln,
                                IsBlock = false,
                                IsFullLine = IsAllWhitespaceBefore(t, i),
                                Opener = "//",
                                Closer = string.Empty,
                            });
                            break;
                        }

                        if (d == '*')
                        {
                            bool isDoc = i + 2 < n && (t[i + 2] == '*' || t[i + 2] == '!');

                            if (isDoc)
                            {
                                inDocBlock = true; closer = "*/";
                            }
                            else
                            {
                                inBlock = true; closer = "*/"; opener = "/*";
                                blockStartAbs = line.Start.Position + i;
                                blockStartLine = ln;
                            }
                            i += 2;
                            continue;
                        }
                    }

                    if (isFSharp && c == '(' && i + 1 < n && t[i + 1] == '*'
                        && !IsLinterAnnotation(t, i))
                    {
                        inBlock = true; closer = "*)"; opener = "(*";
                        blockStartAbs = line.Start.Position + i;
                        blockStartLine = ln;
                        i += 2;
                        continue;
                    }

                    i++;
                }
            }
        }

        /// <summary>
        /// Determines whether every character before <paramref name="index"/> in the
        /// line is whitespace — i.e. the comment opener starts the line's content.
        /// </summary>
        private static bool IsAllWhitespaceBefore(string lineText, int index)
        {
            for (int k = 0; k < index; k++)
            {
                if (!char.IsWhiteSpace(lineText[k])) return false;
            }
            return true;
        }

        /// <summary>
        /// Distinguishes F# linter directives such as <c>(*$ … *)</c> — kept out of
        /// box consideration because their content is tooling metadata, not prose.
        /// </summary>
        private static bool IsLinterAnnotation(string t, int openParenIndex)
        {
            int j = openParenIndex + 2;
            return j < t.Length && t[j] == '$';
        }

        // ── Tag-aware skipping ────────────────────────────────────────────────────

        /// <summary>
        /// UPPERCASE-only tag alternation — identical to the one in
        /// <see cref="TagBadges.CommentTagBadgeTagger"/> so both taggers agree on
        /// which comments the tag feature will render.
        /// </summary>
        private static readonly Regex _tagRegex = new Regex(
            @"\b(TODO|FIXME|HACK|NOTE|BUG|REVIEW|OPTIMIZE|TEMP|WARNING|WARN|" +
            @"DEPRECATED|CHANGED|SAFETY|INVARIANT|ASSUME|MAGIC)\b",
            RegexOptions.Compiled);

        /// <summary>
        /// Determines whether a match is <b>anchored</b> — relative to the match's
        /// OWN line inside the comment: every character from the line's first
        /// character (or from <paramref name="contentStart"/> on the opener line,
        /// skipping the opener itself) up to the match must be whitespace or a block
        /// decorator (<c>*</c>). Verbatim copy of the badge tagger's rule.
        /// </summary>
        private static bool IsAnchored(string text, int contentStart, int matchIndex)
        {
            int lineStart = contentStart;
            for (int i = matchIndex - 1; i >= contentStart; i--)
            {
                if (text[i] == '\n') { lineStart = i + 1; break; }
            }
            for (int i = lineStart; i < matchIndex; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c) || c == '*') continue;
                return false;
            }
            return true;
        }

        /// <summary>
        /// Determines whether the comment-tag feature would render anything inside
        /// this comment range — mirroring the active style's own eligibility rules so
        /// a range is skipped from boxing exactly when a pill/card appears in it:
        /// anchored, known, enabled, and (pills only) not click-dismissed.
        /// </summary>
        /// <param name="snapshot">Snapshot the range belongs to (dismissal key lookup).</param>
        /// <param name="rangeStart">Buffer position of the range start.</param>
        /// <param name="rangeText">Raw text of the range.</param>
        /// <param name="contentStart">Opener length — anchoring ignores it.</param>
        /// <param name="tagStyle">Active tag style ("Pills"/"Cards"), or null when
        /// tag highlighting is disabled (nothing can render → never skip).</param>
        private static bool ContainsRenderableTag(
            ITextSnapshot snapshot, int rangeStart, string rangeText,
            int contentStart, string tagStyle)
        {
            if (tagStyle == null) return false;

            foreach (Match m in _tagRegex.Matches(rangeText))
            {
                if (!IsAnchored(rangeText, contentStart, m.Index)) continue;
                if (!TagBadgeCatalog.TryNormalize(m.Value, out string canonical)) continue;
                if (!RenderDocOptions.Instance.EffectiveTagEnabled(canonical)) continue;

                // Pills honour click-dismissal; cards deliberately do not.
                if (tagStyle == "Pills" &&
                    TagBadgeToggleState.IsHidden(
                        snapshot.GetLineFromPosition(rangeStart + m.Index).GetText().Trim()))
                    continue;

                return true;
            }
            return false;
        }

        // ── BuildTags ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Single full pass: collect raw comment ranges, mark ranges the comment-tag
        /// feature will render, merge the remaining adjacent full-line line comments
        /// into blocks, strip comment tokens, and emit one bordered box per block —
        /// spanning the whole comment region so the editor collapses exactly the
        /// comment text.
        /// </summary>
        /// <remarks>
        /// <para><b>Span safety rules:</b></para>
        /// <list type="bullet">
        /// <item><description>Single-line comments span from opener to line end —
        /// code before an inline comment (<c>int x; // note</c>) remains visible.</description></item>
        /// <item><description>Multi-line blocks span from the FIRST line's start to
        /// the LAST line's end (the doc-card pattern — the editor does not render
        /// intra-text adornments whose multi-line span starts mid-line), and the box
        /// indents itself to the original opener column. Any code after <c>*/</c> on
        /// the closing line is stripped from the box text.</description></item>
        /// <item><description>Only full-line line comments on adjacent lines merge;
        /// trailing comments and block comments always stand alone.</description></item>
        /// <item><description>Ranges containing renderable tags are left to the
        /// pill/card feature and act as grouping barriers, so a box never overlaps a
        /// pill/card adornment.</description></item>
        /// </list>
        /// </remarks>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;

            var lang = GetLanguage(_buffer);
            var ranges = new List<RawRange>();
            CollectRawRanges(snapshot, lang, ranges);

            // Mark SINGLE-LINE ranges the comment-tag feature will render
            // (pills/cards) — those are not boxed. Skipped entirely when tag
            // highlighting is off. Multi-line ranges always box: a per-line
            // pill/card inside a boxed block would overlap the box and leave the
            // rest of the block raw.
            string tagStyle = opts.TagBadgesEnabled ? opts.EffectiveTagStyle : null;
            if (tagStyle != null)
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    var r = ranges[i];
                    if (r.StartLine != r.EndLine) continue;   // multi-line → box owns it
                    int len = r.End - r.Start;
                    if (len <= 0) continue;
                    string text;
                    try { text = snapshot.GetText(r.Start, len); }
                    catch { continue; }

                    r.HasTag = ContainsRenderableTag(
                        snapshot, r.Start, text, r.Opener.Length, tagStyle);
                    ranges[i] = r;
                }
            }

            // Merge consecutive full-line line comments on adjacent lines into one
            // box; trailing comments and block comments stand alone. Tagged ranges
            // break any open group and are never boxed.
            var blocks = new List<RawRange>(ranges.Count);
            RawRange open = default;
            bool hasOpen = false;
            foreach (var r in ranges)
            {
                if (r.HasTag)
                {
                    if (hasOpen)
                    {
                        blocks.Add(open);
                        hasOpen = false;
                    }
                    continue;
                }

                bool groupable = !r.IsBlock && r.IsFullLine;
                if (groupable && hasOpen && open.IsFullLine && !open.IsBlock &&
                    open.EndLine + 1 == r.StartLine)
                {
                    open.End = r.End;
                    open.EndLine = r.EndLine;
                    continue;
                }
                if (hasOpen) blocks.Add(open);
                open = r;
                hasOpen = true;
            }
            if (hasOpen) blocks.Add(open);

            foreach (var b in blocks)
            {
                // ── Resolve the adornment span ────────────────────────────────────
                // Single-line blocks keep the opener-based span (the editor renders
                // mid-line single-line spans fine). Multi-line blocks with a full-line
                // opener use the doc-card pattern: line start → line end, with the box
                // re-indenting itself to the opener column. Inline-opened multi-line
                // blocks keep the opener span so surrounding code is never collapsed.
                var firstLine = snapshot.GetLineFromPosition(b.Start);
                var lastLine = snapshot.GetLineFromPosition(b.End);
                bool multiline = b.StartLine != b.EndLine;
                bool fullLineOpener = b.IsFullLine ||
                    (b.IsBlock && IsAllWhitespaceBefore(
                        firstLine.GetText(), b.Start - firstLine.Start));

                int spanStart = b.Start;
                int spanEnd = b.End;
                double indent = 0.0;
                if (multiline && fullLineOpener)
                {
                    spanStart = firstLine.Start;
                    spanEnd = lastLine.End;
                    indent = MeasureIndent(firstLine.GetText());
                }

                int len = spanEnd - spanStart;
                if (len <= 0) continue;
                string raw;
                try { raw = snapshot.GetText(spanStart, len); }
                catch { continue; }

                var lines = ExtractDisplayLines(raw, b.IsBlock, b.Opener, b.Closer);

                bool hasContent = false;
                foreach (var t in lines)
                {
                    if (t.Trim().Length > 0) { hasContent = true; break; }
                }
                if (!hasContent) continue;

                var control = CreateBox(lines, snapshot, spanStart, indent);
                if (control == null) continue;

                var span = new SnapshotSpan(snapshot, spanStart, len);
                var tag = new IntraTextAdornmentTag(control, null, PositionAffinity.Predecessor);
                result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
            }

            return result;
        }

        /// <summary>
        /// Measures the pixel width of a line's leading whitespace (tabs counted as
        /// four columns) using the editor's column width — same approach as the
        /// doc-card tagger, used to indent line-start box spans back to the
        /// original opener column.
        /// </summary>
        private double MeasureIndent(string lineText)
        {
            int spaces = 0;
            foreach (char c in lineText)
            {
                if (c == ' ') { spaces++; continue; }
                if (c == '\t') { spaces += 4; continue; }
                break;
            }
            try
            {
                var cw = _view.FormattedLineSource?.ColumnWidth;
                if (cw.HasValue && cw.Value > 0) return spaces * cw.Value;
            }
            catch { }
            return spaces * 7.2;
        }

        // ── Comment-text extraction ───────────────────────────────────────────────

        /// <summary>
        /// Strips comment syntax tokens (opener, closer, <c>*</c> decorators, and one
        /// leading space per line) from raw comment text, returning the cleaned
        /// display lines. Blank lines become a single space so the box keeps its
        /// vertical rhythm.
        /// </summary>
        private static List<string> ExtractDisplayLines(
            string raw, bool isBlock, string opener, string closer)
        {
            var lines = new List<string>();
            var parts = raw.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

            if (!isBlock)
            {
                // A merged block spans from the FIRST line's opener, so subsequent
                // lines still carry their own leading indentation — trim it before
                // stripping the opener.
                foreach (var p in parts)
                {
                    string s = p.TrimStart();
                    if (s.StartsWith(opener, StringComparison.Ordinal))
                        s = s.Substring(opener.Length);
                    if (s.StartsWith(" ")) s = s.Substring(1);
                    lines.Add(s.TrimEnd());
                }
                return NormalizeBlanks(lines);
            }

            for (int i = 0; i < parts.Length; i++)
            {
                string s = parts[i];

                if (i == 0)
                {
                    // Line-start spans carry the first line's indentation; opener
                    // spans start exactly at the opener. TrimStart covers both.
                    s = s.TrimStart();
                    if (s.StartsWith(opener, StringComparison.Ordinal))
                        s = s.Substring(opener.Length);

                    // Single-line block: also strip the trailing closer.
                    if (parts.Length == 1 && closer.Length > 0)
                    {
                        var trimmed = s.TrimEnd();
                        if (trimmed.EndsWith(closer, StringComparison.Ordinal))
                            s = trimmed.Substring(0, trimmed.Length - closer.Length);
                    }
                }
                else
                {
                    s = s.TrimStart();

                    if (i == parts.Length - 1 && closer.Length > 0)
                    {
                        // Last block line: strip everything from the closer onward —
                        // line-end spans may include code after "*/" (which the box
                        // collapses), and it must never leak into the box text. The
                        // leading '*' decorator then still needs stripping (unlike
                        // interior lines, the closer cut happens before it here).
                        int idx = s.LastIndexOf(closer, StringComparison.Ordinal);
                        if (idx >= 0)
                        {
                            s = s.Substring(0, idx);
                            if (s.StartsWith("*")) s = s.Substring(1);
                        }
                        else if (s.StartsWith("*"))
                        {
                            s = s.Substring(1);
                        }
                    }
                    else if (s.StartsWith("*"))
                    {
                        s = s.Substring(1);
                    }
                }

                if (s.StartsWith(" ")) s = s.Substring(1);
                lines.Add(s.TrimEnd());
            }

            return NormalizeBlanks(lines);
        }

        /// <summary>Replaces empty display lines with a single space so blank
        /// comment rows keep their height inside the box.</summary>
        private static List<string> NormalizeBlanks(List<string> lines)
        {
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length == 0) lines[i] = " ";
            }
            return lines;
        }

        // ── Box construction ──────────────────────────────────────────────────────

        /// <summary>
        /// Builds the bordered box visual: 1px border in the comment colour
        /// (theme-derived or user-picked), editor-background fill, and one text row
        /// per comment line in the effective font.
        /// </summary>
        /// <param name="lines">Cleaned comment text lines.</param>
        /// <param name="snapshot">Snapshot the box spans (width computation).</param>
        /// <param name="spanStart">Buffer position of the box's span start.</param>
        /// <param name="indentPx">
        /// Left indent in pixels — non-zero for line-start spans, aligning the box
        /// with the original opener column (the doc-card approach).
        /// </param>
        /// <returns>The box element.</returns>
        private UIElement CreateBox(
            IReadOnlyList<string> lines, ITextSnapshot snapshot, int spanStart, double indentPx)
        {
            var opts = RenderDocOptions.Instance;

            // ── Font: option override, else the editor's font ────────────────────
            string famOverride = opts.EffectiveCommentBoxFontFamily;
            FontFamily fontFamily = null;
            if (!string.IsNullOrEmpty(famOverride))
            {
                try { fontFamily = new FontFamily(famOverride); }
                catch { fontFamily = null; }
            }

            double fontSize = 13.0;
            Typeface editorTypeface = null;
            try
            {
                var tp = _view.FormattedLineSource?.DefaultTextProperties;
                if (tp != null && tp.FontRenderingEmSize > 0)
                {
                    fontSize = tp.FontRenderingEmSize;
                    editorTypeface = tp.Typeface;
                }
            }
            catch { }
            if (fontFamily == null)
                fontFamily = editorTypeface?.FontFamily ?? new FontFamily("Consolas");

            // ── Colours: theme comment colour or user-picked; bg always editor ───
            Brush fgBrush;
            Brush borderBrush;
            if (opts.EffectiveCommentBoxUseThemeColors)
            {
                var theme = ResolveThemeCommentBrush();
                fgBrush = theme;
                borderBrush = theme;
            }
            else
            {
                fgBrush = Frozen(opts.EffectiveCommentBoxTextColor);
                borderBrush = Frozen(opts.EffectiveCommentBoxBorderColor);
            }
            Brush bgBrush = ResolveEditorBackgroundBrush();

            // ── Rows ─────────────────────────────────────────────────────────────
            var stack = new StackPanel();
            foreach (var t in lines)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = t,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    Foreground = fgBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            return new Border
            {
                Background = bgBrush,
                BorderBrush = borderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(indentPx + 2, 0, 2, 0),
                MaxWidth = ComputeMaxWidth(snapshot, spanStart, indentPx),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = stack,
            };
        }

        /// <summary>
        /// Resolves the active theme's comment foreground brush from the editor
        /// format map ("Comment" classification). Falls back to the default Visual
        /// Studio comment green when the map is unavailable or has no entry.
        /// </summary>
        private Brush ResolveThemeCommentBrush()
        {
            try
            {
                var formatMap = _view.Properties
                    .GetProperty<IEditorFormatMap>(typeof(IEditorFormatMap));
                if (formatMap != null)
                {
                    var props = formatMap.GetProperties("Comment");
                    if (props != null &&
                        props.Contains(EditorFormatDefinition.ForegroundBrushId) &&
                        props[EditorFormatDefinition.ForegroundBrushId] is Brush b)
                        return EnsureFrozen(b);
                }
            }
            catch { }
            return Frozen(Color.FromRgb(0x57, 0xA6, 0x4A));
        }

        /// <summary>
        /// Resolves the editor background brush from the editor format map
        /// ("TextView Background"). Falls back to the default dark-theme background
        /// when the map is unavailable or has no entry.
        /// </summary>
        private Brush ResolveEditorBackgroundBrush()
        {
            try
            {
                var formatMap = _view.Properties
                    .GetProperty<IEditorFormatMap>(typeof(IEditorFormatMap));
                if (formatMap != null)
                {
                    var props = formatMap.GetProperties("TextView Background");
                    if (props != null &&
                        props.Contains(EditorFormatDefinition.BackgroundBrushId) &&
                        props[EditorFormatDefinition.BackgroundBrushId] is Brush b)
                        return EnsureFrozen(b);
                }
            }
            catch { }
            return Frozen(Color.FromRgb(0x1E, 0x1E, 0x1E));
        }

        /// <summary>
        /// Computes the maximum box width: viewport width minus the box's horizontal
        /// offset (the span start's column offset within its line, approximated with
        /// the editor's column width, plus the explicit indent), minus a small
        /// margin. Long comment lines wrap inside the box rather than spilling past
        /// the viewport.
        /// </summary>
        private double ComputeMaxWidth(ITextSnapshot snapshot, int spanStart, double indentPx)
        {
            try
            {
                double viewport = _view.ViewportWidth;
                if (viewport <= 0) return double.PositiveInfinity;

                var line = snapshot.GetLineFromPosition(spanStart);
                int charsBefore = spanStart - line.Start;

                double colWidth = 7.2;
                try
                {
                    var cw = _view.FormattedLineSource?.ColumnWidth;
                    if (cw.HasValue && cw.Value > 0) colWidth = cw.Value;
                }
                catch { }

                double w = viewport - (charsBefore * colWidth) - indentPx - 8.0;
                return w > 200.0 ? w : 200.0;
            }
            catch
            {
                return double.PositiveInfinity;
            }
        }

        // ── Brush helpers ─────────────────────────────────────────────────────────

        /// <summary>Creates a frozen solid-colour brush.</summary>
        private static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        /// <summary>
        /// Returns a frozen, view-safe copy of a format-map brush: shared instances
        /// are cloned before freezing so the map's brush is never mutated.
        /// </summary>
        private static Brush EnsureFrozen(Brush brush)
        {
            if (brush.IsFrozen) return brush;
            var clone = brush.Clone();
            clone.Freeze();
            return clone;
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
        /// Re-queries tags when the viewport resizes, so box wrap widths and
        /// indentation offsets stay in sync with the new layout.
        /// </summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            bool widthChanged = e.NewViewState.ViewportWidth != e.OldViewState.ViewportWidth;
            bool heightChanged = e.NewViewState.ViewportHeight != e.OldViewState.ViewportHeight;
            if (!widthChanged && !heightChanged) return;

            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = _buffer.CurrentSnapshot;
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
        /// letting <see cref="GetTags"/> suppress/restore boxes on those lines.
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
        /// then rebuild on a dispatcher callback — preventing stale-box flashes.
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
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
