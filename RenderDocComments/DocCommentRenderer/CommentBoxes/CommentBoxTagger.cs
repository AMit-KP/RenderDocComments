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
 *       opener) and its opener/closer tokens. One-line asterisk boxes that
 *       merely look like doc comments are reclaimed for the banner feature.
 *    2. Banner detection — single-line ranges whose cleaned text is a solid
 *       fill rule or a flanked heading, multi-line ruled blocks (rule / text /
 *       rule), and ASCII boxes collapse into slim full-width header bars
 *       (CreateBanner) while BannerCommentsEnabled is effective. Banners win
 *       over tag-pill eligibility and break grouping like tagged ranges do.
 *    3. Tag-aware skip    — ranges containing renderable tag keywords (TODO,
 *       FIXME, … per the active pill/card style's eligibility rules) are left
 *       to the comment-tag feature and act as grouping barriers, so a box and
 *       a pill/card never overlap.
 *    4. Grouping          — remaining consecutive full-line line comments on
 *       adjacent lines merge into one box; trailing (inline) comments and block
 *       comments always stand alone so surrounding code is never collapsed.
 *    5. Box construction — comment opener/closer/decorator tokens are stripped
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
 *    • RenderDocOptions.cs         — EffectiveCommentBoxesEnabled,
 *                                     EffectiveBannerCommentsEnabled, EffectiveCommentBox*.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Boxes appear on non-comments / miss comments — fix CollectRawRanges.
 *    • Box styling changes — CreateBox.
 *    • Banner detection misfires — IsBannerLine / TryMatchMultiLineBanner.
 *    • Banner styling changes — CreateBanner.
 *    • Grouping of consecutive comment lines — BuildTags.
 *    • Visibility quirks around the caret — GetTags / OnCaretPositionChanged.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using RenderDocComments.DocCommentRenderer.TagBadges;
using RenderDocComments.DocCommentRenderer.TagScan;
using static RenderDocComments.DocCommentRenderer.BannerFactory;
using RenderDocComments.Options;

namespace RenderDocComments.DocCommentRenderer.CommentBoxes
{
    /// <summary>
    /// Replaces plain (non-doc) comments with thin bordered boxes across C#,
    /// VB.NET, F#, and C++ buffers. Banner-shaped comments (fill rules, flanked
    /// headings, ruled blocks, ASCII boxes) render as slim section header bars
    /// instead of boxes when the banner toggle is effective.
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
            if (!RenderDocOptions.Instance.EffectiveCommentBoxesEnabled &&
                !RenderDocOptions.Instance.EffectiveBannerCommentsEnabled) yield break;

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

        // ── Raw comment ranges ────────────────────────────────────────────────────

        /// <summary>
        /// One contiguous plain-comment region in buffer coordinates.<br/>
        /// <see cref="IsFullLine"/> is meaningful only for line comments and records
        /// whether everything before the opener on its start line is whitespace
        /// (full-line comments may merge with neighbours; trailing ones may not).
        /// <see cref="HasTag"/> marks ranges the comment-tag feature will render
        /// (pill/card) — they are never boxed and act as grouping barriers.
        /// <see cref="IsBanner"/> marks single-line ranges whose cleaned text is a
        /// banner (solid rule or flanked heading); they render as header bars, win
        /// over pill eligibility, and start multi-line banner units.
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
            public bool IsBanner;
            public BannerStyle Style;
            public RailStyle Rail;
            public string Opener;       // "//" or "'" or "/*" or "(*"
            public string Closer;       // "" (line) or "*/" or "*)"

            /// <summary>
            /// Single-line ranges only: cleaned comment text (opener/closer and the
            /// conventional one-space separator stripped, trailing whitespace
            /// trimmed, leading content whitespace kept) — the input for banner
            /// detection and multi-line banner windows. Null for multi-line ranges.
            /// </summary>
            public string CleanLine;

            /// <summary>Banner display text; empty for pure fill rules.</summary>
            public List<string> BannerText;
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
            ITextSnapshot snapshot, CommentTagLocator.BufferLanguage lang, List<RawRange> ranges)
        {
            bool isVb = lang == CommentTagLocator.BufferLanguage.VBNet;
            bool isFSharp = lang == CommentTagLocator.BufferLanguage.FSharp;

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

                            // A one-line asterisk box (three or more stars flanking
                            // short text, e.g. the degenerate /*** … */) is a
                            // decorative banner, not an XML doc comment — reclaim it
                            // for the box/banner feature when it closes on this line.
                            if (isDoc && t[i + 2] == '*' && IsOneLineStarBox(t, i))
                                isDoc = false;

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
        /// Determines whether the comment-tag feature would render anything inside
        /// this comment range — mirroring the active style's own eligibility rules so
        /// a range is skipped from boxing exactly when a pill/card appears in it:
        /// anchored, known, enabled, and (pills only) not click-dismissed.
        /// Matching goes through the shared locator so the box feature and the
        /// pill/card features can never disagree about where tags are.
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

            foreach (var m in CommentTagLocator.Locate(rangeText, contentStart))
            {
                if (!RenderDocOptions.Instance.EffectiveTagEnabled(m.CanonicalTag)) continue;

                // Pills honour click-dismissal; cards deliberately do not.
                if (tagStyle == "Pills" &&
                    TagBadgeToggleState.IsHidden(
                        snapshot.GetLineFromPosition(rangeStart + m.MatchIndex).GetText().Trim()))
                    continue;

                return true;
            }
            return false;
        }

        // ── Banner detection ──────────────────────────────────────────────────────

        /// <summary>
        /// Characters that count as banner "fill" when they dominate a line: the
        /// ASCII rules (equals, dashes, asterisks, tildes, hashes, pluses) and the
        /// box-drawing horizontals.
        /// </summary>
        private static bool IsRunChar(char c)
        {
            switch (c)
            {
                case '=':
                case '-':
                case '*':
                case '~':
                case '#':
                case '+':
                case '─':
                case '━':
                case '═':
                case '┄':
                case '┈':
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Rule 1 — solid rule: the trimmed line is at least eight characters long
        /// and at least sixty percent of its characters are fill characters.
        /// </summary>
        private static bool IsSolidRule(string line)
        {
            string t = line.Trim();
            if (t.Length < 8) return false;
            int run = 0;
            foreach (char c in t)
            {
                if (IsRunChar(c)) run++;
            }
            return run * 10 >= t.Length * 6;
        }

        /// <summary>
        /// Rule 2 — flanked text: the line starts and ends with a fill run and the
        /// embedded text between the rails is 1–40 characters containing no fill
        /// characters. Rails may be as short as two characters — real-world
        /// headings like <c>── Title ────────</c> open with a short left rail —
        /// except that asterisk rails in line comments still need three so
        /// markdown bold (<c>**text**</c>) never becomes a banner.
        /// <paramref name="allowStarRails"/> (single-line block comments only)
        /// relaxes the asterisk rails to two for the one-line box form.
        /// <paramref name="railChar"/> reports the leading rail character on success.
        /// </summary>
        private static bool TryGetFlankedText(
            string line, bool allowStarRails, out string text, out char railChar)
        {
            text = null;
            railChar = '─';
            string t = line.Trim();
            if (t.Length == 0) return false;   // bare "//" lines — t[0] would throw

            int lead = 0;
            while (lead < t.Length && IsRunChar(t[lead])) lead++;
            int trail = 0;
            while (trail < t.Length - lead && IsRunChar(t[t.Length - 1 - trail])) trail++;

            int minRail = t[0] == '*' && !allowStarRails ? 3 : 2;
            if (lead < minRail || trail < minRail) return false;

            string middle = t.Substring(lead, t.Length - lead - trail);
            // Fill runs of 3+ inside the middle mean embedded rails; shorter runs
            // are ordinary text: "-" in "per-session", "#" in "C#", "*" in "a * b".
            int midRun = 0;
            foreach (char c in middle)
            {
                if (IsRunChar(c))
                {
                    midRun++;
                    if (midRun >= 3) return false;
                }
                else
                {
                    midRun = 0;
                }
            }
            text = middle.Trim();
            if (text.Length == 0 || text.Length > 40)
            {
                text = null;
                return false;
            }
            railChar = t[0];
            return true;
        }

        /// <summary>
        /// Determines whether one cleaned comment line is a banner, and extracts
        /// its display text and rail character. Flanked text is tried FIRST: a line
        /// with text between fill rails is a text banner even when the fill
        /// dominates the line (<c>── Title ────────</c> is mostly fill but obviously
        /// carries a heading) — checking the solid-rule percentage first would
        /// discard the text. Only lines with no extractable text fall through to
        /// the solid rule (style 1), rendering as textless divider bars.
        /// <paramref name="isBlockLine"/> enables the relaxed asterisk rails for
        /// single-line block comments.
        /// </summary>
        private static bool IsBannerLine(
            string line, bool isBlockLine, out string bannerText, out char railChar)
        {
            if (TryGetFlankedText(line, isBlockLine, out string text, out railChar))
            {
                bannerText = text;
                return true;
            }
            bannerText = null;
            string t = line.Trim();
            if (IsSolidRule(t))
            {
                railChar = t.Length > 0 ? t[0] : '─';
                return true;
            }
            railChar = '─';
            return false;
        }

        /// <summary>
        /// Cleans one comment line for banner detection: strips the closer suffix
        /// first (block comments — before the opener, so asterisk boxes keep their
        /// rail lengths), then the opener prefix and the conventional one-space
        /// separator. Leading content whitespace is kept so multi-line banner text
        /// can be dedented relative to its deepest line.
        /// </summary>
        private static string CleanSingleLine(
            string text, bool isBlock, string opener, string closer)
        {
            string s = text.Trim();
            if (isBlock && closer.Length > 0 && s.EndsWith(closer, StringComparison.Ordinal))
                s = s.Substring(0, s.Length - closer.Length);
            if (s.StartsWith(opener, StringComparison.Ordinal))
                s = s.Substring(opener.Length);
            if (s.StartsWith(" ")) s = s.Substring(1);
            return s.TrimEnd();
        }

        /// <summary>
        /// Recognises the degenerate one-line asterisk box: an opening <c>/**</c>
        /// whose line closes with the block closer at end of line and whose content
        /// is asterisk rails around 1–40 plain characters (three stars, spaces, a
        /// section name, spaces, three stars). Such lines are decorative banners,
        /// not XML doc comments, so the box/banner feature may own them.
        /// </summary>
        /// <param name="t">The full line text.</param>
        /// <param name="openIdx">Index of the opening slash of the <c>/**</c> opener.</param>
        private static bool IsOneLineStarBox(string t, int openIdx)
        {
            string trimmed = t.TrimEnd();
            int j = trimmed.LastIndexOf("*/", StringComparison.Ordinal);
            if (j < openIdx + 3 || j + 2 != trimmed.Length) return false;

            string content = trimmed.Substring(openIdx + 3, j - openIdx - 3).Trim();
            if (content.Length < 3 || content[0] != '*' || content[content.Length - 1] != '*')
                return false;

            string core = content.Trim('*').Trim();
            if (core.Length == 0 || core.Length > 40) return false;
            foreach (char c in core)
            {
                if (IsRunChar(c)) return false;
            }
            return true;
        }

        /// <summary>Vertical border characters recognised inside ASCII boxes.</summary>
        private static bool IsVerticalBorder(char c)
            => c == '│' || c == '|' || c == '┃' || c == '║';

        /// <summary>
        /// A boxed-banner middle line: starts and ends with a vertical border
        /// character (after trimming).
        /// </summary>
        private static bool IsBorderedLine(string t)
            => t.Length >= 2 && IsVerticalBorder(t[0]) && IsVerticalBorder(t[t.Length - 1]);

        /// <summary>
        /// Removes the leading/trailing vertical borders from a boxed-banner middle
        /// line and trims the remaining text.
        /// </summary>
        private static string StripVerticalBorders(string t)
        {
            if (t.Length >= 2 && IsVerticalBorder(t[0]) && IsVerticalBorder(t[t.Length - 1]))
                return t.Substring(1, t.Length - 2).Trim();
            if (t.Length >= 1 && IsVerticalBorder(t[0]))
                return t.Substring(1).Trim();
            if (t.Length >= 1 && IsVerticalBorder(t[t.Length - 1]))
                return t.Substring(0, t.Length - 1).Trim();
            return t;
        }

        /// <summary>
        /// Box edge characters. The horizontals and <c>+ = * -</c> appear on both
        /// edges; the corners are edge-specific (top: ┌ ┬ ╭ ┐ ╮, bottom: └ ┴ ╰ ┘ ╯).
        /// </summary>
        private static bool IsBoxEdgeChar(char c, bool top)
        {
            switch (c)
            {
                case '─':
                case '═':
                case '━':
                case '┄':
                case '┈':
                case '+':
                case '=':
                case '*':
                case '-':
                    return true;
                case '┌':
                case '┬':
                case '╭':
                case '┐':
                case '╮':
                    return top;
                case '└':
                case '┴':
                case '╰':
                case '┘':
                case '╯':
                    return !top;
                default:
                    return false;
            }
        }

        /// <summary>
        /// A box top/bottom edge: the line is composed only of edge characters and
        /// spaces, with at least sixty percent edge characters.
        /// </summary>
        private static bool IsBoxEdge(string line, bool top)
        {
            string t = line.Trim();
            if (t.Length < 4) return false;
            int edge = 0;
            foreach (char c in t)
            {
                if (c == ' ') continue;
                if (!IsBoxEdgeChar(c, top)) return false;
                edge++;
            }
            return edge * 10 >= t.Length * 6;
        }

        /// <summary>
        /// Removes one trailing asterisk decorator from a cleaned block-comment
        /// line (the <c>* text *</c> decoration convention keeps a trailing star
        /// after ExtractDisplayLines strips the leading one) so ruled-block middles
        /// stay plain prose and the star never leaks into banner text. No-op for
        /// line-comment windows, where a trailing star is content.
        /// </summary>
        private static string TrimTrailingStarDecorator(string line, bool enabled)
        {
            if (!enabled) return line;
            string t = line.TrimEnd();
            if (t.Length > 0 && t[t.Length - 1] == '*')
                t = t.Substring(0, t.Length - 1);
            return t;
        }

        /// <summary>
        /// A ruled-block middle line: any non-empty line that does not start with a
        /// vertical border (bordered lines belong to the boxed banner form).
        /// Callers guarantee the line is not a solid rule.
        /// </summary>
        private static bool IsProseLine(string trimmed)
            => !IsVerticalBorder(trimmed[0]);

        /// <summary>
        /// Classifies a window of cleaned comment lines as a multi-line banner:
        /// <list type="bullet">
        /// <item><description><b>Ruled block</b> — first and last lines are solid
        /// rules with 1–8 prose lines between them. A solid rule seen mid-scan
        /// closes the block, so stacked multi-banners match one pass at a time and
        /// the caller re-runs the remaining lines.</description></item>
        /// <item><description><b>Boxed banner</b> — first and last lines are box
        /// edges with 1–8 bordered (or empty) lines between them; the border
        /// characters are stripped from the extracted text.</description></item>
        /// </list>
        /// Ruled blocks are tried first so prose middles never fall through to the
        /// boxed form. On success <paramref name="consumed"/> reports how many
        /// leading lines form the banner and <paramref name="textLines"/> holds the
        /// cleaned display text.
        /// </summary>
        /// <param name="lines">Cleaned comment lines (opener tokens stripped).</param>
        /// <param name="stripTrailingStars">Strip one trailing asterisk decorator
        /// per line (block-comment sources, where the <c>* text *</c> decoration is
        /// conventional).</param>
        /// <param name="boxed">True when the match is an ASCII/unicode box
        /// (outlined-box rendering); false for a ruled block (sandwich).</param>
        /// <param name="railChar">Leading rail character of the first line
        /// (drives single/double/fence/sparkle/wave line treatment).</param>
        private static bool TryMatchMultiLineBanner(
            IReadOnlyList<string> lines, bool stripTrailingStars,
            out int consumed, out List<string> textLines,
            out bool boxed, out char railChar)
        {
            consumed = 0;
            textLines = null;
            boxed = false;
            railChar = '─';
            if (lines.Count < 3) return false;

            string first = lines[0].Trim();
            char lead = first.Length > 0 ? first[0] : '─';

            // ── Ruled block: rule / 1–40 prose lines / rule ────────────────────
            if (IsSolidRule(lines[0]))
            {
                var middle = new List<string>();
                for (int j = 1; j < lines.Count && j <= 41; j++)
                {
                    string cur = TrimTrailingStarDecorator(lines[j], stripTrailingStars);
                    if (IsSolidRule(cur))
                    {
                        if (middle.Count == 0) break;   // adjacent rules — not a block
                        consumed = j + 1;
                        textLines = middle;             // original indentation kept
                        boxed = false;
                        railChar = lead;
                        return true;
                    }
                    string probe = cur.Trim();
                    if (probe.Length > 0)
                    {
                        if (!IsProseLine(probe)) break; // bordered — boxed form's job
                        middle.Add(cur);                // leading whitespace kept
                    }
                }
            }

            // ── Boxed banner: top edge / 1–8 bordered lines / bottom edge ─────
            if (IsBoxEdge(lines[0], true))
            {
                var middle = new List<string>();
                for (int j = 1; j < lines.Count && j <= 41; j++)
                {
                    string trimmed = lines[j].Trim();
                    if (IsBoxEdge(trimmed, false))
                    {
                        if (middle.Count == 0) break;   // adjacent edges — degenerate
                        consumed = j + 1;
                        textLines = middle;
                        boxed = true;
                        railChar = lead;
                        return true;
                    }
                    if (trimmed.Length > 0)
                    {
                        if (!IsBorderedLine(trimmed)) break;
                        string inner = StripVerticalBorders(trimmed);
                        if (inner.Length > 0) middle.Add(inner);
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Attempts to extend the banner-marked range at <paramref name="start"/>
        /// into a multi-line banner unit (ruled block or boxed banner) spanning the
        /// following contiguous full-line line-comment ranges. On success the
        /// returned unit carries the banner text and <paramref name="consumed"/>
        /// the number of ranges absorbed; tagged, block, trailing, and multi-line
        /// ranges stop the window exactly as they break grouping.
        /// </summary>
        private static bool TryBuildMultiLineBannerUnit(
            List<RawRange> ranges, int start, out RawRange unit, out int consumed)
        {
            unit = default;
            consumed = 0;

            // The window cap mirrors the classifier's 1–40 middle lines plus the
            // two rails (a closer beyond that cannot match anyway).
            var window = new List<string> { ranges[start].CleanLine ?? string.Empty };
            int last = start;
            while (window.Count < 43 && last + 1 < ranges.Count)
            {
                var next = ranges[last + 1];
                if (next.IsBlock || next.HasTag || !next.IsFullLine ||
                    next.StartLine != next.EndLine ||
                    next.StartLine != ranges[last].EndLine + 1)
                    break;
                window.Add(next.CleanLine ?? string.Empty);
                last++;
            }
            if (window.Count < 3) return false;

            if (!TryMatchMultiLineBanner(window, false, out int used, out List<string> text,
                    out bool boxed, out char railChar))
                return false;

            var endRange = ranges[start + used - 1];
            unit = ranges[start];
            unit.End = endRange.End;
            unit.EndLine = endRange.EndLine;
            unit.IsBanner = true;
            unit.Style = boxed ? BannerStyle.Box : BannerStyle.Sandwich;
            unit.Rail = MapRail(railChar);
            unit.BannerText = text;
            consumed = used;
            return true;
        }

        // ── BuildTags ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Single full pass: collect raw comment ranges, mark single-line banners
        /// and ranges the comment-tag feature will render, merge the remaining
        /// adjacent full-line line comments into blocks (banner lines break groups
        /// and may extend into multi-line banner units), strip comment tokens, and
        /// emit one header bar or bordered box per block — spanning the whole
        /// comment region so the editor collapses exactly the comment text.
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
        /// pill/card adornment. Banner lines win over pill eligibility — they are
        /// marked before the tag scan and never carry a pill.</description></item>
        /// </list>
        /// </remarks>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            var opts = RenderDocOptions.Instance;
            bool bannersOn = opts.EffectiveBannerCommentsEnabled;
            bool boxesOn = opts.EffectiveCommentBoxesEnabled;
            if (!bannersOn && !boxesOn) return result;

            var lang = CommentTagLocator.GetLanguage(_buffer);
            var ranges = new List<RawRange>();
            CollectRawRanges(snapshot, lang, ranges);

            // Single-line range prep. Banners are detected FIRST so they win over
            // pill eligibility ("// ---- TODO list ----" renders as one bar, not a
            // pill plus a box). Everything else is marked when the comment-tag
            // feature will render it — those ranges are not boxed. Multi-line
            // ranges always box (or banner via block classification after
            // grouping): a per-line pill/card inside a collapsed block would
            // overlap the adornment and leave the rest of the block raw.
            string tagStyle = opts.TagBadgesEnabled ? opts.EffectiveTagStyle : null;
            if (bannersOn || tagStyle != null)
            {
                for (int i = 0; i < ranges.Count; i++)
                {
                    var r = ranges[i];
                    if (r.StartLine != r.EndLine) continue;   // multi-line → post-grouping
                    int len = r.End - r.Start;
                    if (len <= 0) continue;
                    string text;
                    try { text = snapshot.GetText(r.Start, len); }
                    catch { continue; }

                    if (bannersOn)
                    {
                        r.CleanLine = CleanSingleLine(text, r.IsBlock, r.Opener, r.Closer);
                        if (IsBannerLine(r.CleanLine, r.IsBlock, out string bannerText, out char railChar))
                        {
                            r.IsBanner = true;
                            r.Rail = MapRail(railChar);
                            r.Style = r.IsBlock
                                ? BannerStyle.Badge
                                : (bannerText != null && bannerText.Length > 0
                                    ? BannerStyle.Heading
                                    : BannerStyle.Divider);
                            r.BannerText = bannerText != null && bannerText.Length > 0
                                ? new List<string> { bannerText }
                                : new List<string>();
                            ranges[i] = r;
                            continue;   // banner wins over pill eligibility
                        }
                        ranges[i] = r;
                    }

                    if (tagStyle != null)
                    {
                        r.HasTag = ContainsRenderableTag(
                            snapshot, r.Start, text, r.Opener.Length, tagStyle);
                        ranges[i] = r;
                    }
                }
            }

            // Merge consecutive full-line line comments on adjacent lines into one
            // box; trailing comments and block comments stand alone. Tagged ranges
            // break any open group and are never boxed. Banner lines also break any
            // open group; each then either extends into a multi-line banner unit
            // (ruled block / boxed banner) or stands alone as a single-line bar.
            var blocks = new List<RawRange>(ranges.Count);
            RawRange open = default;
            bool hasOpen = false;
            int index = 0;
            while (index < ranges.Count)
            {
                var r = ranges[index];

                if (r.HasTag)
                {
                    if (hasOpen)
                    {
                        blocks.Add(open);
                        hasOpen = false;
                    }
                    index++;
                    continue;
                }

                if (bannersOn && r.IsBanner)
                {
                    if (hasOpen)
                    {
                        blocks.Add(open);
                        hasOpen = false;
                    }
                    if (TryBuildMultiLineBannerUnit(ranges, index, out RawRange unit, out int consumed))
                    {
                        blocks.Add(unit);
                        index += consumed;
                        continue;
                    }
                    blocks.Add(r);
                    index++;
                    continue;
                }

                bool groupable = !r.IsBlock && r.IsFullLine;
                if (groupable && hasOpen && open.IsFullLine && !open.IsBlock &&
                    open.EndLine + 1 == r.StartLine)
                {
                    open.End = r.End;
                    open.EndLine = r.EndLine;
                    index++;
                    continue;
                }
                if (hasOpen) blocks.Add(open);
                open = r;
                hasOpen = true;
                index++;
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

                // ── Banner or box ─────────────────────────────────────────────
                // Units marked during grouping render as header bars directly.
                // Block comments never pass through the grouping lookahead (they
                // never merge), so they are classified here from their cleaned
                // display lines.
                List<string> bannerText = b.IsBanner ? b.BannerText : null;
                BannerStyle bannerStyle = b.Style;
                RailStyle bannerRail = b.Rail;
                List<string> lines = null;
                if (bannerText == null && bannersOn && b.IsBlock && multiline)
                {
                    lines = ExtractDisplayLines(raw, true, b.Opener, b.Closer);
                    if (TryMatchMultiLineBanner(lines, true, out _,
                            out List<string> blockBanner, out bool boxed, out char railChar))
                    {
                        bannerText = blockBanner;
                        bannerStyle = boxed ? BannerStyle.Box : BannerStyle.Sandwich;
                        bannerRail = MapRail(railChar);
                    }
                }

                UIElement control = null;
                try
                {
                    if (bannerText != null)
                    {
                        control = CreateBanner(bannerText, bannerStyle, bannerRail,
                            snapshot, spanStart, indent);
                    }
                    else
                    {
                    // Boxes are independently toggleable — with box rendering off,
                    // non-banner comments stay as plain text.
                    if (!boxesOn) continue;

                    if (lines == null)
                        lines = ExtractDisplayLines(raw, b.IsBlock, b.Opener, b.Closer);

                    bool hasContent = false;
                    foreach (var t in lines)
                    {
                        if (t.Trim().Length > 0) { hasContent = true; break; }
                    }
                    if (!hasContent) continue;

                        control = CreateBox(lines, snapshot, spanStart, indent);
                    }
                }
                catch
                {
                    control = null;   // one bad visual must not blank the file
                }
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
        /// Shared style resolution for box and banner visuals: the effective font
        /// (option override, else the editor's font) and the colour set — text and
        /// border from the theme's comment classification or the user overrides,
        /// background always the editor background.
        /// </summary>
        private void ResolveBoxStyle(
            out FontFamily fontFamily, out double fontSize,
            out Brush fgBrush, out Brush borderBrush, out Brush bgBrush)
        {
            var opts = RenderDocOptions.Instance;

            // ── Font: option override, else the editor's font ────────────────
            string famOverride = opts.EffectiveCommentBoxFontFamily;
            fontFamily = null;
            if (!string.IsNullOrEmpty(famOverride))
            {
                try { fontFamily = new FontFamily(famOverride); }
                catch { fontFamily = null; }
            }

            fontSize = 13.0;
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

            // ── Colours: theme comment colour or user-picked; bg always editor ─
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
            bgBrush = ResolveEditorBackgroundBrush();
        }

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
            ResolveBoxStyle(
                out FontFamily fontFamily, out double fontSize,
                out Brush fgBrush, out Brush borderBrush, out Brush bgBrush);

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
        /// Builds the banner visual, per the comment shape it came from:
        /// <list type="bullet">
        /// <item><description><b>Heading</b> — title + trailing rule (trail
        /// treatment per rail character: single/double line, picket fence,
        /// sparkle trail, or wave).</description></item>
        /// <item><description><b>Divider</b> — bare fill rule → the line only.</description></item>
        /// <item><description><b>Sandwich</b> — ruled block → full-width line
        /// above, stacked title lines, line below.</description></item>
        /// <item><description><b>Box</b> — ASCII/unicode box → thin outlined box
        /// around the title.</description></item>
        /// <item><description><b>Badge</b> — one-line asterisk box → small pill.</description></item>
        /// </list>
        /// No background band, no accent segment.
        /// </summary>
        private UIElement CreateBanner(
            IReadOnlyList<string> textLines, BannerStyle style, RailStyle rail,
            ITextSnapshot snapshot, int spanStart, double indentPx)
        {
            BannerFactory.ResolveStyle(
                _view, out FontFamily fontFamily, out double fontSize,
                out Brush textBrush, out Color ruleColor);
            double ruleThickness = RenderDocOptions.Instance.EffectiveBannerRuleThickness;
            double barHeight = RenderDocOptions.Instance.EffectiveBannerBarHeight;

            // Banner text keeps the comment's original casing — and sandwich
            // text its original indentation (continuations, bullets, sub-bullets).
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            foreach (var rawLine in textLines)
            {
                if (rawLine.Trim().Length == 0) continue;
                textStack.Children.Add(new TextBlock
                {
                    Text = rawLine,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textBrush,
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            // A textless badge would be a hollow pill — render a divider instead.
            if (style == BannerStyle.Badge && textStack.Children.Count == 0)
                style = BannerStyle.Divider;

            switch (style)
            {
                case BannerStyle.Box:
                    return new Border
                    {
                        BorderBrush = Frozen(Color.FromArgb(0x8C, ruleColor.R, ruleColor.G, ruleColor.B)),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(6),
                        Padding = new Thickness(16, 3, 16, 3),
                        Margin = new Thickness(indentPx + 2, 0, 2, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = textStack,
                    };

                case BannerStyle.Badge:
                    return new Border
                    {
                        Background = Frozen(Color.FromArgb(0x29, ruleColor.R, ruleColor.G, ruleColor.B)),
                        CornerRadius = new CornerRadius(10),
                        Padding = new Thickness(14, 2, 14, 2),
                        Margin = new Thickness(indentPx + 2, 0, 2, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = textStack,
                    };

                case BannerStyle.Sandwich:
                {
                    var sandwich = new StackPanel { Margin = new Thickness(indentPx + 2, 0, 2, 0) };
                    sandwich.Children.Add(BuildTrail(rail, ruleColor, ruleThickness, fullWidth: true));
                    var mid = new StackPanel { Margin = new Thickness(0, 3, 0, 3) };
                    mid.Children.Add(textStack);
                    sandwich.Children.Add(mid);
                    sandwich.Children.Add(BuildTrail(rail, ruleColor, ruleThickness, fullWidth: true));

                    var frame = new Border
                    {
                        Padding = new Thickness(0, 1, 0, 1),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = sandwich,
                    };
                    double barWidth = ComputeMaxWidth(snapshot, spanStart, indentPx);
                    if (!double.IsInfinity(barWidth))
                        frame.Width = barWidth;
                    if (barHeight > 0)
                        frame.Height = barHeight;
                    return frame;
                }

                default:   // Heading or Divider — title (optional) + trailing rule
                {
                    bool hasTitle = textStack.Children.Count > 0;
                    var grid = new Grid { ClipToBounds = true };

                    if (hasTitle)
                    {
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        textStack.Margin = new Thickness(0, 1, 0, 1);
                        Grid.SetColumn(textStack, 0);
                        grid.Children.Add(textStack);

                        var headingTrail = BuildTrail(rail, ruleColor, ruleThickness, fullWidth: false);
                        headingTrail.Margin = new Thickness(14, 0, 0, 0);
                        headingTrail.VerticalAlignment = VerticalAlignment.Center;
                        Grid.SetColumn(headingTrail, 1);
                        grid.Children.Add(headingTrail);
                    }
                    else
                    {
                        // Textless divider: the trail must live in a STAR column —
                        // an Auto column collapses to the trail's zero desired
                        // width and the line vanishes.
                        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                        var dividerTrail = BuildTrail(rail, ruleColor, ruleThickness, fullWidth: true);
                        Grid.SetColumn(dividerTrail, 0);
                        grid.Children.Add(dividerTrail);
                    }

                    var border = new Border
                    {
                        Padding = new Thickness(0, 1, 0, 1),
                        Margin = new Thickness(indentPx + 2, 0, 2, 0),
                        HorizontalAlignment = HorizontalAlignment.Left,
                        Child = grid,
                    };
                    double barWidth = ComputeMaxWidth(snapshot, spanStart, indentPx);
                    if (!double.IsInfinity(barWidth))
                        border.Width = barWidth;
                    if (barHeight > 0)
                        border.Height = barHeight;
                    return border;
                }
            }
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
