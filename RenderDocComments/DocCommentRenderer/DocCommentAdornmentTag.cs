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
            // Premium: use whatever font the user chose.  Free: always Segoe UI.
            var ff = new FontFamily(opts.EffectiveFontFamily);

            var fontProps = view.FormattedLineSource?.DefaultTextProperties;
            double fs = (fontProps != null && fontProps.FontRenderingEmSize > 0)
                ? fontProps.FontRenderingEmSize
                : 13.0;

            // ── Colour brushes ───────────────────────────────────────────────────
            // For every colour: Premium uses the user-chosen value, free uses the
            // original hardcoded default (exactly as the extension shipped).

            // Summary / plain text — free: use theme foreground (same as before)
            Brush summaryBrush = LicenseManager.PremiumUnlocked
                ? new SolidColorBrush(opts.EffectiveColorSummaryFg)
                : themeFg;

            // Link — free default: #569CD6 (VS blue)
            Brush linkBrush = new SolidColorBrush(opts.EffectiveColorLink);

            // Inline code fg — FREE DEFAULT: #CE9178 (the original orange-tan, NOT purple)
            // This is restored to the original value; it only changes if the user
            // explicitly picks a different colour in the Premium options.
            Brush codeFgBrush = new SolidColorBrush(opts.EffectiveColorCodeFg);

            // Param names — free default: #9CDCFE (light blue)
            Brush paramBrush = new SolidColorBrush(opts.EffectiveColorParamName);

            // Section labels — free default: #969696 (dim grey)
            Brush sectionBrush = new SolidColorBrush(opts.EffectiveColorSectionLabel);

            // Gradient bar brush
            // Free default: the original purple gradient exactly as before.
            // Premium: uses user-chosen stop colours.
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