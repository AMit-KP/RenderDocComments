/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    DocCommentAdornmentTag.cs
 *  Purpose: Creates the intra-text adornment tag that wraps the DocCommentControl
 *           (the primary WPF rendering control) and injects theme-aware colors,
 *           font settings, and brush configurations.
 *
 *  Architecture Role:
 *    Sits between the tagger (which detects doc comments) and the control (which
 *    renders them). The tagger instantiates this tag for each documentation
 *    comment span; the tag's constructor extracts theme colors from the VS
 *    IEditorFormatMap, builds all required brushes, and passes them to
 *    DocCommentControl.
 *
 *  Key Classes:
 *    DocCommentAdornmentTag  — Inherits from IntraTextAdornmentTag; creates and
 *                              configures a DocCommentControl instance.
 *
 *  Dependencies:
 *    • DocCommentControl.cs       — Creates the control instance.
 *    • DocCommentParser.cs        — Consumes ParsedDocComment data model.
 *    • RenderDocOptions.cs        — Reads EffectiveFontFamily, gradient stops,
 *      and color settings (premium-aware via LicenseManager.PremiumUnlocked).
 *    • LicenseManager.cs          — Checks PremiumUnlocked for summary color.
 *    • Microsoft.VisualStudio.Text.Classification (IEditorFormatMap,
 *      EditorFormatDefinition).
 *
 *  When to Edit:
 *    • Adding a new brush/color that DocCommentControl needs (e.g., a new
 *      section type).
 *    • Changing how theme colors are extracted from VS format map.
 *    • Modifying font size/family resolution logic.
 *    • Switching the control type from DocCommentControl to a different WPF
 *      element.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System.Windows;
using System.Windows.Media;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// Represents an intra-text adornment tag that renders documentation comments as visual controls.<br/>
    /// This tag inherits from <see cref="IntraTextAdornmentTag"/> and is responsible for creating<br/>
    /// and configuring the <see cref="DocCommentControl"/> that displays formatted documentation.
    /// </summary>
    /// <remarks>
    /// The adornment tag is created by the <see cref="DocCommentAdornmentTagger"/> for each<br/>
    /// documentation comment span detected in the source code. It encapsulates the logic<br/>
    /// for theme color extraction, font configuration, and brush creation.
    /// </remarks>
    internal sealed class DocCommentAdornmentTag : IntraTextAdornmentTag
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="DocCommentAdornmentTag"/> class.<br/>
        /// Creates an adornment tag with a configured <see cref="DocCommentControl"/> that<br/>
        /// will render the parsed documentation comment in the editor viewport.
        /// </summary>
        /// <param name="doc">
        /// The parsed documentation comment containing XML structure and content<br/>
        /// extracted from the source code by <see cref="DocCommentParser"/>.
        /// </param>
        /// <param name="view">
        /// The WPF text view that hosts this adornment, used for retrieving<br/>
        /// theme colors, font settings, and viewport dimensions.
        /// </param>
        /// <param name="indentWidth">
        /// The horizontal indentation width in pixels, used to position the<br/>
        /// rendered documentation control at the correct horizontal offset.
        /// </param>
        public DocCommentAdornmentTag(ParsedDocComment doc, IWpfTextView view, double indentWidth)
            : base(CreateControl(doc, view, indentWidth), null, PositionAffinity.Predecessor)
        {
        }

        /// <summary>
        /// Creates and configures a <see cref="DocCommentControl"/> instance for rendering<br/>
        /// the documentation comment with proper theme colors, fonts, and styling.
        /// </summary>
        /// <param name="doc">
        /// The parsed documentation comment containing XML structure and content.
        /// </param>
        /// <param name="view">
        /// The WPF text view used to extract theme colors and font properties<br/>
        /// from the Visual Studio editor's format map.
        /// </param>
        /// <param name="indentWidth">
        /// The indentation width in pixels for horizontal positioning.
        /// </param>
        /// <returns>
        /// A configured <see cref="DocCommentControl"/> ready to render the documentation<br/>
        /// with theme-aware colors, custom fonts, and gradient accent bars.
        /// </returns>
        /// <remarks>
        /// This method performs the following configuration steps:<br/>
        /// 1. Extracts theme foreground and background colors from the editor's <see cref="IEditorFormatMap"/>.<br/>
        /// 2. Retrieves the default font family from <see cref="RenderDocOptions"/> and font size from the view.<br/>
        /// 3. Creates specialized brushes for summary text, links, code blocks, parameters, and sections.<br/>
        /// 4. Constructs a gradient brush for accent bars using configured gradient stops.<br/>
        /// 5. Instantiates the <see cref="DocCommentControl"/> with all visual parameters.<br/>
        /// <br/>
        /// Theme color extraction gracefully falls back to default dark theme colors<br/>
        /// (foreground: RGB 212,212,212; background: RGB 30,30,30) if the format map is unavailable.<br/>
        /// Premium license status affects whether summary text uses custom or theme colors.
        /// </remarks>
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