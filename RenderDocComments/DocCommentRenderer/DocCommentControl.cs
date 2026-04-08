using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// WPF user control that renders parsed documentation comments as a formatted visual block.<br/>
    /// Inherits from <see cref="StackPanel"/> to vertically stack documentation sections.
    /// </summary>
    /// <remarks>
    /// <para>This control is the primary visual representation of documentation comments in the<br/>
    /// editor. It is created by <see cref="DocCommentAdornmentTag.CreateControl"/> and positioned<br/>
    /// as an intra-text adornment via the tagger infrastructure.</para>
    /// <para>The control renders the following documentation sections when present:</para>
    /// <list type="bullet">
    /// <item><description><b>InheritDoc / Include fallback:</b> Shown when resolution fails.</description></item>
    /// <item><description><b>Deprecated banner:</b> Red-toned admonition for deprecated members.</description></item>
    /// <item><description><b>Summary / Brief:</b> Primary documentation text.</description></item>
    /// <item><description><b>Remarks / Details:</b> Extended documentation.</description></item>
    /// <item><description><b>Admonitions:</b> Note, Warning, Attention, Bug, Todo — each with distinct accent colors.</description></item>
    /// <item><description><b>Example:</b> Usage examples with mixed prose and code blocks.</description></item>
    /// <item><description><b>Parameters:</b> Type parameters (<c>&lt;typeparam&gt;</c>) and value parameters (<c>&lt;param&gt;</c>) with direction badges.</description></item>
    /// <item><description><b>Returns:</b> Return value documentation.</description></item>
    /// <item><description><b>Return values:</b> C++ <c>\retval</c> documentation.</description></item>
    /// <item><description><b>Exceptions:</b> Exception types with clickable navigation links.</description></item>
    /// <item><description><b>Pre/Post conditions:</b> Contract documentation.</description></item>
    /// <item><description><b>Permission:</b> Access permission documentation.</description></item>
    /// <item><description><b>Completion List:</b> IntelliSense completion entries.</description></item>
    /// <item><description><b>See Also:</b> Cross-reference links.</description></item>
    /// <item><description><b>Meta-info:</b> Since, Version, Author, Date, Copyright.</description></item>
    /// </list>
    /// <para>All colors are theme-aware and adapt to dark/light Visual Studio themes.<br/>
    /// The control supports configurable gradient accent bars on any combination of sides.</para>
    /// </remarks>
    public class DocCommentControl : StackPanel
    {
        // ── All brushes injected — NO static fields ───────────────────────────────
        
        /// <summary>Primary foreground brush for standard documentation text.</summary>
        private readonly Brush _fg;
        /// <summary>Dimmed foreground brush for secondary or less prominent text.</summary>
        private readonly Brush _fgDim;
        /// <summary>Background brush matching the editor's theme background.</summary>
        private readonly Brush _bg;
        /// <summary>Foreground brush for summary text (custom color for premium users).</summary>
        private readonly Brush _summaryFg;
        /// <summary>Foreground brush for hyperlinks and cref navigation targets.</summary>
        private readonly Brush _linkBrush;
        /// <summary>Background brush for inline and block code elements.</summary>
        private readonly Brush _codeBg;
        /// <summary>Foreground brush for code text (monospaced fonts).</summary>
        private readonly Brush _codeFg;
        /// <summary>Foreground brush for parameter names in the parameter grid.</summary>
        private readonly Brush _paramNameBrush;
        /// <summary>Foreground brush for section labels (e.g., "Parameters:", "Returns:").</summary>
        private readonly Brush _sectionLabelBrush;
        /// <summary>Gradient brush for accent bars surrounding the documentation block.</summary>
        private readonly Brush _gradientBarBrush;

        // ── Admonition brushes derived from theme ─────────────────────────────────
        
        /// <summary>Accent brush for Note admonitions (blue tones).</summary>
        private readonly Brush _noteBrush;
        /// <summary>Accent brush for Warning admonitions (amber tones).</summary>
        private readonly Brush _warningBrush;
        /// <summary>Accent brush for Deprecated banners (red tones).</summary>
        private readonly Brush _deprecatedBrush;
        /// <summary>Accent brush for Bug admonitions (orange-red tones).</summary>
        private readonly Brush _bugBrush;
        /// <summary>Accent brush for Todo admonitions (green tones).</summary>
        private readonly Brush _todoBrush;
        /// <summary>Brush for meta-information fields (author, date, version, etc.).</summary>
        private readonly Brush _metaBrush;

        /// <summary>The editor's default font family, used for prose text.</summary>
        private readonly FontFamily _editorFont;
        /// <summary>Monospaced font family for code elements (Cascadia Mono, Consolas, Courier New).</summary>
        private readonly FontFamily _monoFont;
        /// <summary>Font size in device-independent units, derived from the editor's font settings.</summary>
        private readonly double _fontSize;
        /// <summary>Maximum content width calculated as 60% of available viewport width minus indentation.</summary>
        private readonly double _contentMaxWidth;

        /// <summary>Constant indentation width in pixels for section content bodies.</summary>
        private const double SectionContentIndent = 12.0;

        // ── Constructor ───────────────────────────────────────────────────────────

        /// <summary>
        /// Initializes a new instance of the <see cref="DocCommentControl"/> class with<br/>
        /// all visual properties and builds the documentation layout tree.
        /// </summary>
        /// <param name="doc">
        /// The parsed documentation comment containing structured XML documentation content.
        /// </param>
        /// <param name="foreground">
        /// The primary foreground brush for standard text, derived from the editor's theme.
        /// </param>
        /// <param name="background">
        /// The background brush matching the editor's theme, used for the control's background<br/>
        /// and to derive theme-adaptive accent colors for admonitions and code blocks.
        /// </param>
        /// <param name="summaryFg">
        /// The foreground brush specifically for summary text. May differ from the primary<br/>
        /// foreground color for premium users with custom color profiles.
        /// </param>
        /// <param name="linkColor">
        /// The foreground brush for hyperlinks and cref navigation targets.
        /// </param>
        /// <param name="codeFg">
        /// The foreground brush for code text in inline and block code elements.
        /// </param>
        /// <param name="paramNameBrush">
        /// The foreground brush for parameter names in the parameter grid layout.
        /// </param>
        /// <param name="sectionLabelBrush">
        /// The foreground brush for section labels (e.g., "Parameters:", "Returns:", "Exceptions:").
        /// </param>
        /// <param name="gradientBarBrush">
        /// The gradient brush used to render accent bars on the sides, top, or bottom of the control.
        /// </param>
        /// <param name="editorFont">
        /// The font family used by the editor for prose text, ensuring visual consistency.
        /// </param>
        /// <param name="fontSize">
        /// The font size in device-independent units (1/96 inch), matching the editor's font settings.
        /// </param>
        /// <param name="viewportWidth">
        /// The current width of the editor viewport in device-independent pixels, used to calculate<br/>
        /// the maximum content width for optimal line length.
        /// </param>
        /// <param name="indentWidth">
        /// The horizontal indentation width in pixels, used to position the control at the correct<br/>
        /// offset matching the source code indentation.
        /// </param>
        /// <remarks>
        /// <para>The constructor performs the following initialization steps:</para>
        /// <list type="number">
        /// <item><description>Stores all injected brushes and font properties for later use.</description></item>
        /// <item><description>Creates a monospaced font family with fallback chain: Cascadia Mono → Consolas → Courier New.</description></item>
        /// <item><description>Calculates <see cref="_contentMaxWidth"/> as 60% of available viewport width to prevent overly long lines.</description></item>
        /// <item><description>Detects theme darkness via <see cref="GetLuminance"/> to adapt color palettes.</description></item>
        /// <item><description>Creates theme-adaptive brushes:
        ///   <list type="bullet">
        ///   <item><description><b>Dim foreground:</b> Semi-transparent gray for secondary text.</description></item>
        ///   <item><description><b>Code background:</b> Semi-transparent white (dark theme) or black (light theme).</description></item>
        ///   <item><description><b>Admonition colors:</b> Note (blue), Warning (amber), Deprecated (red), Bug (orange-red), Todo (green).</description></item>
        ///   <item><description><b>Meta brush:</b> Reuses the dim foreground for meta-information fields.</description></item>
        ///   </list>
        /// </description></item>
        /// <item><description>Configures the StackPanel with vertical orientation and appropriate margins.</description></item>
        /// <item><description>Invokes <see cref="Build"/> to construct the complete visual layout tree.</description></item>
        /// </list>
        /// <para>Theme detection logic:</para>
        /// <list type="bullet">
        /// <item><description><b>Dark theme:</b> Luminance &lt; 0.4 — uses lighter, more saturated accent colors.</description></item>
        /// <item><description><b>Light theme:</b> Luminance ≥ 0.4 — uses darker, more muted accent colors for contrast.</description></item>
        /// </list>
        /// </remarks>
        public DocCommentControl(
            ParsedDocComment doc,
            Brush foreground,
            Brush background,
            Brush summaryFg,
            Brush linkColor,
            Brush codeFg,
            Brush paramNameBrush,
            Brush sectionLabelBrush,
            Brush gradientBarBrush,
            FontFamily editorFont,
            double fontSize,
            double viewportWidth,
            double indentWidth)
        {
            _fg = foreground;
            _bg = background;
            _summaryFg = summaryFg;
            _linkBrush = linkColor;
            _codeFg = codeFg;
            _paramNameBrush = paramNameBrush;
            _sectionLabelBrush = sectionLabelBrush;
            _gradientBarBrush = gradientBarBrush;
            _editorFont = editorFont;
            _monoFont = new FontFamily("Cascadia Mono, Consolas, Courier New");
            _fontSize = fontSize;
            _contentMaxWidth = (viewportWidth - indentWidth - 32) * 0.60;

            bool isDark = GetLuminance(background) < 0.4;

            _fgDim = new SolidColorBrush(isDark
                ? Color.FromArgb(180, 180, 180, 180)
                : Color.FromArgb(220, 80, 80, 80));

            _codeBg = new SolidColorBrush(isDark
                ? Color.FromArgb(50, 255, 255, 255)
                : Color.FromArgb(30, 0, 0, 0));

            // Admonition palette — adapt for dark / light themes.
            _noteBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(86, 156, 214)    // VS blue
                : Color.FromRgb(0, 80, 160));

            _warningBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(220, 170, 50)    // amber
                : Color.FromRgb(160, 100, 0));

            _deprecatedBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(210, 100, 100)   // soft red
                : Color.FromRgb(160, 30, 30));

            _bugBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(240, 100, 80)    // orange-red
                : Color.FromRgb(180, 50, 20));

            _todoBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(180, 215, 130)   // green
                : Color.FromRgb(60, 130, 60));

            _metaBrush = _fgDim;  // author, date, version etc.

            Background = background;
            Orientation = Orientation.Vertical;
            Margin = new Thickness(indentWidth, 2, 0, 6);

            Build(doc);
        }

        // ── Build ─────────────────────────────────────────────────────────────────

        /// <summary>
        /// Constructs the complete visual layout tree for the documentation comment.<br/>
        /// This method creates all WPF elements (grids, panels, text blocks, borders) and<br/>
        /// adds them as children of the StackPanel in the correct rendering order.
        /// </summary>
        /// <param name="doc">
        /// The <see cref="ParsedDocComment"/> containing structured documentation content<br/>
        /// to be rendered. Each non-empty field is rendered as a distinct section.
        /// </param>
        /// <remarks>
        /// <para>The method builds a hierarchical layout using the following structure:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>Outer Grid:</b> A 5-column, 5-row grid that manages gradient accent bars<br/>
        /// and contains the central content panel. Column layout:
        ///   <list type="bullet">
        ///   <item><description>Column 0: Left gradient bar (conditional, 4px if enabled).</description></item>
        ///   <item><description>Column 1: Left gap (8px if left bar present).</description></item>
        ///   <item><description>Column 2: Content area (star-sized, takes remaining space).</description></item>
        ///   <item><description>Column 3: Right gap (8px if right bar present).</description></item>
        ///   <item><description>Column 4: Right gradient bar (conditional, 4px if enabled).</description></item>
        ///   </list>
        /// Row layout follows the same pattern for top/bottom bars.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Gradient Bars:</b> Created by the local <c>MakeBar</c> function, each bar is a<br/>
        /// <see cref="System.Windows.Shapes.Rectangle"/> filled with a 3-stop gradient and a<br/>
        /// <see cref="DropShadowEffect"/> for a glowing purple effect. Bars can be vertical or horizontal.
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Content StackPanel:</b> Placed in the center cell (column 2, row 2), this panel<br/>
        /// vertically stacks all documentation sections in the following order:
        ///   <list type="bullet">
        ///   <item><description>InheritDoc / Include fallback messages (when resolution fails).</description></item>
        ///   <item><description>Deprecated banner (always first if present, red accent).</description></item>
        ///   <item><description>Summary / Brief (primary documentation text).</description></item>
        ///   <item><description>Remarks / Details (extended documentation).</description></item>
        ///   <item><description>Note admonition (blue accent, labeled "ℹ Note").</description></item>
        ///   <item><description>Warning admonition (amber accent, labeled "⚠ Warning").</description></item>
        ///   <item><description>Attention admonition (amber accent, labeled "❗ Attention").</description></item>
        ///   <item><description>Example section (mixed prose + code blocks).</description></item>
        ///   <item><description>Parameters grid (type params first, then value params with direction badges).</description></item>
        ///   <item><description>Returns section.</description></item>
        ///   <item><description>Return values grid (C++ <c>\retval</c>).</description></item>
        ///   <item><description>Exceptions grid (with clickable cref links).</description></item>
        ///   <item><description>Precondition / Postcondition / Invariant sections.</description></item>
        ///   <item><description>Permission section (with cref label if present).</description></item>
        ///   <item><description>Completion List entries.</description></item>
        ///   <item><description>Bug / Todo admonitions (orange-red / green accents).</description></item>
        ///   <item><description>See Also wrap panel (horizontal link list).</description></item>
        ///   <item><description>Meta-info strip (Since, Version, Author, Date, Copyright in horizontal wrap panel).</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>Each section is only rendered if its corresponding field in <paramref name="doc"/> is non-empty.</para>
        /// </remarks>
        private void Build(ParsedDocComment doc)
        {
            var opts = RenderDocOptions.Instance;

            double left = opts.EffectiveBorderLeft ? 4 : 0;
            double top = opts.EffectiveBorderTop ? 4 : 0;
            double right = opts.EffectiveBorderRight ? 4 : 0;
            double bottom = opts.EffectiveBorderBottom ? 4 : 0;

            var outerGrid = new Grid
            {
                MaxWidth = _contentMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Left
            };

            // Column layout: left-bar | gap | content | gap | right-bar
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left > 0 ? left : 0) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(left > 0 ? 8 : 0) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right > 0 ? 8 : 0) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(right > 0 ? right : 0) });

            // Row layout: top-bar | gap | content | gap | bottom-bar
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(top > 0 ? top : 0) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(top > 0 ? 4 : 0) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Auto) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bottom > 0 ? 4 : 0) });
            outerGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(bottom > 0 ? bottom : 0) });

            // Helper: create a glowing gradient bar rectangle.
            System.Windows.Shapes.Rectangle MakeBar(bool vertical)
            {
                var stops = new GradientStopCollection
                {
                    new GradientStop(opts.EffectiveGradientStop0, 0.0),
                    new GradientStop(opts.EffectiveGradientStop1, 0.4),
                    new GradientStop(opts.EffectiveGradientStop2, 1.0),
                };
                var brush = vertical
                    ? new LinearGradientBrush(stops, new Point(0, 0), new Point(0, 1))
                    : new LinearGradientBrush(stops, new Point(0, 0), new Point(1, 0));

                return new System.Windows.Shapes.Rectangle
                {
                    Fill = brush,
                    RadiusX = 2,
                    RadiusY = 2,
                    VerticalAlignment = VerticalAlignment.Stretch,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    Effect = new DropShadowEffect
                    {
                        Color = Color.FromArgb(140, 180, 90, 240),
                        BlurRadius = 8,
                        ShadowDepth = 0,
                        Direction = 0
                    }
                };
            }

            if (left > 0)
            {
                var bar = MakeBar(true);
                Grid.SetColumn(bar, 0); Grid.SetRow(bar, 0);
                Grid.SetRowSpan(bar, 5);
                outerGrid.Children.Add(bar);
            }
            if (right > 0)
            {
                var bar = MakeBar(true);
                Grid.SetColumn(bar, 4); Grid.SetRow(bar, 0);
                Grid.SetRowSpan(bar, 5);
                outerGrid.Children.Add(bar);
            }
            if (top > 0)
            {
                var bar = MakeBar(false);
                Grid.SetColumn(bar, 0); Grid.SetRow(bar, 0);
                Grid.SetColumnSpan(bar, 5);
                outerGrid.Children.Add(bar);
            }
            if (bottom > 0)
            {
                var bar = MakeBar(false);
                Grid.SetColumn(bar, 0); Grid.SetRow(bar, 4);
                Grid.SetColumnSpan(bar, 5);
                outerGrid.Children.Add(bar);
            }

            // Content panel
            var content = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 2, 0, 2)
            };
            Grid.SetColumn(content, 2);
            Grid.SetRow(content, 2);
            outerGrid.Children.Add(content);

            // ── Sections ──────────────────────────────────────────────────────────

            // InheritDoc / Include are only shown when the tagger could not resolve them.
            if (doc.InheritDoc != null)
            {
                var idText = string.IsNullOrEmpty(doc.InheritDoc.Cref)
                    ? "(inherits documentation — source not found)"
                    : $"(inherits documentation from {DocCommentParser.SimplifyCref(doc.InheritDoc.Cref)} — source not found)";
                content.Children.Add(BuildRichBlock(idText, _fgDim, marginBottom: 4));
            }

            if (doc.Include != null)
                content.Children.Add(BuildRichBlock(
                    $"(documentation included from {doc.Include.File} — file not found or path unmatched)",
                    _fgDim, marginBottom: 4));

            // ── Deprecated banner (always shown first if present) ──────────────────
            if (!string.IsNullOrWhiteSpace(doc.Deprecated))
                content.Children.Add(
                    BuildAdmonitionBanner("⚠ Deprecated", doc.Deprecated, _deprecatedBrush));

            // ── Summary / Brief ───────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Summary))
                content.Children.Add(BuildRichBlock(doc.Summary, _summaryFg, marginBottom: 4));

            // ── Remarks / Details ─────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Remarks))
                content.Children.Add(BuildRichBlock(doc.Remarks, _fgDim, marginBottom: 4));

            // ── Note ──────────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Note))
                content.Children.Add(
                    BuildAdmonitionBanner("ℹ Note", doc.Note, _noteBrush));

            // ── Warning ───────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Warning))
                content.Children.Add(
                    BuildAdmonitionBanner("⚠ Warning", doc.Warning, _warningBrush));

            // ── Attention ─────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Attention))
                content.Children.Add(
                    BuildAdmonitionBanner("❗ Attention", doc.Attention, _warningBrush));

            // ── Example ───────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Example))
            {
                content.Children.Add(BuildSectionLabel("Example:"));
                content.Children.Add(BuildMixedPanel(doc.Example, _fg, SectionContentIndent));
            }

            // ── Parameters (type params first, then value params) ─────────────────
            if (doc.Params.Count > 0 || doc.TypeParams.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Parameters:"));
                var grid = BuildParamGrid(SectionContentIndent);
                int row = 0;
                foreach (var tp in doc.TypeParams)
                    AddParamRow(grid, tp.Name, tp.Description, ref row,
                        direction: string.Empty, isTypeParam: true);
                foreach (var p in doc.Params)
                    AddParamRow(grid, p.Name, p.Description, ref row,
                        direction: p.Direction, isTypeParam: false);
                content.Children.Add(grid);
            }

            // ── Returns ───────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Returns))
            {
                content.Children.Add(BuildSectionLabel("Returns:"));
                content.Children.Add(BuildRichBlock(
                    doc.Returns, _fg, marginLeft: SectionContentIndent));
            }

            // ── Return values (\retval) ────────────────────────────────────────────
            if (doc.RetVals.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Return values:"));
                var grid = BuildParamGrid(SectionContentIndent);
                int row = 0;
                foreach (var rv in doc.RetVals)
                    AddParamRow(grid, rv.Name, rv.Description, ref row);
                content.Children.Add(grid);
            }

            // ── Exceptions / Throws ───────────────────────────────────────────────
            if (doc.Exceptions.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Exceptions:"));
                var grid = BuildParamGrid(SectionContentIndent);
                int row = 0;
                foreach (var ex in doc.Exceptions)
                    AddParamRow(grid, ex.Type, ex.Description, ref row, navCref: ex.FullCref);
                content.Children.Add(grid);
            }

            // ── Pre-condition ─────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Pre))
            {
                content.Children.Add(BuildSectionLabel("Precondition:"));
                content.Children.Add(BuildRichBlock(doc.Pre, _fg, marginLeft: SectionContentIndent));
            }

            // ── Post-condition ────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Post))
            {
                content.Children.Add(BuildSectionLabel("Postcondition:"));
                content.Children.Add(BuildRichBlock(doc.Post, _fg, marginLeft: SectionContentIndent));
            }

            // ── Invariant ─────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Invariant))
            {
                content.Children.Add(BuildSectionLabel("Invariant:"));
                content.Children.Add(BuildRichBlock(doc.Invariant, _fg, marginLeft: SectionContentIndent));
            }

            // ── Permission ────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Permission))
            {
                var permLabel = string.IsNullOrEmpty(doc.PermissionCref)
                    ? "Permission:"
                    : $"Permission ({doc.PermissionCref}):";
                content.Children.Add(BuildSectionLabel(permLabel));
                content.Children.Add(BuildRichBlock(
                    doc.Permission, _fg, marginLeft: SectionContentIndent));
            }

            // ── Completion list ───────────────────────────────────────────────────
            if (doc.CompletionList.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Completion List:"));
                foreach (var cl in doc.CompletionList)
                    content.Children.Add(BuildRichBlock(
                        cl, _paramNameBrush, marginLeft: SectionContentIndent));
            }

            // ── Bug / Todo ────────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(doc.Bug))
                content.Children.Add(
                    BuildAdmonitionBanner("🐛 Bug", doc.Bug, _bugBrush));

            if (!string.IsNullOrWhiteSpace(doc.Todo))
                content.Children.Add(
                    BuildAdmonitionBanner("✔ Todo", doc.Todo, _todoBrush));

            // ── See Also ──────────────────────────────────────────────────────────
            if (doc.SeeAlsos.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("See Also:"));
                var seePanel = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(SectionContentIndent, 0, 0, 0)
                };
                foreach (var sa in doc.SeeAlsos)
                {
                    var tb = new TextBlock { Margin = new Thickness(0, 0, 12, 0) };
                    tb.Inlines.Add(BuildLink(sa.Label, sa.Cref, sa.Href));
                    seePanel.Children.Add(tb);
                }
                content.Children.Add(seePanel);
            }

            // ── Meta-information strip ─────────────────────────────────────────────
            // Only rendered if at least one meta field is present.
            bool hasMeta =
                !string.IsNullOrWhiteSpace(doc.Since)
                || !string.IsNullOrWhiteSpace(doc.Version)
                || !string.IsNullOrWhiteSpace(doc.Author)
                || !string.IsNullOrWhiteSpace(doc.Date)
                || !string.IsNullOrWhiteSpace(doc.Copyright);

            if (hasMeta)
            {
                content.Children.Add(BuildSectionLabel("Info:"));
                var metaPanel = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(SectionContentIndent, 0, 0, 2)
                };

                void AddMeta(string label, string value)
                {
                    if (string.IsNullOrWhiteSpace(value)) return;
                    var tb = new TextBlock
                    {
                        FontFamily = _editorFont,
                        FontSize = _fontSize - 1,
                        Margin = new Thickness(0, 0, 16, 2)
                    };
                    tb.Inlines.Add(new Run(label + " ")
                    {
                        Foreground = _sectionLabelBrush,
                        FontStyle = FontStyles.Italic
                    });
                    tb.Inlines.Add(new Run(value) { Foreground = _metaBrush });
                    metaPanel.Children.Add(tb);
                }

                AddMeta("Since:", doc.Since);
                AddMeta("Version:", doc.Version);
                AddMeta("Author:", doc.Author);
                AddMeta("Date:", doc.Date);
                AddMeta("Copyright:", doc.Copyright);

                content.Children.Add(metaPanel);
            }

            Children.Add(outerGrid);
        }

        // ── Admonition banner ─────────────────────────────────────────────────────
        //
        // Renders a coloured left-border box for Note, Warning, Deprecated, Bug, Todo.

        /// <summary>
        /// Creates an admonition banner UI element with a colored left accent bar and title.<br/>
        /// Used for rendering Note, Warning, Deprecated, Bug, and Todo sections.
        /// </summary>
        /// <param name="title">
        /// The title text displayed at the top of the banner (e.g., "⚠ Warning", "ℹ Note").
        /// </param>
        /// <param name="body">
        /// The body text content of the admonition, rendered as mixed prose below the title.
        /// </param>
        /// <param name="accentBrush">
        /// The brush used for the left accent bar and the title text, defining the admonition's color identity.
        /// </param>
        /// <returns>
        /// A <see cref="Grid"/> containing the accent bar and vertically stacked title/body content.
        /// </returns>
        /// <remarks>
        /// <para>The layout uses a 3-column grid:</para>
        /// <list type="bullet">
        /// <item><description>Column 0: 3px wide accent bar with rounded corners.</description></item>
        /// <item><description>Column 1: 6px gap for spacing.</description></item>
        /// <item><description>Column 2: StackPanel with title (bold, semi-bold) and body text.</description></item>
        /// </list>
        /// <para>The title is rendered with <see cref="FontWeights.SemiBold"/> weight and the accent color.<br/>
        /// The body is rendered via <see cref="BuildMixedPanel"/> to support inline code and links.</para>
        /// </remarks>
        private UIElement BuildAdmonitionBanner(string title, string body, Brush accentBrush)
        {
            var grid = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var accentBar = new System.Windows.Shapes.Rectangle
            {
                Fill = accentBrush,
                RadiusX = 1,
                RadiusY = 1,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            Grid.SetColumn(accentBar, 0);
            grid.Children.Add(accentBar);

            var inner = new StackPanel { Orientation = Orientation.Vertical };
            Grid.SetColumn(inner, 2);
            grid.Children.Add(inner);

            // Title row
            inner.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = accentBrush,
                FontFamily = _editorFont,
                FontSize = _fontSize - 0.5,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });

            // Body text
            if (!string.IsNullOrWhiteSpace(body))
                inner.Children.Add(BuildMixedPanel(body, _fg));

            return grid;
        }

        // ── Section label ─────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a section label text element with italic styling and the section label brush.<br/>
        /// Used for headers like "Parameters:", "Returns:", "Exceptions:", etc.
        /// </summary>
        /// <param name="text">
        /// The label text to display (e.g., "Parameters:", "See Also:").
        /// </param>
        /// <returns>
        /// A <see cref="TextBlock"/> with italic font style, section label foreground color,<br/>
        /// and appropriate top/bottom margins for visual separation.
        /// </returns>
        private UIElement BuildSectionLabel(string text) =>
            new TextBlock
            {
                Text = text,
                Foreground = _sectionLabelBrush,
                FontFamily = _editorFont,
                FontSize = _fontSize,
                Margin = new Thickness(0, 6, 0, 2),
                FontStyle = FontStyles.Italic
            };

        // ── Rich text block ───────────────────────────────────────────────────────

        /// <summary>
        /// Creates a rich text block UI element with formatted inline content.<br/>
        /// This method wraps prose text with support for links, inline code, parameter references,<br/>
        /// and text formatting (bold, italic, underline, strikethrough) via <see cref="BuildInlines"/>.
        /// </summary>
        /// <param name="text">
        /// The text content to render, which may contain tokenized markup like<br/>
        /// <c>`code`</c>, <c>[LINK href=...]...</c>, <c>[BOLD]...[/BOLD]</c>, etc.
        /// </param>
        /// <param name="fg">
        /// The foreground brush for the text content.
        /// </param>
        /// <param name="marginBottom">
        /// The bottom margin in pixels for spacing below the text block (default: 0).
        /// </param>
        /// <param name="marginLeft">
        /// The left margin in pixels for indentation (default: 0).
        /// </param>
        /// <returns>
        /// A <see cref="TextBlock"/> with <see cref="TextWrapping.Wrap"/> enabled and<br/>
        /// parsed inline content added to its <see cref="TextBlock.Inlines"/> collection.
        /// </returns>
        private UIElement BuildRichBlock(string text, Brush fg,
            double marginBottom = 0, double marginLeft = 0)
        {
            var tb = new TextBlock
            {
                Foreground = fg,
                FontFamily = _editorFont,
                FontSize = _fontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(marginLeft, 0, 0, marginBottom)
            };
            BuildInlines(text, tb.Inlines, fg);
            return tb;
        }

        // ── Mixed prose + code panel ──────────────────────────────────────────────
        //
        // Renders content that can contain both prose and [CODE]…[/CODE] blocks.
        // WPF does not correctly lay out a block-level Border embedded as an
        // InlineUIContainer inside a TextBlock — it silently collapses the layout.
        // We avoid that by tokenising the content, accumulating prose tokens into
        // a TextBlock, and flushing a new Border-based code block as a separate
        // direct child of a vertical StackPanel whenever a CodeBlock token appears.

        /// <summary>
        /// Creates a mixed content panel that renders both prose text and block-level code elements.<br/>
        /// This method solves a WPF limitation where block-level <see cref="Border"/> elements inside<br/>
        /// <see cref="InlineUIContainer"/> are silently collapsed within <see cref="TextBlock"/>.
        /// </summary>
        /// <param name="text">
        /// The content text which may contain prose segments and <c>[CODE]...[/CODE]</c> blocks.
        /// </param>
        /// <param name="fg">
        /// The foreground brush for prose text content.
        /// </param>
        /// <param name="marginLeft">
        /// The left margin in pixels for indenting the entire panel (default: 0).
        /// </param>
        /// <returns>
        /// A <see cref="StackPanel"/> with vertical orientation containing alternating<br/>
        /// <see cref="TextBlock"/> (for prose) and <see cref="Border"/> (for code blocks) children.
        /// </returns>
        /// <remarks>
        /// <para>The method uses a tokenization approach with the following logic:</para>
        /// <list type="number">
        /// <item><description>Tokenizes the input via <see cref="Tokenise"/> into segments of different kinds.</description></item>
        /// <item><description>Accumulates prose text into a current <see cref="TextBlock"/> (created on-demand via <c>EnsureTb</c>).</description></item>
        /// <item><description>When a code block token is encountered, flushes the current TextBlock via <c>FlushTb</c> and creates a new <see cref="Border"/> via <see cref="BuildCodeBlock"/>.</description></item>
        /// <item><description>For inline elements (code, links, param refs, formatting), adds them to the current TextBlock.</description></item>
        /// <item><description>After processing all tokens, flushes any remaining prose text.</description></item>
        /// </list>
        /// <para>Supported token types:</para>
        /// <list type="bullet">
        /// <item><description><see cref="SegKind.Text"/> — Plain prose text with paragraph and line break support.</description></item>
        /// <item><description><see cref="SegKind.CodeBlock"/> — Full-width code block with monospaced font and background.</description></item>
        /// <item><description><see cref="SegKind.InlineCode"/> — Inline code snippet with rounded background border.</description></item>
        /// <item><description><see cref="SegKind.Link"/> — Hyperlink with cref or href navigation.</description></item>
        /// <item><description><see cref="SegKind.ParamRef"/> — Parameter reference with monospaced styling.</description></item>
        /// <item><description><see cref="SegKind.Bold"/>, <see cref="SegKind.Italic"/>, <see cref="SegKind.Underline"/>, <see cref="SegKind.Strike"/> — Text formatting.</description></item>
        /// </list>
        /// </remarks>
        private UIElement BuildMixedPanel(string text, Brush fg, double marginLeft = 0)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(marginLeft, 0, 0, 0)
            };

            TextBlock currentTb = null;

            void FlushTb()
            {
                if (currentTb != null && currentTb.Inlines.Count > 0)
                    panel.Children.Add(currentTb);
                currentTb = null;
            }

            void EnsureTb()
            {
                if (currentTb == null)
                    currentTb = new TextBlock
                    {
                        Foreground = fg,
                        FontFamily = _editorFont,
                        FontSize = _fontSize,
                        TextWrapping = TextWrapping.Wrap,
                        Margin = new Thickness(0, 0, 0, 2)
                    };
            }

            foreach (var seg in Tokenise(text))
            {
                switch (seg.Kind)
                {
                    case SegKind.CodeBlock:
                        FlushTb();
                        panel.Children.Add(BuildCodeBlock(seg.Value));
                        break;

                    case SegKind.Text:
                        EnsureTb();
                        var paras = seg.Value.Split(new[] { "\n\n" }, StringSplitOptions.None);
                        for (int p = 0; p < paras.Length; p++)
                        {
                            if (p > 0)
                            {
                                currentTb.Inlines.Add(new LineBreak());
                                currentTb.Inlines.Add(new LineBreak());
                            }
                            var lines = paras[p].Split('\n');
                            for (int l = 0; l < lines.Length; l++)
                            {
                                if (l > 0)
                                    currentTb.Inlines.Add(new LineBreak());
                                currentTb.Inlines.Add(new Run(lines[l]) { Foreground = fg });
                            }
                        }
                        break;

                    case SegKind.InlineCode:
                        EnsureTb();
                        currentTb.Inlines.Add(new InlineUIContainer(new Border
                        {
                            Background = _codeBg,
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(3, 1, 3, 1),
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Child = new TextBlock
                            {
                                Text = seg.Value,
                                FontFamily = _monoFont,
                                FontSize = _fontSize - 1,
                                Foreground = _codeFg,
                                VerticalAlignment = VerticalAlignment.Bottom,
                                Padding = new Thickness(0)
                            }
                        })
                        {
                            BaselineAlignment = BaselineAlignment.TextBottom
                        });
                        break;

                    case SegKind.Link:
                        EnsureTb();
                        currentTb.Inlines.Add(BuildLink(seg.Label, seg.Cref, seg.Href));
                        break;

                    case SegKind.ParamRef:
                        EnsureTb();
                        currentTb.Inlines.Add(new Run(seg.Value)
                        {
                            Foreground = _paramNameBrush,
                            FontFamily = _monoFont,
                            FontSize = _fontSize - 1
                        });
                        break;

                    case SegKind.Bold:
                    case SegKind.Italic:
                    case SegKind.Underline:
                    case SegKind.Strike:
                        EnsureTb();
                        currentTb.Inlines.Add(BuildFormattedSpan(seg.Kind, seg.Value, fg));
                        break;
                }
            }

            FlushTb();
            return panel;
        }

        // ── Inline builder ────────────────────────────────────────────────────────

        /// <summary>
        /// Builds inline text elements into the specified <see cref="InlineCollection"/> from tokenized text.<br/>
        /// This is the entry point for creating formatted inline content within a text block.
        /// </summary>
        /// <param name="text">
        /// The text content containing tokenized markup to parse into inline elements.
        /// </param>
        /// <param name="inlines">
        /// The <see cref="InlineCollection"/> to append the generated inline elements to.
        /// </param>
        /// <param name="fg">
        /// The default foreground brush for plain text segments.
        /// </param>
        /// <seealso cref="BuildInlinesInto"/>
        /// <seealso cref="Tokenise"/>
        private void BuildInlines(string text, InlineCollection inlines, Brush fg)
            => BuildInlinesInto(text, inlines, fg);

        // ── Formatted span (bold / italic / underline / strike) with nested support ──
        //
        // Tokenises inner content recursively so combinations like <b><i>x</i></b> work.

        /// <summary>
        /// Creates a formatted span element for bold, italic, underline, or strikethrough text.<br/>
        /// Supports nested formatting by recursively tokenizing the inner content.
        /// </summary>
        /// <param name="kind">
        /// The type of formatting to apply, specified as a <see cref="SegKind"/> enum value.
        /// </param>
        /// <param name="innerText">
        /// The inner text content which may itself contain nested formatting tokens.
        /// </param>
        /// <param name="fg">
        /// The default foreground brush for plain text segments within the span.
        /// </param>
        /// <returns>
        /// A <see cref="Span"/> subclass (<see cref="Bold"/>, <see cref="Italic"/>, <see cref="Underline"/>,<br/>
        /// or <see cref="Span"/> with <see cref="TextDecorations.Strikethrough"/>) containing<br/>
        /// recursively parsed inline content.
        /// </returns>
        /// <remarks>
        /// <para>The method enables nested formatting by calling <see cref="BuildInlinesInto"/><br/>
        /// on the inner text, allowing combinations like <c>[BOLD][ITALIC]text[/ITALIC][/BOLD]</c>.</para>
        /// <para>Strike-through is handled specially by setting <see cref="Span.TextDecorations"/><br/>
        /// to <see cref="TextDecorations.Strikethrough"/> on a generic <see cref="Span"/>.</para>
        /// </remarks>
        private Span BuildFormattedSpan(SegKind kind, string innerText, Brush fg)
        {
            Span span;
            switch (kind)
            {
                case SegKind.Bold: span = new Bold(); break;
                case SegKind.Italic: span = new Italic(); break;
                case SegKind.Underline: span = new Underline(); break;
                default: span = new Span(); break; // Strike handled separately
            }
            if (kind == SegKind.Strike)
                span.TextDecorations = TextDecorations.Strikethrough;

            BuildInlinesInto(innerText, span.Inlines, fg);
            return span;
        }

        /// <summary>
        /// Shared recursive inline parser that tokenizes text and adds formatted elements<br/>
        /// to the specified <see cref="InlineCollection"/>. Used by both <see cref="BuildInlines"/><br/>
        /// and <see cref="BuildFormattedSpan"/> for consistent inline rendering.
        /// </summary>
        /// <param name="text">
        /// The text content to tokenize and parse into inline elements.
        /// </param>
        /// <param name="inlines">
        /// The <see cref="InlineCollection"/> to append the generated inline elements to.
        /// </param>
        /// <param name="fg">
        /// The default foreground brush for plain text segments.
        /// </param>
        /// <remarks>
        /// <para>The method iterates through tokens from <see cref="Tokenise"/> and handles each type:</para>
        /// <list type="bullet">
        /// <item><description><see cref="SegKind.Text"/>: Splits on double newlines (paragraphs) and single newlines, adding <see cref="Run"/> and <see cref="LineBreak"/> elements.</description></item>
        /// <item><description><see cref="SegKind.InlineCode"/>: Creates an <see cref="InlineUIContainer"/> with a rounded <see cref="Border"/> containing monospaced text.</description></item>
        /// <item><description><see cref="SegKind.Link"/>: Creates a <see cref="Hyperlink"/> via <see cref="BuildLink"/>.</description></item>
        /// <item><description><see cref="SegKind.ParamRef"/>: Creates a monospaced <see cref="Run"/> with parameter name styling.</description></item>
        /// <item><description><see cref="SegKind.CodeBlock"/>: Wraps a <see cref="BuildCodeBlock"/> in an <see cref="InlineUIContainer"/>.</description></item>
        /// <item><description><see cref="SegKind.Bold"/>, <see cref="SegKind.Italic"/>, <see cref="SegKind.Underline"/>, <see cref="SegKind.Strike"/>: Recursively calls <see cref="BuildFormattedSpan"/>.</description></item>
        /// </list>
        /// </remarks>
        private void BuildInlinesInto(string text, InlineCollection inlines, Brush fg)
        {
            foreach (var seg in Tokenise(text))
            {
                switch (seg.Kind)
                {
                    case SegKind.Text:
                        var paras = seg.Value.Split(new[] { "\n\n" }, StringSplitOptions.None);
                        for (int i = 0; i < paras.Length; i++)
                        {
                            if (i > 0) { inlines.Add(new LineBreak()); inlines.Add(new LineBreak()); }
                            var lines = paras[i].Split('\n');
                            for (int j = 0; j < lines.Length; j++)
                            {
                                if (j > 0) inlines.Add(new LineBreak());
                                inlines.Add(new Run(lines[j]) { Foreground = fg });
                            }
                        }
                        break;

                    case SegKind.InlineCode:
                        inlines.Add(new InlineUIContainer(new Border
                        {
                            Background = _codeBg,
                            CornerRadius = new CornerRadius(2),
                            Padding = new Thickness(3, 1, 3, 1),
                            VerticalAlignment = VerticalAlignment.Bottom,
                            Child = new TextBlock
                            {
                                Text = seg.Value,
                                FontFamily = _monoFont,
                                FontSize = _fontSize - 1,
                                Foreground = _codeFg,
                                VerticalAlignment = VerticalAlignment.Bottom,
                                Padding = new Thickness(0)
                            }
                        })
                        {
                            BaselineAlignment = BaselineAlignment.TextBottom
                        });
                        break;

                    case SegKind.Link:
                        inlines.Add(BuildLink(seg.Label, seg.Cref, seg.Href));
                        break;

                    case SegKind.ParamRef:
                        inlines.Add(new Run(seg.Value)
                        {
                            Foreground = _paramNameBrush,
                            FontFamily = _monoFont,
                            FontSize = _fontSize - 1
                        });
                        break;

                    case SegKind.CodeBlock:
                        inlines.Add(new InlineUIContainer(BuildCodeBlock(seg.Value))
                        {
                            BaselineAlignment = BaselineAlignment.Center
                        });
                        break;

                    case SegKind.Bold:
                    case SegKind.Italic:
                    case SegKind.Underline:
                    case SegKind.Strike:
                        inlines.Add(BuildFormattedSpan(seg.Kind, seg.Value, fg));
                        break;
                }
            }
        }

        // ── Code block ────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a code block UI element with monospaced font, background, and rounded corners.<br/>
        /// Used for rendering multi-line code examples within documentation.
        /// </summary>
        /// <param name="code">
        /// The raw code text to display, which will be trimmed of leading/trailing whitespace.
        /// </param>
        /// <param name="marginLeft">
        /// The left margin in pixels for indenting the code block (default: 0).
        /// </param>
        /// <returns>
        /// A <see cref="Border"/> with rounded corners containing a monospaced <see cref="TextBlock"/>.<br/>
        /// The border uses <see cref="_codeBg"/> for background and the text uses <see cref="_codeFg"/>.
        /// </returns>
        /// <remarks>
        /// <para>The code block styling includes:</para>
        /// <list type="bullet">
        /// <item><description>Background: <see cref="_codeBg"/> — semi-transparent theme-adaptive brush.</description></item>
        /// <item><description>Font: <see cref="_monoFont"/> — Cascadia Mono, Consolas, or Courier New fallback.</description></item>
        /// <item><description>Size: <see cref="_fontSize"/> minus 1 unit for slightly smaller code text.</description></item>
        /// <item><description>Corners: 3px radius for rounded appearance.</description></item>
        /// <item><description>Padding: 8px horizontal, 4px vertical for comfortable reading.</description></item>
        /// <item><description>Wrapping: <see cref="TextWrapping.Wrap"/> enabled for long lines.</description></item>
        /// </list>
        /// </remarks>
        private UIElement BuildCodeBlock(string code, double marginLeft = 0) =>
            new Border
            {
                Background = _codeBg,
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(marginLeft, 2, 0, 4),
                Child = new TextBlock
                {
                    Text = code.Trim(),
                    FontFamily = _monoFont,
                    FontSize = _fontSize - 1,
                    Foreground = _codeFg,
                    TextWrapping = TextWrapping.Wrap
                }
            };

        // ── Param grid ────────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a parameter grid layout for rendering parameter, type parameter, exception,<br/>
        /// or return value documentation in a structured tabular format.
        /// </summary>
        /// <param name="marginLeft">
        /// The left margin in pixels for indenting the entire grid (default: 0).
        /// </param>
        /// <returns>
        /// A <see cref="Grid"/> with 4 columns:
        /// <list type="bullet">
        /// <item><description>Column 0: Direction badge (auto-sized, hidden when empty) — shows <c>[in]</c>, <c>[out]</c>, <c>[in,out]</c>.</description></item>
        /// <item><description>Column 1: Parameter name (auto-sized) — rendered with <see cref="_paramNameBrush"/> and monospaced font.</description></item>
        /// <item><description>Column 2: Dash separator (auto-sized) — " — " separator in dimmed color.</description></item>
        /// <item><description>Column 3: Description (star-sized, takes remaining space) — supports rich inline formatting.</description></item>
        /// </list>
        /// </returns>
        /// <remarks>
        /// Each row is added dynamically via <see cref="AddParamRow"/> which creates the appropriate<br/>
        /// elements for each column and manages row-specific styling and formatting.
        /// </remarks>
        private Grid BuildParamGrid(double marginLeft = 0)
        {
            var grid = new Grid { Margin = new Thickness(marginLeft, 0, 0, 2) };
            // Col 0: direction badge (hidden when empty)
            // Col 1: name
            // Col 2: dash spacer
            // Col 3: description
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        // ── AddParamRow ───────────────────────────────────────────────────────────
        //
        // Overload that accepts direction (C++ \param[in] / [out] / [in,out]) and
        // an optional isTypeParam flag (renders <T> prefix style).
        // For exceptions the navCref is provided for click-to-navigate.

        /// <summary>
        /// Adds a single parameter row to the parameter grid, creating name, direction,<br/>
        /// separator, and description elements in the appropriate grid columns.
        /// </summary>
        /// <param name="grid">
        /// The target <see cref="Grid"/> where the row will be added.
        /// </param>
        /// <param name="name">
        /// The parameter name to display. For type parameters, wrapped in angle brackets.
        /// </param>
        /// <param name="description">
        /// The description text for the parameter, supporting rich inline formatting.
        /// </param>
        /// <param name="row">
        /// Reference to the current row index, incremented after adding the row.<br/>
        /// Used to track grid row placement across multiple calls.
        /// </param>
        /// <param name="navCref">
        /// Optional cref string for exception types. When provided, the parameter name<br/>
        /// becomes a clickable hyperlink for navigation (used for <c>&lt;exception&gt;</c> tags).
        /// </param>
        /// <param name="direction">
        /// Optional direction string for C++ parameters (e.g., <c>"in"</c>, <c>"out"</c>, <c>"in,out"</c>).<br/>
        /// Rendered as a badge in column 0 when non-empty.
        /// </param>
        /// <param name="isTypeParam">
        /// When <c>true</c>, renders the parameter name with angle brackets (<c>&lt;T&gt;</c>)<br/>
        /// and slightly smaller font to distinguish type parameters from value parameters.
        /// </param>
        /// <remarks>
        /// <para>The method creates the following grid children:</para>
        /// <list type="bullet">
        /// <item><description><b>Column 0:</b> Direction badge — <see cref="Border"/> with <see cref="_codeBg"/> background and <see cref="_noteBrush"/> text (only if <paramref name="direction"/> is non-empty).</description></item>
        /// <item><description><b>Column 1:</b> Parameter name — either a clickable <see cref="Hyperlink"/> (if <paramref name="navCref"/> is provided) or styled <see cref="TextBlock"/> with monospaced font.</description></item>
        /// <item><description><b>Column 2:</b> Dash separator — " — " in <see cref="_fgDim"/> for visual separation (only if name or description exists).</description></item>
        /// <item><description><b>Column 3:</b> Description — <see cref="TextBlock"/> with <see cref="TextWrapping.Wrap"/> and inline formatting via <see cref="BuildInlines"/>.</description></item>
        /// </list>
        /// <para>Type parameters (<paramref name="isTypeParam"/> = <c>true</c>) render with &lt;angle brackets&gt;<br/>
        /// and the bracket characters use <see cref="_fgDim"/> while the name uses <see cref="_paramNameBrush"/>.</para>
        /// </remarks>
        private void AddParamRow(Grid grid, string name, string description,
            ref int row,
            string navCref = null,
            string direction = "",
            bool isTypeParam = false)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // ── Column 0: direction badge ─────────────────────────────────────────
            if (!string.IsNullOrEmpty(direction))
            {
                var badge = new Border
                {
                    Background = _codeBg,
                    CornerRadius = new CornerRadius(2),
                    Padding = new Thickness(3, 1, 3, 1),
                    Margin = new Thickness(0, 2, 4, 1),
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new TextBlock
                    {
                        Text = direction,
                        FontFamily = _monoFont,
                        FontSize = _fontSize - 2,
                        Foreground = _noteBrush,
                        Padding = new Thickness(0)
                    }
                };
                Grid.SetRow(badge, row); Grid.SetColumn(badge, 0);
                grid.Children.Add(badge);
            }

            // ── Column 1: name ────────────────────────────────────────────────────
            if (!string.IsNullOrEmpty(navCref))
            {
                // Exception-style: name is a clickable link.
                var tb = new TextBlock
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Top
                };
                tb.Inlines.Add(BuildLink(name, navCref, string.Empty));
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 1);
                grid.Children.Add(tb);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                var prefix = isTypeParam ? "<" : string.Empty;
                var suffix = isTypeParam ? ">" : string.Empty;
                var nameTb = new TextBlock
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Top
                };
                if (isTypeParam)
                {
                    nameTb.Inlines.Add(new Run(prefix) { Foreground = _fgDim, FontFamily = _monoFont, FontSize = _fontSize - 1 });
                    nameTb.Inlines.Add(new Run(name) { Foreground = _paramNameBrush, FontFamily = _monoFont, FontSize = _fontSize - 1 });
                    nameTb.Inlines.Add(new Run(suffix) { Foreground = _fgDim, FontFamily = _monoFont, FontSize = _fontSize - 1 });
                }
                else
                {
                    nameTb.Inlines.Add(new Run(name)
                    {
                        Foreground = _paramNameBrush,
                        FontFamily = _monoFont,
                        FontSize = _fontSize - 1
                    });
                }
                Grid.SetRow(nameTb, row); Grid.SetColumn(nameTb, 1);
                grid.Children.Add(nameTb);
            }

            // ── Column 2: dash ────────────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(description) || !string.IsNullOrEmpty(name))
            {
                var dashTb = new TextBlock
                {
                    Text = " — ",
                    Foreground = _fgDim,
                    FontFamily = _editorFont,
                    FontSize = _fontSize,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                Grid.SetRow(dashTb, row); Grid.SetColumn(dashTb, 2);
                grid.Children.Add(dashTb);
            }

            // ── Column 3: description ─────────────────────────────────────────────
            if (!string.IsNullOrWhiteSpace(description))
            {
                var descTb = new TextBlock
                {
                    FontFamily = _editorFont,
                    FontSize = _fontSize,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                BuildInlines(description, descTb.Inlines, _fg);
                Grid.SetRow(descTb, row); Grid.SetColumn(descTb, 3);
                grid.Children.Add(descTb);
            }

            row++;
        }

        // ── Link / cref ───────────────────────────────────────────────────────────

        /// <summary>
        /// Creates a hyperlink inline element with navigation support for both external URLs<br/>
        /// and internal code symbol references (cref).
        /// </summary>
        /// <param name="label">
        /// The display text for the hyperlink.
        /// </param>
        /// <param name="cref">
        /// The code reference string (e.g., <c>M:System.String.IsNullOrEmpty(System.String)</c>).<br/>
        /// Used when <paramref name="href"/> is empty to enable symbol navigation via DTE.
        /// </param>
        /// <param name="href">
        /// An external URL for web navigation. Takes precedence over <paramref name="cref"/><br/>
        /// when both are provided — opens the URL in the default browser.
        /// </param>
        /// <returns>
        /// A <see cref="Hyperlink"/> with underline text decoration and <see cref="_linkBrush"/> foreground.
        /// </returns>
        /// <remarks>
        /// <para>The method supports two navigation modes:</para>
        /// <list type="number">
        /// <item>
        /// <description><b>External URL navigation (href):</b>
        ///   <list type="bullet">
        ///   <item><description>Parses <paramref name="href"/> as a <see cref="Uri"/>.</description></item>
        ///   <item><description>Subscribes to <see cref="Hyperlink.RequestNavigate"/> to open the URL via <see cref="System.Diagnostics.Process.Start"/>.</description></item>
        ///   <item><description>Marks the event as handled to prevent default behavior.</description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// <item>
        /// <description><b>Internal symbol navigation (cref):</b>
        ///   <list type="bullet">
        ///   <item><description>Simplifies the cref via <see cref="DocCommentParser.SimplifyCref"/> to extract the symbol name.</description></item>
        ///   <item><description>Subscribes to <see cref="Hyperlink.Click"/> to perform a Visual Studio search-and-navigate operation.</description></item>
        ///   <item><description>Uses the DTE (Development Tools Environment) to:
        ///     <list type="bullet">
        ///     <item><description>Access the active document and text selection.</description></item>
        ///     <item><description>Configure the Find dialog with the symbol name (case-sensitive, whole word).</description></item>
        ///     <item><description>Execute the find operation in the current document.</description></item>
        ///     <item><description>Navigate to the definition if found, or open "Navigate To" dialog otherwise.</description></item>
        ///     </list>
        ///   </description></item>
        ///   </list>
        /// </description>
        /// </item>
        /// </list>
        /// <para>All DTE operations are wrapped in try-catch to prevent navigation failures<br/>
        /// from crashing the editor or disrupting the user experience.</para>
        /// </remarks>
        private Inline BuildLink(string label, string cref, string href)
        {
            var hl = new Hyperlink(new Run(label))
            {
                Foreground = _linkBrush,
                TextDecorations = TextDecorations.Underline
            };

            if (!string.IsNullOrEmpty(href))
            {
                try
                {
                    hl.NavigateUri = new Uri(href);
                    hl.RequestNavigate += (s, e) =>
                    {
                        try { System.Diagnostics.Process.Start(e.Uri.AbsoluteUri); }
                        catch { }
                        e.Handled = true;
                    };
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(cref))
            {
                var symbolName = DocCommentParser.SimplifyCref(cref);
                hl.Click += (s, e) =>
                {
                    try
                    {
                        var dte = Microsoft.VisualStudio.Shell.Package
                            .GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                        if (dte == null) return;

                        var doc = dte.ActiveDocument;
                        var sel = doc?.Selection as EnvDTE.TextSelection;
                        if (sel == null) return;

                        var find = dte.Find;
                        find.FindWhat = symbolName;
                        find.MatchCase = true;
                        find.MatchWholeWord = true;
                        find.Target = EnvDTE.vsFindTarget.vsFindTargetCurrentDocument;
                        find.Action = EnvDTE.vsFindAction.vsFindActionFind;
                        var findResult = find.Execute();

                        dte.ExecuteCommand(
                            findResult == EnvDTE.vsFindResult.vsFindResultFound
                                ? "Edit.GoToDefinition"
                                : "Edit.NavigateTo");
                    }
                    catch { }
                };
            }

            return hl;
        }

        // ── Tokeniser ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Enumeration of segment types recognized by the text tokenizer.<br/>
        /// Each value represents a different kind of formatted content in documentation text.
        /// </summary>
        private enum SegKind
        {
            /// <summary>Plain text content without formatting.</summary>
            Text,
            /// <summary>Inline code snippet enclosed in backticks (<c>`code`</c>).</summary>
            InlineCode,
            /// <summary>Hyperlink with href or cref navigation target.</summary>
            Link,
            /// <summary>Parameter reference (<c>&lt;paramref&gt;</c>) rendered in monospaced font.</summary>
            ParamRef,
            /// <summary>Block-level code section enclosed in <c>[CODE]...[/CODE]</c>.</summary>
            CodeBlock,
            /// <summary>Bold text (<c>[BOLD]...[/BOLD]</c>).</summary>
            Bold,
            /// <summary>Italic text (<c>[ITALIC]...[/ITALIC]</c>).</summary>
            Italic,
            /// <summary>Underlined text (<c>[UNDERLINE]...[/UNDERLINE]</c>).</summary>
            Underline,
            /// <summary>Strikethrough text (<c>[STRIKE]...[/STRIKE]</c>).</summary>
            Strike
        }

        /// <summary>
        /// Data class representing a tokenized segment of documentation text.<br/>
        /// Each instance holds the segment type and associated content values.
        /// </summary>
        private class Seg
        {
            /// <summary>The type of content segment.</summary>
            public SegKind Kind;
            /// <summary>The primary text value of the segment.</summary>
            public string Value = string.Empty;
            /// <summary>The display label for link segments.</summary>
            public string Label = string.Empty;
            /// <summary>The external URL for href-based link segments.</summary>
            public string Href = string.Empty;
            /// <summary>The code reference for cref-based link segments.</summary>
            public string Cref = string.Empty;
        }

        /// <summary>
        /// Compiled regular expression that matches all supported documentation text tokens.<br/>
        /// Uses named capture groups to identify token types and extract their content.
        /// </summary>
        /// <remarks>
        /// <para>The regex matches the following patterns (in priority order):</para>
        /// <list type="bullet">
        /// <item><description><c>`code`</c> — Inline code (capture group: <c>code</c>).</description></item>
        /// <item><description><c>[LINK href=url]label[/LINK]</c> — External URL link (capture groups: <c>href</c>, <c>hlabel</c>).</description></item>
        /// <item><description><c>[LINK cref=cref]label[/LINK]</c> — Symbol reference link (capture groups: <c>cref</c>, <c>clabel</c>).</description></item>
        /// <item><description><c>[PARAMREF]name[/PARAMREF]</c> — Parameter reference (capture group: <paramref name="paramref"/></description></item>
        /// <item><description><c>[CODE]...[/CODE]</c> — Block code section (capture group: <c>codeblock</c>, supports multiline with <c>[\s\S]*?</c>).</description></item>
        /// <item><description><c>[BOLD]...[/BOLD]</c> — Bold text (capture group: <c>bold</c>, multiline).</description></item>
        /// <item><description><c>[ITALIC]...[/ITALIC]</c> — Italic text (capture group: <c>italic</c>, multiline).</description></item>
        /// <item><description><c>[UNDERLINE]...[/UNDERLINE]</c> — Underline text (capture group: <c>underline</c>, multiline).</description></item>
        /// <item><description><c>[STRIKE]...[/STRIKE]</c> — Strikethrough text (capture group: <c>strike</c>, multiline).</description></item>
        /// </list>
        /// </remarks>
        private static readonly Regex TokenRegex = new Regex(
            @"`(?<code>[^`]+)`" +
            @"|\[LINK href=(?<href>[^\]]+)\](?<hlabel>[^\[]+)\[/LINK\]" +
            @"|\[LINK cref=(?<cref>[^\]]+)\](?<clabel>[^\[]+)\[/LINK\]" +
            @"|\[PARAMREF\](?<paramref>[^\[]+)\[/PARAMREF\]" +
            @"|\[CODE\](?<codeblock>[\s\S]*?)\[/CODE\]" +
            @"|\[BOLD\](?<bold>[\s\S]*?)\[/BOLD\]" +
            @"|\[ITALIC\](?<italic>[\s\S]*?)\[/ITALIC\]" +
            @"|\[UNDERLINE\](?<underline>[\s\S]*?)\[/UNDERLINE\]" +
            @"|\[STRIKE\](?<strike>[\s\S]*?)\[/STRIKE\]",
            RegexOptions.Compiled);

        /// <summary>
        /// Tokenizes documentation text into a list of typed segments based on markup patterns.<br/>
        /// Plain text between markup tokens is collected as <see cref="SegKind.Text"/> segments.
        /// </summary>
        /// <param name="input">
        /// The input text string potentially containing markup tokens for parsing.
        /// </param>
        /// <returns>
        /// A list of <see cref="Seg"/> objects representing the sequential segments found in the input.<br/>
        /// The list always covers the entire input string with no gaps or overlaps.
        /// </returns>
        /// <remarks>
        /// <para>The method uses <see cref="TokenRegex"/> to find all markup matches in the input.<br/>
        /// For each match, it creates a segment based on which named capture group succeeded:</para>
        /// <list type="bullet">
        /// <item><description><c>code</c> → <see cref="SegKind.InlineCode"/> with <see cref="Seg.Value"/> set to the matched code text.</description></item>
        /// <item><description><c>hlabel</c> → <see cref="SegKind.Link"/> with <see cref="Seg.Href"/> and <see cref="Seg.Label"/> populated.</description></item>
        /// <item><description><c>clabel</c> → <see cref="SegKind.Link"/> with <see cref="Seg.Cref"/> and <see cref="Seg.Label"/> populated.</description></item>
        /// <item><description><paramref name="paramref"/> → <see cref="SegKind.ParamRef"/> with <see cref="Seg.Value"/> set to the parameter name.</description></item>
        /// <item><description><c>codeblock</c> → <see cref="SegKind.CodeBlock"/> with <see cref="Seg.Value"/> set to the code content.</description></item>
        /// <item><description><c>bold</c>, <c>italic</c>, <c>underline</c>, <c>strike</c> → Corresponding <see cref="SegKind"/> values.</description></item>
        /// </list>
        /// <para>Text between matches (including before the first and after the last markup)<br/>
        /// is collected as <see cref="SegKind.Text"/> segments, preserving the original content order.</para>
        /// </remarks>
        private List<Seg> Tokenise(string input)
        {
            var result = new List<Seg>();
            int lastIndex = 0;

            foreach (Match m in TokenRegex.Matches(input))
            {
                if (m.Index > lastIndex)
                    result.Add(new Seg
                    {
                        Kind = SegKind.Text,
                        Value = input.Substring(lastIndex, m.Index - lastIndex)
                    });

                if (m.Groups["code"].Success)
                    result.Add(new Seg
                    {
                        Kind = SegKind.InlineCode,
                        Value = m.Groups["code"].Value
                    });
                else if (m.Groups["hlabel"].Success)
                    result.Add(new Seg
                    {
                        Kind = SegKind.Link,
                        Label = m.Groups["hlabel"].Value,
                        Value = m.Groups["hlabel"].Value,
                        Href = m.Groups["href"].Value
                    });
                else if (m.Groups["clabel"].Success)
                    result.Add(new Seg
                    {
                        Kind = SegKind.Link,
                        Label = m.Groups["clabel"].Value,
                        Value = m.Groups["clabel"].Value,
                        Cref = m.Groups["cref"].Value
                    });
                else if (m.Groups["paramref"].Success)
                    result.Add(new Seg
                    {
                        Kind = SegKind.ParamRef,
                        Value = m.Groups["paramref"].Value
                    });
                else if (m.Groups["codeblock"].Success)
                    result.Add(new Seg
                    {
                        Kind = SegKind.CodeBlock,
                        Value = m.Groups["codeblock"].Value
                    });
                else if (m.Groups["bold"].Success)
                    result.Add(new Seg { Kind = SegKind.Bold, Value = m.Groups["bold"].Value });
                else if (m.Groups["italic"].Success)
                    result.Add(new Seg { Kind = SegKind.Italic, Value = m.Groups["italic"].Value });
                else if (m.Groups["underline"].Success)
                    result.Add(new Seg { Kind = SegKind.Underline, Value = m.Groups["underline"].Value });
                else if (m.Groups["strike"].Success)
                    result.Add(new Seg { Kind = SegKind.Strike, Value = m.Groups["strike"].Value });

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < input.Length)
                result.Add(new Seg
                {
                    Kind = SegKind.Text,
                    Value = input.Substring(lastIndex)
                });

            return result;
        }

        // ── Helpers ───────────────────────────────────────────────────────────────

        /// <summary>
        /// Calculates the relative luminance of a brush color to determine theme brightness.<br/>
        /// Used to adapt color palettes for dark versus light Visual Studio themes.
        /// </summary>
        /// <param name="brush">
        /// The brush to analyze for luminance. Expected to be a <see cref="SolidColorBrush"/><br/>
        /// for accurate calculation; other brush types return a default mid-range value.
        /// </param>
        /// <returns>
        /// A double between 0.0 (black) and 1.0 (white) representing the perceived brightness<br/>
        /// of the brush color, calculated using the sRGB luminance formula (Rec. 709 coefficients).<br/>
        /// Returns 0.5 for non-solid brushes or invalid input.
        /// </returns>
        /// <remarks>
        /// <para>The luminance calculation uses the standard formula:</para>
        /// <c>L = 0.2126 × R + 0.7152 × G + 0.0722 × B</c><br/>
        /// where R, G, B are normalized to [0, 1] range from [0, 255] byte values.
        /// <para>The coefficients reflect human perceptual sensitivity to different color channels,<br/>
        /// with green contributing most to perceived brightness and blue least.</para>
        /// <para>Threshold usage:</para>
        /// <list type="bullet">
        /// <item><description>Luminance &lt; 0.4 → Dark theme detection (used in <see cref="DocCommentControl"/> constructor).</description></item>
        /// <item><description>Luminance ≥ 0.4 → Light theme detection.</description></item>
        /// </list>
        /// </remarks>
        private static double GetLuminance(Brush brush)
        {
            if (brush is SolidColorBrush scb)
            {
                var c = scb.Color;
                return 0.2126 * (c.R / 255.0)
                     + 0.7152 * (c.G / 255.0)
                     + 0.0722 * (c.B / 255.0);
            }
            return 0.5;
        }
    }
}