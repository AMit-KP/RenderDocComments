using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Windows;
using System.Windows.Media;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentTag : IntraTextAdornmentTag
    {
        public DocCommentAdornmentTag(ParsedDocComment doc, IWpfTextView view, double indentWidth)
            : base(CreateControl(doc, view, indentWidth), null, PositionAffinity.Predecessor)
        {
        }

        private static UIElement CreateControl(ParsedDocComment doc, IWpfTextView view, double indentWidth)
        {
            // Defaults — VS dark theme colours as fallback
            Brush fg = new SolidColorBrush(Color.FromRgb(212, 212, 212));
            Brush bg = new SolidColorBrush(Color.FromRgb(30, 30, 30));
            Brush link = new SolidColorBrush(Color.FromRgb(86, 156, 214));

            try
            {
                var formatMap = view.Properties
                    .GetProperty<IEditorFormatMap>(typeof(IEditorFormatMap));

                if (formatMap != null)
                {
                    // Background — read from the view/editor background, not Plain Text
                    var bgProps = formatMap.GetProperties("TextView Background");
                    if (bgProps != null && bgProps.Contains(EditorFormatDefinition.BackgroundBrushId))
                        bg = (Brush)bgProps[EditorFormatDefinition.BackgroundBrushId];

                    // Foreground — read from Plain Text classification
                    var fgProps = formatMap.GetProperties("Plain Text");
                    if (fgProps != null && fgProps.Contains(EditorFormatDefinition.ForegroundBrushId))
                        fg = (Brush)fgProps[EditorFormatDefinition.ForegroundBrushId];
                }
            }
            catch { }

            // Font — use editor's font size but Segoe UI for readability (#1)
            var font = view.FormattedLineSource?.DefaultTextProperties;
            var ff = new FontFamily("Segoe UI");
            double fs = (font != null && font.FontRenderingEmSize > 0)
                ? font.FontRenderingEmSize
                : 13.0;

            return new DocCommentControl(doc, fg, bg, link, ff, fs, view.ViewportWidth, indentWidth);
        }
    }
}