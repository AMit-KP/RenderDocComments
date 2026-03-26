using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentTagger
        : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;

        private ITextSnapshot _cachedSnapshot;
        private int _cachedSettingsGen = -1;
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> _cachedTags;

        private static int _settingsGeneration = 0;
        private bool _forceEmpty = false;

        private int _caretLine = -1;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DocCommentAdornmentTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;

            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // ── GetTags ───────────────────────────────────────────────────────────────

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;
            if (_forceEmpty) yield break;
            if (!RenderDocOptions.Instance.RenderEnabled) yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);

            foreach (var tag in tags)
            {
                if (RenderDocOptions.Instance.EffectiveGlyphToggle)
                {
                    if (DocCommentToggleState.IsHidden(
                            new SnapshotSpan(snapshot, tag.Span)))
                        continue;
                }
                else
                {
                    if (_caretLine >= 0)
                    {
                        int s = snapshot.GetLineNumberFromPosition(tag.Span.Start);
                        int e = snapshot.GetLineNumberFromPosition(tag.Span.End);
                        if (_caretLine >= s && _caretLine <= e) continue;
                    }
                }

                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tag.Span)))
                    yield return tag;
            }
        }


        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> GetOrBuildTags(
            ITextSnapshot snapshot)
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

        private static readonly Regex DocLineRegex =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(
            ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            int lineCount = snapshot.LineCount;
            int i = 0;

            while (i < lineCount)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                if (!DocLineRegex.IsMatch(line.GetText())) { i++; continue; }

                var blockLines = new List<ITextSnapshotLine>();
                while (i < lineCount &&
                       DocLineRegex.IsMatch(snapshot.GetLineFromLineNumber(i).GetText()))
                    blockLines.Add(snapshot.GetLineFromLineNumber(i++));

                var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                var parsed = DocCommentParser.Parse(rawBlock);
                if (parsed == null || !parsed.IsValid) continue;

                double indentWidth = MeasureIndent(blockLines[0].GetText());

                var startPos = blockLines[0].Start;
                var endPos = blockLines[blockLines.Count - 1].End;
                var blockSpan = new SnapshotSpan(snapshot,
                                    Span.FromBounds(startPos, endPos));

                var tag = new DocCommentAdornmentTag(parsed, _view, indentWidth);
                result.Add(new TagSpan<IntraTextAdornmentTag>(blockSpan, tag));
            }

            return result;
        }

        // ── Indent measurement ────────────────────────────────────────────────────

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

        // ── Event handlers ────────────────────────────────────────────────────────

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            _cachedSnapshot = null;
            _cachedTags = null;
            var snap = e.After;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(snap, 0, snap.Length)));
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (RenderDocOptions.Instance.EffectiveGlyphToggle) return;

            int newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (newLine == _caretLine) return;
            int old = _caretLine;
            _caretLine = newLine;

            var snap = _buffer.CurrentSnapshot;
            var cached = _cachedTags;

            void InvalidateBlock(int ln)
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
                            TagsChanged?.Invoke(this,
                                new SnapshotSpanEventArgs(ts.Span));
                            return;
                        }
                    }
                }
                var l = snap.GetLineFromLineNumber(ln);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snap, l.Start, l.LengthIncludingLineBreak)));
            }

            InvalidateBlock(old);
            InvalidateBlock(newLine);
        }

        /// <summary>
        /// Settings changed (options saved, or theme changed with auto-refresh on).
        /// Bump the generation counter so GetOrBuildTags treats the current cache as
        /// stale and rebuilds every tag with the new settings.
        /// </summary>
        private void OnSettingsChanged(object sender, EventArgs e)
        {
            System.Threading.Interlocked.Increment(ref _settingsGeneration);
            _cachedSnapshot = null;
            _cachedTags = null;

            var snap = _buffer.CurrentSnapshot;
            var fullSpan = new SnapshotSpan(snap, 0, snap.Length);

            _forceEmpty = true;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(fullSpan));

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

        // ── IDisposable ───────────────────────────────────────────────────────────

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }
}