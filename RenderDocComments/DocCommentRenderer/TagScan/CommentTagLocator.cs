/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    CommentTagLocator.cs
 *  Purpose: Single shared implementation of the comment-tag detection pipeline —
 *           comment-range walk, tag alternation, anchoring rule, normalisation,
 *           and tooltip-tail extraction — consumed by every tag feature so their
 *           outputs can never disagree.
 *
 *  Architecture Role:
 *    Pure static scan helpers with no VS services, no options reads, and no UI.
 *    Consumers apply their own eligibility filters (EffectiveTagEnabled, pill
 *    click-dismissal, single-line restriction, …) on top of the located matches.
 *
 *  Consumers:
 *    • CommentTagBadgeTagger / TagCardTagger (pills & cards)
 *    • CommentBoxTagger (ContainsRenderableTag — box/tag overlap avoidance)
 *    • ScrollMarkerMargin (scroll-bar ticks)
 *    • CommentTagsExplorer's CommentTagsScanner (file-based scan; keeps its own
 *      disk-oriented range walk because it reads closed files, but reuses the
 *      anchored-match helper so a match is a match everywhere).
 *
 *  History:
 *    This pipeline existed as four deliberate byte-identical copies (each file's
 *    comments said "identical to the one in …"). Extraction keeps the matching
 *    rules verbatim — a behaviour-identical refactor; a match found by any
 *    feature is found by all.
 *
 *  When to Edit:
 *    • A tag is not recognised everywhere — extend TagRegex AND TagBadgeCatalog.
 *    • Anchoring behaves oddly on interior block lines — IsAnchored (shared by
 *      all features, so a fix propagates everywhere at once).
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using RenderDocComments.DocCommentRenderer.TagBadges;

namespace RenderDocComments.DocCommentRenderer.TagScan
{
    /// <summary>
    /// Shared tag-detection pipeline for every comment-tag feature: comment-range
    /// collection, anchored UPPERCASE tag matching, canonical normalisation, and
    /// tooltip-tail extraction.
    /// </summary>
    /// <remarks>
    /// <para><b>Behavioural contract:</b> the regex, anchoring rule, and tail
    /// extraction below are the verbatim rules the pill/card taggers shipped with.
    /// Features that must agree on which tags exist (pills, cards, scroll ticks)
    /// all locate through this class, so their occurrence sets are identical by
    /// construction.</para>
    /// <para><b>Purity:</b> no options reads and no UI — callers decide eligibility
    /// (<c>EffectiveTagEnabled</c>, dismissal, style restrictions) per feature.</para>
    /// </remarks>
    internal static class CommentTagLocator
    {
        // ── Language detection ────────────────────────────────────────────────────

        /// <summary>Supported buffer languages (shared by all tag features).</summary>
        internal enum BufferLanguage { CSharp, VBNet, FSharp, Cpp }

        /// <summary>
        /// Maps the buffer's content type onto a <see cref="BufferLanguage"/>.
        /// Unknown content types default to C# semantics (<c>//</c> comments).
        /// </summary>
        internal static BufferLanguage GetLanguage(ITextBuffer buffer)
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
        /// One anchored, normalised tag match inside a comment region.
        /// </summary>
        internal struct TagMatch
        {
            /// <summary>Canonical tag name after alias normalisation (e.g. <c>"WARN"</c>).</summary>
            public string CanonicalTag;

            /// <summary>Index of the matched token within the region text.</summary>
            public int MatchIndex;

            /// <summary>Length of the matched token (before any colon swallowing).</summary>
            public int MatchLength;

            /// <summary>
            /// Human-readable comment text following the tag (newline-cut, closer-stripped,
            /// capped at <see cref="MaxTooltipTailLength"/>). May be empty.
            /// </summary>
            public string TailText;
        }

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
        internal const int MaxTooltipTailLength = 200;

        /// <summary>Newline characters used to cut multi-line block tails.</summary>
        private static readonly char[] _newlineChars = { '\r', '\n' };

        /// <summary>
        /// Matches a trailing block-comment closer (and surrounding whitespace) so
        /// single-line tails like <c>"broken */"</c> end cleanly at the prose.
        /// </summary>
        private static readonly Regex _trailingCloser = new Regex(
            @"\s*\*/\s*$", RegexOptions.Compiled);

        /// <summary>
        /// Locates every anchored, known tag inside one comment region and returns
        /// them in document order with their canonical names and tooltip tails.
        /// </summary>
        /// <param name="rangeText">Raw text of the comment region.</param>
        /// <param name="contentStart">
        /// Length of the comment opener at the start of <paramref name="rangeText"/>
        /// (<c>//</c>, <c>'</c>, <c>/*</c>, <c>(*</c>) — anchoring ignores it so the
        /// opener itself doesn't unanchor matches.
        /// </param>
        /// <remarks>
        /// Eligibility filters (per-tag enablement, click-dismissal, …) are the
        /// caller's responsibility — this method reports every tag the detection
        /// pipeline can see.
        /// </remarks>
        internal static List<TagMatch> Locate(string rangeText, int contentStart)
        {
            var results = new List<TagMatch>();
            foreach (Match m in _tagRegex.Matches(rangeText))
            {
                if (!IsAnchored(rangeText, contentStart, m.Index)) continue;
                if (!TagBadgeCatalog.TryNormalize(m.Value, out var canonical)) continue;
                results.Add(new TagMatch
                {
                    CanonicalTag = canonical,
                    MatchIndex = m.Index,
                    MatchLength = m.Length,
                    TailText = ExtractTail(rangeText, m.Index + m.Length),
                });
            }
            return results;
        }

        /// <summary>
        /// Determines whether a match is <b>anchored</b> — relative to the match's
        /// OWN line inside the comment: every character from the line's first
        /// character (or from <paramref name="contentStart"/> on the opener line,
        /// skipping the opener itself) up to the match must be whitespace or a block
        /// decorator (<c>*</c>). Interior block lines therefore anchor fresh after
        /// each newline (<c>" * NOTE"</c> renders), while words following prose on
        /// the same line (<c>"// WARNING: alias of WARN"</c>) never do.
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
        /// Extracts the human-readable remainder of a comment following a matched tag:<br/>
        /// cut at the first newline (block ranges span lines — following lines must
        /// never leak into the tooltip), trailing <c>*/</c> closer removed,
        /// optional colon and whitespace trimmed, capped at
        /// <see cref="MaxTooltipTailLength"/> characters.
        /// </summary>
        internal static string ExtractTail(string rangeText, int tailStart)
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

        // ── Comment-range collection (snapshot walk) ─────────────────────────────

        /// <summary>
        /// A contiguous comment region in buffer coordinates.<br/>
        /// <see cref="PrefixLen"/> is the length of the comment opener at the very
        /// start of the region text (<c>//</c>, <c>'</c>, <c>/*</c>, <c>(*</c>) —
        /// anchoring must ignore it so the opener itself doesn't unanchor matches.
        /// </summary>
        internal struct CommentRange
        {
            public int Start;
            public int End;
            public int PrefixLen;
            public CommentRange(int start, int end, int prefixLen)
            {
                Start = start;
                End = end;
                PrefixLen = prefixLen;
            }
        }

        /// <summary>
        /// Walks every snapshot line once, recording plain comment regions while skipping:
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
        internal static void CollectCommentRanges(
            ITextSnapshot snapshot, BufferLanguage lang, List<CommentRange> ranges)
        {
            bool isVb = lang == BufferLanguage.VBNet;
            bool isFSharp = lang == BufferLanguage.FSharp;

            bool inBlock = false;     // inside /* … */ or (* … *)
            bool inDocBlock = false;  // inside /** … */ or /*! … */ (skip silently)
            char close1 = '/';        // block closer first char  ('/' or '*')
            char close2 = '/';        // block closer second char
            int blockStartAbs = -1;   // buffer position of the active block opener
            int blockPrefixLen = 2;   // opener length at the start of the range text

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
                                    line.Start.Position + i + 2,
                                    blockPrefixLen));
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
                                                         line.End.Position, 1));
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
                                                         line.End.Position, 2));
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
        /// tag consideration because their content is tooling metadata, not prose.
        /// </summary>
        private static bool IsLinterAnnotation(string t, int openParenIndex)
        {
            int j = openParenIndex + 2;
            return j < t.Length && t[j] == '$';
        }
    }
}
