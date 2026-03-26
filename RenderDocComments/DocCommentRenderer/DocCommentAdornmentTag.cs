using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

namespace RenderDocComments.DocCommentRenderer
{
    internal sealed class DocCommentAdornmentTag : IntraTextAdornmentTag
    {
        public DocCommentAdornmentTag(ParsedDocComment doc, IWpfTextView view, double indentWidth)
            : base(CreateControl(doc, view, indentWidth), null, PositionAffinity.Predecessor)
        {
        }

        private static UIElement CreateControl(
            ParsedDocComment doc, IWpfTextView view, double indentWidth)
        {
            var opts = RenderDocOptions.Instance;

            // ── Theme colours from VS format map ─────────────────────────────────
            Brush themeFg = new SolidColorBrush(Color.FromRgb(212, 212, 212));
            Brush themeBg = new SolidColorBrush(Color.FromRgb(30, 30, 30));

            try
            {
                var formatMap = view.Properties
                    .GetProperty<IEditorFormatMap>(typeof(IEditorFormatMap));
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
            var ff = new FontFamily(opts.EffectiveFontFamily);

            var fontProps = view.FormattedLineSource?.DefaultTextProperties;
            double fs = (fontProps != null && fontProps.FontRenderingEmSize > 0)
                ? fontProps.FontRenderingEmSize
                : 13.0;

            // ── Colour brushes ───────────────────────────────────────────────────
            Brush summaryBrush = LicenseManager.PremiumUnlocked
                ? new SolidColorBrush(opts.EffectiveColorSummaryFg)
                : themeFg;

            Brush linkBrush = new SolidColorBrush(opts.EffectiveColorLink);

            Brush codeFgBrush = new SolidColorBrush(opts.EffectiveColorCodeFg);

            Brush paramBrush = new SolidColorBrush(opts.EffectiveColorParamName);

            Brush sectionBrush = new SolidColorBrush(opts.EffectiveColorSectionLabel);

            // Gradient bar brush
            Brush gradBrush = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(opts.EffectiveGradientStop0, 0.0),
                    new GradientStop(opts.EffectiveGradientStop1, 0.4),
                    new GradientStop(opts.EffectiveGradientStop2, 1.0),
                },
                new Point(0, 0), new Point(1, 0));

            return new DocCommentControl(
                doc,
                themeFg,
                themeBg,
                summaryBrush,
                linkBrush,
                codeFgBrush,
                paramBrush,
                sectionBrush,
                gradBrush,
                ff, fs,
                view.ViewportWidth,
                indentWidth);
        }
    }
}