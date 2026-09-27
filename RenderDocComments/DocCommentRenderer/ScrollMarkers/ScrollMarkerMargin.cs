/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    ScrollMarkerMargin.cs
 *  Purpose: Paints one coloured tick in the editor's vertical scroll bar for
 *           every eligible comment tag (TODO, FIXME, …) — the same strip where
 *           breakpoints and Error List entries appear — so flagged comments are
 *           visible from anywhere in the file. Hovering a tick shows the tag
 *           text; clicking jumps to and centres the comment.
 *
 *  Architecture Role:
 *    Implements IWpfTextViewMargin authored into VS's shared scroll-bar container
 *    (PredefinedMarginNames.VerticalScrollBarContainer, ordered after the vertical
 *    scroll bar itself). Tick Ys come from IVerticalScrollBar.GetYCoordinateOfScrollMapPosition —
 *    the scrollbar's own mapping (the one that places breakpoint and current-line
 *    markers), which correctly accounts for viewport overhang, elisions, and map
 *    mode. Raw IScrollMap fractions are NOT used: they span the scrollable span
 *    rather than the physical track, which pushes ticks toward the bottom.
 *
 *  Detection & Eligibility:
 *    The occurrence list comes from the shared pipeline (TagScan/CommentTagLocator)
 *    with the pill feature's eligibility rules applied verbatim — per-tag
 *    enablement, pill click-dismissal (Pills style only), and the single-line
 *    restriction — so ticks and badges can never disagree about what exists.
 *    Tick colour follows the same effective-colour path as pills
 *    (Premium per-tag overrides included).
 *
 *  Caching & Re-layout:
 *    Occurrences are cached per snapshot (tracking points) and invalidated by
 *    buffer changes and SettingsChangedBroadcast. IScrollMap.MappingChanged and
 *    IWpfTextView.LayoutChanged trigger Y-fraction recomputation only — cached
 *    occurrence positions survive scrolling and viewport moves.
 *
 *  Key Classes:
 *    ScrollMarkerMargin         — the margin (Canvas of 3px-high tick rectangles).
 *    ScrollMarkerMarginProvider — MEF export wiring the margin into every
 *                                 editable document view of the supported languages.
 *
 *  Dependencies:
 *    • TagScan/CommentTagLocator.cs — shared tag detection pipeline.
 *    • TagBadges/TagBadgeToggleState.cs — click-dismissal parity with pills.
 *    • RenderDocOptions.cs         — EffectiveScrollMarkersEnabled (Premium-gated),
 *                                    TagBadgesEnabled, EffectiveTag*.
 *    • SettingsChangedBroadcast.cs — rebuild notifications.
 *
 *  When to Edit:
 *    • Ticks missing / misplaced — check GetFractionAtBufferPosition usage and
 *      UpdatePositions clamping.
 *    • Ticks disagree with pills — the eligibility rules live in BuildOccurrences;
 *      keep them in lockstep with CommentTagBadgeTagger.BuildTags.
 *    • Strip width / tick height — StripWidth / TickHeight.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Threading;
using Microsoft.VisualStudio.Utilities;
using RenderDocComments.DocCommentRenderer.TagBadges;
using RenderDocComments.DocCommentRenderer.TagScan;
using RenderDocComments.Options;

namespace RenderDocComments.DocCommentRenderer.ScrollMarkers
{
    /// <summary>
    /// Vertical scroll-bar margin rendering one coloured tick per eligible comment
    /// tag across C#, VB.NET, F#, and C++ buffers.
    /// </summary>
    /// <remarks>
    /// <para><b>Positioning:</b> each tick sits at the Y coordinate the vertical
    /// scroll bar itself reports for the tag's buffer position
    /// (<see cref="IVerticalScrollBar.GetYCoordinateOfScrollMapPosition"/>) — the same
    /// mapping that places VS's breakpoint and current-line markers, so ticks
    /// always align with their comment's location in the strip, in both bar-mode
    /// and map-mode scroll bars.</para>
    /// <para><b>Caching:</b> occurrences (as tracking points) are cached per
    /// snapshot; scrolling and viewport changes only recompute Y coordinates.
    /// <see cref="IScrollMap.MappingChanged"/> covers scroll-in-map-mode and
    /// mapping shifts; <see cref="IWpfTextView.LayoutChanged"/> covers edits,
    /// resizes, and elision changes.</para>
    /// </remarks>
    internal sealed class ScrollMarkerMargin : IWpfTextViewMargin
    {
        /// <summary>Margin identity — matches the provider's <c>[Name]</c>.</summary>
        internal const string MarginName = "RenderDocCommentsScrollMarkers";

        /// <summary>Width of the marker strip, in WPF device-independent pixels.</summary>
        private const double StripWidth = 4.0;

        /// <summary>Height of one tag tick, in WPF device-independent pixels.</summary>
        private const double TickHeight = 3.0;

        /// <summary>One located tag: tracked buffer position + display data.</summary>
        private sealed class Occurrence
        {
            /// <summary>Tracks the tag token's start across edits above it.</summary>
            public ITrackingPoint Point;

            /// <summary>Canonical tag name (colour + tooltip).</summary>
            public string CanonicalTag;

            /// <summary>Comment text following the tag (tooltip tail).</summary>
            public string TailText;
        }

        private readonly IWpfTextView _view;
        private IWpfTextViewHost _host;
        private readonly Canvas _canvas;

        /// <summary>
        /// The view's vertical scroll bar, whose own mapping positions the ticks.
        /// Resolved lazily — sibling margins may build after ours.
        /// </summary>
        private IVerticalScrollBar _scrollBar;
        private IScrollMap _scrollMap;

        /// <summary>
        /// The scrollbar margin's visual — the coordinate space its Y values are
        /// relative to. This margin is a SIBLING of it in the scroll bar container,
        /// so track Ys must be TranslatePoint-ed into our canvas before use.
        /// </summary>
        private FrameworkElement _barVisual;

        private ITextSnapshot _occSnapshot;
        private int _cachedSettingsGen = -1;
        private List<Occurrence> _occurrences;
        private readonly List<Rectangle> _ticks = new List<Rectangle>();

        /// <summary>Bumped on every settings broadcast — mirrors the taggers' cache key.</summary>
        private static int _settingsGeneration = 0;

        private bool _disposed;

        /// <summary>
        /// Creates the margin for a view: a transparent strip canvas positioned via
        /// the view's vertical scroll bar, subscribed to layout/mapping/buffer/
        /// settings events.
        /// </summary>
        /// <param name="view">The WPF text view whose scroll bar hosts the strip.</param>
        /// <param name="host">The view host — source of the vertical scroll bar margin.</param>
        public ScrollMarkerMargin(IWpfTextView view, IWpfTextViewHost host)
        {
            _view = view;
            _host = host;

            // Transparent (not null) so the strip hit-tests its own area while the
            // actual scrollbar thumb — a separate margin — stays fully interactive.
            _canvas = new Canvas
            {
                Background = Brushes.Transparent,
                ClipToBounds = true,
                Width = StripWidth,
            };
            _canvas.SizeChanged += OnCanvasSizeChanged;

            _view.LayoutChanged += OnLayoutChanged;
            _view.TextBuffer.Changed += OnBufferChanged;
            _view.Closed += OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;

            // First paint — LayoutChanged usually follows, but a quiet view
            // (already laid out before we attached) would otherwise stay empty.
            PostToUpdate(System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // ── IWpfTextViewMargin ────────────────────────────────────────────────────

        /// <summary>Strip width; vertical margins report their width here.</summary>
        public double MarginSize => StripWidth;

        /// <summary>The strip is functional until the view or margin is torn down.</summary>
        public bool Enabled => !_disposed;

        /// <summary>The transparent tick canvas.</summary>
        public FrameworkElement VisualElement => _canvas;

        /// <summary>
        /// Returns this margin when queried by name; null for any other margin.
        /// </summary>
        public ITextViewMargin GetTextViewMargin(string marginName)
            => marginName == MarginName ? this : null;

        // ── Refresh ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Full refresh: reapplies the feature gates, rebuilds the occurrence list
        /// when the snapshot or settings changed, and repositions the ticks.
        /// </summary>
        private void UpdateAll()
        {
            if (_disposed) return;

            if (!IsFeatureEnabled())
            {
                ClearTicks();
                return;
            }

            var snapshot = _view.TextSnapshot;
            if (_occSnapshot != snapshot ||
                _occurrences == null ||
                _cachedSettingsGen != _settingsGeneration)
            {
                _occSnapshot = snapshot;
                _cachedSettingsGen = _settingsGeneration;
                _occurrences = BuildOccurrences(snapshot);
                RebuildTicks();
            }

            UpdatePositions();
        }

        /// <summary>
        /// The same gates the pill feature applies — plus the feature's own
        /// Premium-gated master toggle — so ticks and badges can never disagree
        /// about what exists.
        /// </summary>
        private bool IsFeatureEnabled()
        {
            var opts = RenderDocOptions.Instance;
            if (!opts.EffectiveScrollMarkersEnabled || !opts.TagBadgesEnabled) return false;

            if (_view.Properties.TryGetProperty("RenderDocComments_Disabled", out bool disabled) && disabled)
                return false;

            return true;
        }

        /// <summary>
        /// Single full pass over the snapshot: collect comment ranges via the shared
        /// locator and keep every tag the pill feature would render — anchored,
        /// known, enabled, not click-dismissed (Pills style), in a single-line
        /// comment (multi-line blocks belong to the comment-box feature).
        /// </summary>
        private List<Occurrence> BuildOccurrences(ITextSnapshot snapshot)
        {
            var opts = RenderDocOptions.Instance;
            var list = new List<Occurrence>();

            var lang = CommentTagLocator.GetLanguage(_view.TextBuffer);
            var ranges = new List<CommentTagLocator.CommentRange>();
            CommentTagLocator.CollectCommentRanges(snapshot, lang, ranges);

            foreach (var range in ranges)
            {
                int len = range.End - range.Start;
                if (len <= 0) continue;
                string text;
                try { text = snapshot.GetText(range.Start, len); }
                catch { continue; }

                // Parity with pills: a pill never renders inside a multi-line block.
                if (snapshot.GetLineNumberFromPosition(range.Start) !=
                    snapshot.GetLineNumberFromPosition(range.End - 1))
                    continue;

                foreach (var m in CommentTagLocator.Locate(text, range.PrefixLen))
                {
                    if (!opts.EffectiveTagEnabled(m.CanonicalTag)) continue;

                    // Pills honour click-dismissal; cards deliberately do not —
                    // mirror the active style so ticks match what actually renders.
                    if (opts.EffectiveTagStyle == "Pills")
                    {
                        var line = snapshot.GetLineFromLineNumber(
                            snapshot.GetLineNumberFromPosition(range.Start + m.MatchIndex));
                        if (TagBadgeToggleState.IsHidden(line.GetText().Trim())) continue;
                    }

                    list.Add(new Occurrence
                    {
                        Point = snapshot.CreateTrackingPoint(
                            range.Start + m.MatchIndex, PointTrackingMode.Positive),
                        CanonicalTag = m.CanonicalTag,
                        TailText = m.TailText,
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// Recreates the tick rectangles for the current occurrence list — called
        /// when occurrences or their colours change, not on scroll.
        /// </summary>
        private void RebuildTicks()
        {
            ClearTicks();

            var opts = RenderDocOptions.Instance;
            double width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : StripWidth;

            foreach (var occ in _occurrences)
            {
                Color bg = opts.EffectiveTagColor(occ.CanonicalTag);
                var brush = new SolidColorBrush(bg);
                brush.Freeze();

                var tick = new Rectangle
                {
                    Fill = brush,
                    Height = TickHeight,
                    Width = width,
                    RadiusX = 1,
                    RadiusY = 1,
                    Cursor = Cursors.Hand,
                    Tag = occ,
                    ToolTip = string.IsNullOrEmpty(occ.TailText)
                        ? $"{occ.CanonicalTag} — {TagBadgeCatalog.GetDescription(occ.CanonicalTag)}"
                        : $"{occ.CanonicalTag} — {TagBadgeCatalog.GetDescription(occ.CanonicalTag)}\n{occ.TailText}",
                };
                tick.MouseLeftButtonUp += OnTickClicked;

                _canvas.Children.Add(tick);
                _ticks.Add(tick);
            }
        }

        /// <summary>Removes every tick from the strip.</summary>
        private void ClearTicks()
        {
            _canvas.Children.Clear();
            _ticks.Clear();
        }

        // ── Positioning ───────────────────────────────────────────────────────────

        /// <summary>
        /// Resolves the view's vertical scroll bar — the margin whose track mapping
        /// positions the ticks — and hooks its scroll map's mapping-changed event.
        /// Returns null until the sibling margin exists.
        /// </summary>
        private IVerticalScrollBar GetScrollBar()
        {
            if (_scrollBar != null) return _scrollBar;
            if (_disposed || _host == null) return null;

            var barMargin = _host.GetTextViewMargin(PredefinedMarginNames.VerticalScrollBar);
            var scrollBar = barMargin as IVerticalScrollBar;
            if (scrollBar == null) return null;

            _scrollBar = scrollBar;
            _scrollMap = scrollBar.Map;
            _scrollMap.MappingChanged += OnMappingChanged;
            _barVisual = (barMargin as IWpfTextViewMargin)?.VisualElement;
            return _scrollBar;
        }

        private void UpdatePositions()
        {
            if (_disposed || _occurrences == null) return;

            // Only trust cached positions on the snapshot they were built for —
            // a newer snapshot gets a full rebuild from the next layout pass.
            var snapshot = _view.TextSnapshot;
            if (_occSnapshot != snapshot) return;

            var scrollBar = GetScrollBar();
            if (scrollBar == null || _scrollMap == null) return;

            double height = _canvas.ActualHeight;
            if (height <= 0) return;

            double width = _canvas.ActualWidth > 0 ? _canvas.ActualWidth : StripWidth;

            for (int i = 0; i < _occurrences.Count && i < _ticks.Count; i++)
            {
                var point = _occurrences[i].Point.GetPoint(snapshot);

                // The scrollbar's own track Y for this position — the exact value
                // VS's enhanced scrollbar feeds its caret line and marks (accounts
                // for viewport overhang, elisions, and map mode). Those draw as
                // children of the SCROLLBAR margin; our strip is a sibling margin,
                // so the value must be translated from the scrollbar margin's
                // visual into our canvas before it can be used as a canvas Y.
                double y = scrollBar.GetYCoordinateOfBufferPosition(point);
                if (_barVisual != null)
                {
                    var translated = _barVisual.TranslatePoint(new Point(0, y), _canvas);
                    if (!double.IsNaN(translated.Y) && !double.IsInfinity(translated.Y))
                        y = translated.Y;
                }

                // Caret-line convention: round to the half-pixel grid so ticks sit
                // on the same rows VS draws its own markers on.
                double center = Math.Round(y - 0.5) + 0.5;
                double top = center - TickHeight / 2.0;
                if (top < 0) top = 0;
                if (top > height - TickHeight) top = height - TickHeight;

                var tick = _ticks[i];
                tick.Width = width;
                Canvas.SetTop(tick, top);
            }
        }

        // ── Interaction ───────────────────────────────────────────────────────────

        /// <summary>
        /// Click on a tick: caret to the occurrence (revealing the raw comment on
        /// that line, matching the taggers' caret-hide model) and centre the view.
        /// </summary>
        private void OnTickClicked(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_disposed) return;
            if (!(sender is FrameworkElement fe) || !(fe.Tag is Occurrence occ)) return;

            var snapshot = _view.TextSnapshot;
            var point = occ.Point.GetPoint(snapshot);

            _view.Caret.MoveTo(point);
            _view.ViewScroller.EnsureSpanVisible(
                new SnapshotSpan(snapshot, point.Position, 0),
                EnsureSpanVisibleOptions.AlwaysCenter);
            _view.VisualElement.Focus();
        }

        // ── Event handlers ────────────────────────────────────────────────────────

        /// <summary>Layout pass: edits, resizes, elision changes — full refresh.</summary>
        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
            => UpdateAll();

        /// <summary>Scroll in map mode / mapping shifts — recompute Y fractions only.</summary>
        private void OnMappingChanged(object sender, EventArgs e)
            => UpdatePositions();

        /// <summary>
        /// Buffer edit: force an occurrence rebuild at the next update. LayoutChanged
        /// normally drives it; the dispatcher callback covers layouts that race ahead.
        /// </summary>
        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_disposed) return;
            _occSnapshot = null;
            PostToUpdate(System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Strip resize — tick Ys are fractions of the height.</summary>
        private void OnCanvasSizeChanged(object sender, SizeChangedEventArgs e)
            => UpdatePositions();

        /// <summary>
        /// Settings changed (options dialog, theme refresh, pill click-dismissal):
        /// bump the generation — the next update rebuilds occurrences and colours.
        /// Marshalled to the UI thread; the broadcast can fire from background code.
        /// </summary>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _settingsGeneration);
            if (_disposed) return;
            PostToUpdate(System.Windows.Threading.DispatcherPriority.Normal);
        }

        /// <summary>
        /// Schedules <see cref="UpdateAll"/> on the view's UI thread at the given
        /// priority via the package's JoinableTaskFactory — the same marshalling
        /// discipline the taggers use, avoiding raw dispatcher APIs that can
        /// deadlock during shutdown. A no-op when the margin is already disposed.
        /// </summary>
        private void PostToUpdate(System.Windows.Threading.DispatcherPriority priority)
        {
            if (_disposed) return;
            _ = RenderDocCommentsPackage.SharedJoinableTaskFactory.RunAsync(async () =>
            {
                await RenderDocCommentsPackage.SharedJoinableTaskFactory
                    .WithPriority(_view.VisualElement.Dispatcher, priority)
                    .SwitchToMainThreadAsync();

                UpdateAll();
            });
        }

        /// <summary>Detaches every handler and empties the strip on view closure.</summary>
        private void OnViewClosed(object sender, EventArgs e)
            => Detach();

        /// <summary>
        /// Unsubscribes all events and clears the strip; called by the editor when
        /// it disposes the margin, and on view closure.
        /// </summary>
        public void Dispose()
            => Detach();

        private void Detach()
        {
            if (_disposed) return;
            _disposed = true;

            _view.LayoutChanged -= OnLayoutChanged;
            if (_scrollMap != null) _scrollMap.MappingChanged -= OnMappingChanged;
            _view.TextBuffer.Changed -= OnBufferChanged;
            _view.Closed -= OnViewClosed;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;

            _canvas.SizeChanged -= OnCanvasSizeChanged;
            ClearTicks();
            _scrollBar = null;
            _scrollMap = null;
            _barVisual = null;
            _host = null;
            _occSnapshot = null;
            _occurrences = null;
        }
    }

    /// <summary>
    /// MEF-exported factory authoring <see cref="ScrollMarkerMargin"/> into the
    /// editor's vertical scroll bar container for the supported languages.
    /// </summary>
    /// <remarks>
    /// <para>The margin sits directly after the vertical scroll bar inside
    /// <see cref="PredefinedMarginNames.VerticalScrollBarContainer"/> — the same
    /// strip the debugger paints its markers in. Restricted to editable document
    /// views of the same content types the pill/card taggers serve, so ticks and
    /// badges always appear together.</para>
    /// </remarks>
    [Export(typeof(IWpfTextViewMarginProvider))]
    [Name(ScrollMarkerMargin.MarginName)]
    [MarginContainer(PredefinedMarginNames.VerticalScrollBarContainer)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [Order(After = PredefinedMarginNames.VerticalScrollBar)]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("FSharp")]
    [ContentType("F#")]
    [ContentType("C/C++")]
    internal sealed class ScrollMarkerMarginProvider : IWpfTextViewMarginProvider
    {
        /// <summary>
        /// Creates the per-view <see cref="ScrollMarkerMargin"/>.
        /// </summary>
        /// <param name="textViewHost">The hosting view host (source of the vertical scroll bar).</param>
        /// <param name="container">The requesting container margin (unused).</param>
        /// <returns>The margin, or null for non-WPF views.</returns>
        public IWpfTextViewMargin CreateMargin(IWpfTextViewHost textViewHost, IWpfTextViewMargin container)
        {
            var view = textViewHost?.TextView as IWpfTextView;
            if (view == null) return null;

            return view.Properties.GetOrCreateSingletonProperty(
                typeof(ScrollMarkerMargin),
                () => new ScrollMarkerMargin(view, textViewHost));
        }
    }
}
