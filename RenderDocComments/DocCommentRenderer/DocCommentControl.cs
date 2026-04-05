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
    public class DocCommentControl : StackPanel
    {
        // ── All brushes injected — NO static fields ───────────────────────────────
        private readonly Brush _fg;
        private readonly Brush _fgDim;
        private readonly Brush _bg;
        private readonly Brush _summaryFg;
        private readonly Brush _linkBrush;
        private readonly Brush _codeBg;
        private readonly Brush _codeFg;
        private readonly Brush _paramNameBrush;
        private readonly Brush _sectionLabelBrush;
        private readonly Brush _gradientBarBrush;

        // ── Admonition brushes derived from theme ─────────────────────────────────
        private readonly Brush _noteBrush;
        private readonly Brush _warningBrush;
        private readonly Brush _deprecatedBrush;
        private readonly Brush _bugBrush;
        private readonly Brush _todoBrush;
        private readonly Brush _metaBrush;

        private readonly FontFamily _editorFont;
        private readonly FontFamily _monoFont;
        private readonly double _fontSize;
        private readonly double _contentMaxWidth;

        private const double SectionContentIndent = 12.0;

        // ── Constructor ───────────────────────────────────────────────────────────

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

        private void BuildInlines(string text, InlineCollection inlines, Brush fg)
            => BuildInlinesInto(text, inlines, fg);

        // ── Formatted span (bold / italic / underline / strike) with nested support ──
        //
        // Tokenises inner content recursively so combinations like <b><i>x</i></b> work.

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

        // Shared recursive inline builder — used by BuildInlines and BuildFormattedSpan.
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

        private enum SegKind
        {
            Text, InlineCode, Link, ParamRef, CodeBlock,
            Bold, Italic, Underline, Strike
        }

        private class Seg
        {
            public SegKind Kind;
            public string Value = string.Empty;
            public string Label = string.Empty;
            public string Href = string.Empty;
            public string Cref = string.Empty;
        }

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