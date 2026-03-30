using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace RenderDocComments.DocCommentRenderer
{
    // ── Tag ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Margin glyph tag — one per doc-comment block first line.
    /// Carries the block span so the glyph knows which block to toggle.
    /// </summary>
    internal sealed class DocCommentGlyphTag : IGlyphTag
    {
        public SnapshotSpan BlockSpan
        {
            get;
        }
        public DocCommentGlyphTag(SnapshotSpan blockSpan)
        {
            BlockSpan = blockSpan;
        }
    }

    // ── Tagger ───────────────────────────────────────────────────────────────────

    /// <summary>
    /// Emits <see cref="DocCommentGlyphTag"/> on the first line of each doc-comment
    /// block when the Premium glyph toggle feature is enabled.
    /// </summary>
    internal sealed class DocCommentGlyphTagger : ITagger<DocCommentGlyphTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        // Matches the first line of any recognised doc-comment style:
        //   C# / F# / VB     ///  (triple slash)
        //   C++ line          ///  or  //!
        //   C++ block         /**  or  /*!  (block opener — the entire /** … */ block
        //                     is tagged as a single glyph unit in the tagger)
        private static readonly System.Text.RegularExpressions.Regex DocLineRegex =
            new System.Text.RegularExpressions.Regex(
                @"^\s*(?:///|//!|/\*[*!])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public DocCommentGlyphTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += OnBufferChanged;
            SettingsChangedBroadcast.SettingsChanged += OnSettingsChanged;
        }

        // Recognise C++ block comment openers: /** or /*!
        private static readonly System.Text.RegularExpressions.Regex CppBlockOpenRegex =
            new System.Text.RegularExpressions.Regex(
                @"^\s*/\*[*!]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Recognise C++ or C# line-doc comments: /// or //!
        private static readonly System.Text.RegularExpressions.Regex LineDocRegex =
            new System.Text.RegularExpressions.Regex(
                @"^\s*(?:///|//!)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        public IEnumerable<ITagSpan<DocCommentGlyphTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (!RenderDocOptions.Instance.EffectiveGlyphToggle) yield break;

            var snapshot = spans[0].Snapshot;
            int lineCount = snapshot.LineCount;
            int i = 0;

            while (i < lineCount)
            {
                var line = snapshot.GetLineFromLineNumber(i);
                var lineText = line.GetText();

                ITextSnapshotLine firstLine;
                ITextSnapshotLine lastLine;

                if (CppBlockOpenRegex.IsMatch(lineText))
                {
                    // ── C++ block comment  /** … */  or  /*! … */ ─────────────────
                    firstLine = line;
                    bool closed = lineText.Contains("*/");
                    i++;
                    while (!closed && i < lineCount)
                    {
                        var bodyLine = snapshot.GetLineFromLineNumber(i);
                        if (bodyLine.GetText().Contains("*/"))
                            closed = true;
                        i++;
                    }
                    lastLine = snapshot.GetLineFromLineNumber(i - 1);
                }
                else if (LineDocRegex.IsMatch(lineText))
                {
                    // ── Line-doc comment  ///  or  //! ───────────────────────────
                    firstLine = line;
                    int blockStart = i;
                    while (i < lineCount &&
                           LineDocRegex.IsMatch(snapshot.GetLineFromLineNumber(i).GetText()))
                        i++;
                    lastLine = snapshot.GetLineFromLineNumber(i - 1);
                }
                else
                {
                    i++;
                    continue;
                }

                var blockSpan = new SnapshotSpan(snapshot,
                    Span.FromBounds(firstLine.Start, lastLine.End));

                var tagSpan = new SnapshotSpan(snapshot, firstLine.Start, firstLine.Length);
                if (spans.IntersectsWith(new NormalizedSnapshotSpanCollection(tagSpan)))
                    yield return new TagSpan<DocCommentGlyphTag>(tagSpan,
                        new DocCommentGlyphTag(blockSpan));
            }
        }

        private void OnBufferChanged(object sender, TextContentChangedEventArgs e)
            => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(e.After, 0, e.After.Length)));

        private void OnSettingsChanged(object sender, EventArgs e)
            => TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(
                new SnapshotSpan(_buffer.CurrentSnapshot, 0, _buffer.CurrentSnapshot.Length)));

        public void Dispose()
        {
            _buffer.Changed -= OnBufferChanged;
            SettingsChangedBroadcast.SettingsChanged -= OnSettingsChanged;
        }
    }

    // ── Tagger provider ───────────────────────────────────────────────────────────

    [Export(typeof(ITaggerProvider))]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("FSharp")]
    [ContentType("C/C++")]
    [TagType(typeof(DocCommentGlyphTag))]
    internal sealed class DocCommentGlyphTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            return buffer.Properties.GetOrCreateSingletonProperty(
                typeof(DocCommentGlyphTagger),
                () => new DocCommentGlyphTagger(buffer)) as ITagger<T>;
        }
    }

    // ── Glyph factory ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Produces the WPF toggle button shown in the left margin for each doc-comment block.
    /// Clicking it calls <see cref="DocCommentToggleState.Toggle"/> for the block span,
    /// then broadcasts a settings-changed event so the adornment tagger invalidates.
    /// </summary>
    internal sealed class DocCommentGlyphFactory : IGlyphFactory
    {
        private readonly IWpfTextView _view;

        public DocCommentGlyphFactory(IWpfTextView view)
        {
            _view = view;
        }

        private static readonly string IconBase64 = "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";

        private static System.Windows.Media.Imaging.BitmapImage _iconImage;

        private static System.Windows.Media.Imaging.BitmapImage GetIcon()
        {
            if (_iconImage != null) return _iconImage;
            try
            {
                var bytes = Convert.FromBase64String(IconBase64);
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using (var ms = new System.IO.MemoryStream(bytes))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                _iconImage = bmp;
            }
            catch { }
            return _iconImage;
        }

        public UIElement GenerateGlyph(IWpfTextViewLine line, IGlyphTag tag)
        {
            if (!(tag is DocCommentGlyphTag glyphTag)) return null;
            if (!RenderDocOptions.Instance.EffectiveGlyphToggle) return null;

            bool isRendered = !DocCommentToggleState.IsHidden(glyphTag.BlockSpan);

            var icon = GetIcon();
            UIElement child;

            if (icon != null)
            {
                child = new System.Windows.Controls.Image
                {
                    Source = icon,
                    Width = 14,
                    Height = 14,
                    Opacity = isRendered ? 1.0 : 0.35,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Stretch = System.Windows.Media.Stretch.Uniform
                };
            }
            else
            {
                child = new TextBlock
                {
                    Text = isRendered ? "●" : "○",
                    FontSize = 9,
                    Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var btn = new Border
            {
                Width = 16,
                Height = 16,
                CornerRadius = new CornerRadius(3),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = isRendered ? "Hide rendered comment (click to edit XML)"
                                          : "Show rendered comment",
                Child = child
            };

            btn.MouseLeftButtonUp += (s, e) =>
            {
                DocCommentToggleState.Toggle(glyphTag.BlockSpan);
                SettingsChangedBroadcast.RaiseSettingsChanged();
                e.Handled = true;
            };

            return btn;
        }
    }

    // ── Glyph factory provider ────────────────────────────────────────────────────

    [Export(typeof(IGlyphFactoryProvider))]
    [Name("DocCommentGlyphFactory")]
    [ContentType("CSharp")]
    [ContentType("Basic")]
    [ContentType("FSharp")]
    [ContentType("C/C++")]
    [TagType(typeof(DocCommentGlyphTag))]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [Order(Before = "VsTextMarker")]
    internal sealed class DocCommentGlyphFactoryProvider : IGlyphFactoryProvider
    {
        public IGlyphFactory GetGlyphFactory(IWpfTextView view, IWpfTextViewMargin margin)
            => new DocCommentGlyphFactory(view);
    }

    // ── Per-block toggle state ────────────────────────────────────────────────────
    /// <summary>
    /// Tracks which doc-comment blocks have been manually hidden via the glyph button.
    /// Key: the text of the first line of the block (stable enough for a session).
    /// </summary>
    public static class DocCommentToggleState
    {
        private static readonly HashSet<int> _hiddenStarts = new HashSet<int>();

        public static bool IsHidden(SnapshotSpan blockSpan)
            => _hiddenStarts.Contains(blockSpan.Start.Position);

        public static void Toggle(SnapshotSpan blockSpan)
        {
            int pos = blockSpan.Start.Position;
            if (_hiddenStarts.Contains(pos)) _hiddenStarts.Remove(pos);
            else _hiddenStarts.Add(pos);
        }

        public static void Clear() => _hiddenStarts.Clear();
    }
}