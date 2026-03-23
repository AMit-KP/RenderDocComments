using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentTagger
        : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly IWpfTextView _view;
        private ITextSnapshot _cachedSnapshot;
        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> _cachedTags;
        private int _caretLine = -1;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public DocCommentAdornmentTagger(ITextBuffer buffer, IWpfTextView view)
        {
            _buffer = buffer;
            _view = view;
            _buffer.Changed += OnBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
        }

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(
            NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0) yield break;

            var snapshot = spans[0].Snapshot;
            var tags = GetOrBuildTags(snapshot);

            foreach (var tag in tags)
            {
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

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> GetOrBuildTags(ITextSnapshot snapshot)
        {
            if (_cachedSnapshot == snapshot && _cachedTags != null)
                return _cachedTags;
            _cachedSnapshot = snapshot;
            _cachedTags = BuildTags(snapshot);
            return _cachedTags;
        }

        private static readonly Regex DocLineRegex =
            new Regex(@"^\s*///", RegexOptions.Compiled);

        private IReadOnlyList<TagSpan<IntraTextAdornmentTag>> BuildTags(ITextSnapshot snapshot)
        {
            var result = new List<TagSpan<IntraTextAdornmentTag>>();
            int lineCount = snapshot.LineCount;
            int i = 0;

            while (i < lineCount)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                if (DocLineRegex.IsMatch(line.GetText()))
                {
                    var blockLines = new List<ITextSnapshotLine>();
                    while (i < lineCount && DocLineRegex.IsMatch(snapshot.GetLineFromLineNumber(i).GetText()))
                        blockLines.Add(snapshot.GetLineFromLineNumber(i++));

                    var rawBlock = string.Join("\n", blockLines.Select(l => l.GetText()));
                    var parsed = DocCommentParser.Parse(rawBlock);
                    if (parsed == null || !parsed.IsValid) continue;

                    // #3 — measure indent as character count × char width
                    double indentWidth = MeasureIndent(blockLines[0].GetText());

                    var startPos = blockLines[0].Start;
                    var endPos = blockLines[blockLines.Count - 1].End;
                    var blockSpan = new SnapshotSpan(snapshot, Span.FromBounds(startPos, endPos));

                    var tag = new DocCommentAdornmentTag(parsed, _view, indentWidth);
                    result.Add(new TagSpan<IntraTextAdornmentTag>(blockSpan, tag));
                }
                else
                {
                    i++;
                }
            }

            return result;
        }

        /// <summary>
        /// Counts leading spaces/tabs and converts to approximate pixel width
        /// using the editor's character width (assumes monospace font).
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
                var charWidth = _view.FormattedLineSource?.ColumnWidth;
                if (charWidth.HasValue && charWidth.Value > 0)
                    return spaces * charWidth.Value;
            }
            catch { }

            // Fallback: ~7.2px per char at 13pt Consolas
            return spaces * 7.2;
        }

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
            int newLine = e.NewPosition.BufferPosition.GetContainingLine().LineNumber;
            if (newLine == _caretLine) return;
            int old = _caretLine;
            _caretLine = newLine;

            var snap = _buffer.CurrentSnapshot;
            void Invalidate(int ln)
            {
                if (ln < 0 || ln >= snap.LineCount) return;
                var l = snap.GetLineFromLineNumber(ln);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                    new SnapshotSpan(snap, l.Start, l.LengthIncludingLineBreak)));
            }
            Invalidate(old);
            Invalidate(newLine);
        }

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
        }
    }
}