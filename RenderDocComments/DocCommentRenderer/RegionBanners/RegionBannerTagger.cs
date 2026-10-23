/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    RegionBannerTagger.cs
 *  Purpose: Renders #region directive lines as full-width banner bars (Plan E)
 *           and #endregion lines as low-key dim end-cap rules. Regions keep
 *           their built-in VS outlining — clicking a banner toggles collapse.
 *
 *  Architecture Role:
 *    Implements ITagger<IntraTextAdornmentTag> with the same per-view cache and
 *    event discipline as CommentBoxTagger. Regions are preprocessor directives,
 *    not comments, so detection is a per-line regex scan per language:
 *      C#   — #region Name / #endregion
 *      VB   — #Region "Name" / #End Region
 *      C++  — #pragma region Name / #pragma endregion
 *
 *  Key Behaviours:
 *    • Click a banner → caret moves to the directive line and the standard
 *      Edit.ToggleOutliningExpansion command toggles the region.
 *    • Caret on a directive line → the raw text returns for editing.
 *    • Visuals come from BannerFactory so they cannot diverge from comment
 *      banners (colours, rule thickness, fonts all shared).
 *
 *  Detection Caveat:
 *    Matching is anchored after line-leading whitespace, so "region" inside
 *    string literals or trailing comments — practically never at column 0 — is
 *    ignored. Same accepted-heuristic class as the comment scanners.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Outlining;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Threading;
using RenderDocComments.DocCommentRenderer.TagScan;
using RenderDocComments.Options;
using static RenderDocComments.DocCommentRenderer.BannerFactory;

namespace RenderDocComments.DocCommentRenderer.RegionBanners
{
    /// <summary>
    /// Renders <c>#region</c> directive lines as banner bars and
    /// <c>#endregion</c> lines as dim end-cap rules across C#, VB.NET, and C++
    /// buffers. Clicking a banner toggles the built-in region outlining.
    /// </summary>
    internal sealed class RegionBannerTagger
        : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;
        private readonly IOutliningManagerService _outliningManagerService;
        private IOutliningManager _outlining;

        private ITextSnapshot _cachedSnapshot;
        private int _cachedSettingsGen = -1;
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> _cachedTags;

        private static int _settingsGeneration = 0;
        private bool _forceEmpty = false;
        private volatile int _caretLine = -1;

        private sealed class LanguageDirectives
        {
            public Regex Region;     // opens a banner (region / if / pragma)
            public Regex EndRegion;  // dim end-cap
            public Regex If;         // opens a banner (conditional)
            public Regex EndIf;      // dim end-cap
            public Regex Pragma;     // opens a banner (warning suppression), nullable
        }

        private static readonly LanguageDirectives CSharpDirectives = new LanguageDirectives
        {
            Region = new Regex(@"^\s*#\s*region\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
            EndRegion = new Regex(@"^\s*#\s*endregion\b", RegexOptions.Compiled),
            If = new Regex(@"^\s*#\s*if\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
            EndIf = new Regex(@"^\s*#\s*endif\b", RegexOptions.Compiled),
            Pragma = new Regex(@"^\s*#\s*pragma\s+warning\s+(?<kind>disable|restore)\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
        };

        private static readonly LanguageDirectives VbDirectives = new LanguageDirectives
        {
            Region = new Regex(@"^\s*#\s*Region\s+""?(?<name>[^""]*)""?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            EndRegion = new Regex(@"^\s*#\s*End\s+Region\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            If = new Regex(@"^\s*#\s*If\s+(?<name>.+?)\s*Then\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            EndIf = new Regex(@"^\s*#\s*End\s+If\b", RegexOptions.Compiled | RegexOptions.IgnoreCase),
            Pragma = new Regex(@"^\s*#\s*(?<kind>Disable|Enable)\s+Warning\b[ \t]*(?<name>.*)$", RegexOptions.Compiled | RegexOptions.IgnoreCase),
        };

        private static readonly LanguageDirectives CppDirectives = new LanguageDirectives
        {
            Region = new Regex(@"^\s*#\s*pragma\s+region\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
            EndRegion = new Regex(@"^\s*#\s*pragma\s+endregion\b", RegexOptions.Compiled),
            If = new Regex(@"^\s*#\s*if\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
            EndIf = new Regex(@"^\s*#\s*endif\b", RegexOptions.Compiled),
            Pragma = new Regex(@"^\s*#\s*pragma\s+warning\s+(?<kind>disable|restore)\b[ \t]*(?<name>.*)$", RegexOptions.Compiled),
        };

        /// <summary>
        /// Raised when the set of banners changes so the editor re-queries tags.
        /// </summary>
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        /// <summary>
        /// Creates a tagger bound to a specific view/buffer pair and subscribes to
        /// buffer changes, caret movement, layout updates, view closure, and
        /// settings broadcasts.
        /// </summary>
        public RegionBannerTagger(ITextBuffer buffer, IWpfTextView view,
            IOutliningManagerService outliningManagerService)
        {
            _buffer = buffer;
            _view = view;
            _outliningManagerService = outliningManagerService;

            try
            {
                var outlining = _outliningManagerService?.GetOutliningManager(_view);
                if (outlining != null)
                {
                    _outlining = outlining;
                    outlining.RegionsCollapsed += OnRegionsChanged;
                    outlining.RegionsExpanded += OnRegionsChanged;
                }
            }
            catch { }

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Yields banner/end-cap tags after applying the premium-gated master
        /// toggle, per-file override, force-empty, caret-hide, and intersection
        /// filters.
        /// </summary>
        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (spans.Count == 0) yield break;
            if (_forceEmpty) yield break;
            if (!RenderDocOptions.Instance.EffectiveRegionBannersEnabled) yield break;

            if (_buffer.Properties.TryGetProperty("RenderDocComments_Disabled", out bool disabled) && disabled)
                yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);
            foreach (var tag in tags)
            {
                // Caret-based hide: the raw directive returns while the user
                // edits its line.
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
            ThreadHelper.ThrowIfNotOnUIThread();
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
        /// Single pass over every line: match the language's region/endregion
        /// regexes and emit one banner or end-cap tag per directive.
        /// </summary>
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var result = new List<TagSpan<IntraTextAdornmentTag>>();

            LanguageDirectives directives;
            switch (CommentTagLocator.GetLanguage(_buffer))
            {
                case CommentTagLocator.BufferLanguage.VBNet:
                    directives = VbDirectives; break;
                case CommentTagLocator.BufferLanguage.Cpp:
                    directives = CppDirectives; break;
                default:
                    directives = CSharpDirectives; break;
            }

            BannerFactory.ResolveStyle(
                _view, out var fontFamily, out double fontSize,
                out var textBrush, out var ruleColor);
            double ruleThickness = RenderDocOptions.Instance.EffectiveBannerRuleThickness;
            double barHeight = RenderDocOptions.Instance.EffectiveBannerBarHeight;

            int lineCount = snapshot.LineCount;
            for (int ln = 0; ln < lineCount; ln++)
            {
                var line = snapshot.GetLineFromLineNumber(ln);
                string text = line.GetText();
                if (text.Length == 0) continue;

                double indent = MeasureIndent(text);

                // Span covers the WHOLE line (leading whitespace included) —
                // mirroring the multi-line comment-banner pattern, whose origin
                // at line start positions the banner exactly at the code's
                // indent level. A span starting at the '#' leaves the leading
                // whitespace visible and shifts the banner off-level.
                var span = new SnapshotSpan(snapshot, line.Start, line.Length);

                // Banner-opening directives, in priority order: region → if →
                // pragma warning. First match wins.
                string bannerName = null;

                var region = directives.Region.Match(text);
                if (region.Success)
                {
                    bannerName = region.Groups["name"].Success
                        ? region.Groups["name"].Value.Trim()
                        : string.Empty;
                }
                else
                {
                    var ifMatch = directives.If.Match(text);
                    if (ifMatch.Success)
                    {
                        bannerName = ifMatch.Groups["name"].Value.Trim();
                    }
                    else if (directives.Pragma != null)
                    {
                        var pragma = directives.Pragma.Match(text);
                        if (pragma.Success)
                        {
                            bool disabling = pragma.Groups["kind"].Value.Equals(
                                "disable", StringComparison.OrdinalIgnoreCase);
                            string codes = pragma.Groups["name"].Value.Trim();
                            bannerName = (disabling ? "Warnings disabled" : "Warnings restored")
                                + (codes.Length > 0 ? " — " + codes : string.Empty);
                        }
                    }
                }

                if (bannerName != null)
                {

                    // ComputeWidth already subtracts the directive's column offset;
                    // passing the indent here would subtract it twice and cut the
                    // banner short.
                    FrameworkElement visual;
                    if (IsRegionCollapsed(span, snapshot))
                    {
                        // Collapsed: a plain label — no lines, no trail.
                        visual = new Border
                        {
                            Margin = new Thickness(indent + 2, 0, 2, 0),
                            HorizontalAlignment = HorizontalAlignment.Left,
                            Child = new TextBlock
                            {
                                Text = bannerName.Length > 0 ? bannerName : "Region",
                                FontFamily = fontFamily,
                                FontSize = fontSize,
                                FontWeight = FontWeights.SemiBold,
                                Foreground = textBrush,
                                Margin = new Thickness(0, 1, 0, 1),
                            },
                        };
                    }
                    else
                    {
                        try
                        {
                            visual = CreateRegionBanner(
                                bannerName, fontFamily, fontSize, textBrush, ruleColor,
                                ruleThickness, barHeight, indent, snapshot, span);
                        }
                        catch
                        {
                            visual = null;   // one bad visual must not blank the file
                        }
                    }

                    var tag = new IntraTextAdornmentTag(visual, null, PositionAffinity.Predecessor);
                    result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
                    continue;
                }

                if (directives.EndRegion.Match(text).Success ||
                    directives.EndIf.Match(text).Success)
                {
                    FrameworkElement visual = null;
                    try
                    {
                        visual = CreateEndCap(ruleColor, ruleThickness, indent, snapshot, span);
                    }
                    catch
                    {
                        visual = null;   // one bad visual must not blank the file
                    }
                    if (visual != null)
                    {
                        var tag = new IntraTextAdornmentTag(visual, null, PositionAffinity.Predecessor);
                        result.Add(new TagSpan<IntraTextAdornmentTag>(span, tag));
                    }
                }
            }

            return result;
        }

        // ── Visuals ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Builds the region banner: the region name (uppercased, semibold) plus a
        /// fading hairline, spanning the directive line. Transparent background
        /// keeps it hit-testable; clicking toggles the built-in outlining.
        /// </summary>
        private FrameworkElement CreateRegionBanner(
            string name, FontFamily fontFamily, double fontSize,
            Brush textBrush, Color ruleColor,
            double ruleThickness, double barHeight, double indentPx,
            ITextSnapshot snapshot, SnapshotSpan span)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            bool hasTitle = name.Length > 0;

            var grid = new Grid
            {
                ClipToBounds = true,
                Background = Brushes.Transparent,   // hit-testable for the click
                Cursor = Cursors.Hand,
            };

            if (hasTitle)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                var title = new TextBlock
                {
                    Text = name,
                    FontFamily = fontFamily,
                    FontSize = fontSize,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = textBrush,
                    Margin = new Thickness(0, 1, 0, 1),
                };
                Grid.SetColumn(title, 0);
                grid.Children.Add(title);

                var trail = BuildTrail(RailStyle.Single, ruleColor, ruleThickness, fullWidth: false);
                trail.Margin = new Thickness(14, 0, 0, 0);
                trail.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(trail, 1);
                grid.Children.Add(trail);
            }
            else
            {
                // Unnamed region: the line alone, full width.
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                var trail = BuildTrail(RailStyle.Single, ruleColor, ruleThickness, fullWidth: true);
                Grid.SetColumn(trail, 0);
                grid.Children.Add(trail);
            }

            var border = new Border
            {
                Padding = new Thickness(0, 1, 0, 1),
                Margin = new Thickness(indentPx + 2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = grid,
            };
            double barWidth = ComputeWidth(snapshot, span.Start, indentPx);
            if (!double.IsInfinity(barWidth))
                border.Width = barWidth;
            if (barHeight > 0)
                border.Height = barHeight;

            var clicked = span;
            border.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                ThreadHelper.ThrowIfNotOnUIThread();
                ToggleOutlining(clicked);
            };

            return border;
        }

        /// <summary>
        /// Builds the dim end-cap rule for <c>#endregion</c> — deliberately
        /// low-key so ends do not compete with headers.
        /// </summary>
        private FrameworkElement CreateEndCap(
            Color ruleColor, double ruleThickness, double indentPx,
            ITextSnapshot snapshot, SnapshotSpan span)
        {
            var grid = new Grid { Background = Brushes.Transparent };
            grid.Children.Add(new Border
            {
                // Same thickness as the region banner's trail — dimmed by the
                // fade alpha, not by being thinner.
                Height = Math.Max(1.0, ruleThickness),
                Background = FadeBrush(ruleColor, 0.45),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 1, 0, 1),
            });

            var border = new Border
            {
                Margin = new Thickness(indentPx + 2, 0, 2, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Child = grid,
            };
            // Same zero-desired-width trap as the divider trails: an unstretched
            // adornment needs an explicit width or the line never renders.
            double width = ComputeWidth(snapshot, span.Start, indentPx);
            if (!double.IsInfinity(width))
                border.Width = width;
            return border;
        }

        /// <summary>True when the region whose directive starts in
        /// <paramref name="span"/> is currently collapsed in the outlining
        /// manager (unknown → false, banner renders normally).</summary>
        private bool IsRegionCollapsed(SnapshotSpan span, ITextSnapshot snapshot)
        {
            var outlining = _outlining;
            if (outlining == null) return false;
            try
            {
                foreach (var region in outlining.GetAllRegions(span))
                {
                    var extent = region.Extent.GetSpan(snapshot);
                    if (extent.Start <= span.Start && extent.End >= span.End)
                        return region.IsCollapsed;
                }
            }
            catch { }
            return false;
        }

        /// <summary>Outlining changed (region collapsed/expanded) — rebuild so
        /// banners swap between the full bar and the collapsed label.</summary>
        private void OnRegionsChanged(object sender, EventArgs e)
        {
            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = _buffer.CurrentSnapshot;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(snap, 0, snap.Length)));
        }

        /// <summary>
        /// Places the caret on the directive line and executes the standard
        /// outlining toggle. All failures are swallowed — the banner is
        /// informational when outlining is unavailable.
        /// </summary>
        private void ToggleOutlining(SnapshotSpan directiveSpan)
        {
            try
            {
                // Mouse handler — always already on the UI thread; the explicit
                // check documents that invariant for the threading analyser.
                ThreadHelper.ThrowIfNotOnUIThread();
                _view.Caret.MoveTo(new SnapshotPoint(directiveSpan.Snapshot, directiveSpan.Start));
                _view.VisualElement.Focus();

                var dte = Package.GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                dte?.ExecuteCommand("Edit.ToggleOutliningExpansion");
            }
            catch
            {
                // Outlining unavailable for this buffer — informational banner only.
            }
        }

        /// <summary>
        /// Measures the pixel width of a line's leading whitespace (tabs counted
        /// as four columns) so banners indent with the code — same approach as
        /// the other adornment taggers.
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

        /// <summary>
        /// Maximum banner width: viewport minus the directive's column offset and
        /// indent, minus a small margin.
        /// </summary>
        private double ComputeWidth(ITextSnapshot snapshot, int spanStart, double indentPx)
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

        /// <summary>Re-queries tags when the viewport resizes.</summary>
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

        /// <summary>Caret-hide bookkeeping: re-queries the affected lines when the
        /// caret moves, letting <see cref="GetTags"/> suppress/restore banners on
        /// those lines.</summary>
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

        /// <summary>Two-phase invalidation on settings changes: first clear
        /// (suppressing tags), then rebuild on a dispatcher callback — preventing
        /// stale-banner flashes.</summary>
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

        /// <summary>Unsubscribes all events; called when the editor disposes the
        /// tagger.</summary>
        public void Dispose()
        {
            if (_outlining != null)
            {
                _outlining.RegionsCollapsed -= OnRegionsChanged;
                _outlining.RegionsExpanded -= OnRegionsChanged;
            }
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}
