using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace RenderDocComments.DocCommentRenderer
{
    public class DocCommentControl : StackPanel
    {
        private readonly Brush _fg;
        private readonly Brush _fgDim;
        private readonly Brush _bg;
        private readonly Brush _linkBrush;
        private readonly Brush _codeBg;
        private readonly Brush _codeFg;
        private readonly Brush _paramNameBrush;
        private readonly Brush _sectionLabelBrush;
        private readonly FontFamily _editorFont;   // Segoe UI — set by caller
        private readonly FontFamily _monoFont;
        private readonly double _fontSize;
        private readonly double _contentMaxWidth;

        private const double SectionContentIndent = 12.0;

        private static readonly Brush AccentBarBrush = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(255, 190, 110, 240), 0.0),
                new GradientStop(Color.FromArgb(200, 120,  70, 200), 0.4),
                new GradientStop(Color.FromArgb( 80,  80,  40, 150), 1.0),
            },
            new Point(0, 0), new Point(0, 1));

        public DocCommentControl(
            ParsedDocComment doc,
            Brush foreground,
            Brush background,
            Brush linkColor,
            FontFamily editorFont,
            double fontSize,
            double viewportWidth,
            double indentWidth)
        {
            _fg = foreground;
            _bg = background;
            _linkBrush = linkColor;
            _editorFont = editorFont;  // already Segoe UI from the tag
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

            _codeFg = new SolidColorBrush(isDark
                ? Color.FromRgb(206, 145, 120)
                : Color.FromRgb(160, 70, 0));

            _paramNameBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(156, 220, 254)
                : Color.FromRgb(0, 90, 170));

            _sectionLabelBrush = new SolidColorBrush(isDark
                ? Color.FromRgb(150, 150, 150)
                : Color.FromRgb(110, 110, 110));

            Background = background;
            Orientation = Orientation.Vertical;
            Margin = new Thickness(indentWidth, 2, 0, 6);

            Build(doc);
        }

        private void Build(ParsedDocComment doc)
        {
            var outerGrid = new Grid
            {
                MaxWidth = _contentMaxWidth,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
            outerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var bar = new Rectangle
            {
                Fill = AccentBarBrush,
                Width = 5,
                RadiusX = 2,
                RadiusY = 2,
                VerticalAlignment = VerticalAlignment.Stretch,
                Effect = new DropShadowEffect
                {
                    Color = Color.FromArgb(140, 180, 90, 240),
                    BlurRadius = 8,
                    ShadowDepth = 0,
                    Direction = 0
                }
            };
            Grid.SetColumn(bar, 0);
            outerGrid.Children.Add(bar);

            var content = new StackPanel { Orientation = Orientation.Vertical };
            Grid.SetColumn(content, 2);
            outerGrid.Children.Add(content);

            if (doc.InheritDoc != null)
            {
                var idText = string.IsNullOrEmpty(doc.InheritDoc.Cref)
                    ? "(inherits documentation)"
                    : $"(inherits documentation from {DocCommentParser.SimplifyCref(doc.InheritDoc.Cref)})";
                content.Children.Add(BuildRichBlock(idText, _fgDim, marginBottom: 4));
            }

            if (doc.Include != null)
                content.Children.Add(BuildRichBlock(
                    $"(documentation included from {doc.Include.File})", _fgDim, marginBottom: 4));

            if (!string.IsNullOrWhiteSpace(doc.Summary))
                content.Children.Add(BuildRichBlock(doc.Summary, _fg, marginBottom: 4));

            if (!string.IsNullOrWhiteSpace(doc.Remarks))
                content.Children.Add(BuildRichBlock(doc.Remarks, _fgDim, marginBottom: 4));

            if (!string.IsNullOrWhiteSpace(doc.Example))
            {
                content.Children.Add(BuildSectionLabel("Example:"));
                content.Children.Add(BuildCodeBlock(doc.Example, SectionContentIndent));
            }

            if (doc.Params.Count > 0 || doc.TypeParams.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Parameters:"));
                var grid = BuildParamGrid(SectionContentIndent);
                int row = 0;
                foreach (var tp in doc.TypeParams)
                    AddParamRow(grid, tp.Name, tp.Description, ref row);
                foreach (var p in doc.Params)
                    AddParamRow(grid, p.Name, p.Description, ref row);
                content.Children.Add(grid);
            }

            if (!string.IsNullOrWhiteSpace(doc.Returns))
            {
                content.Children.Add(BuildSectionLabel("Returns:"));
                content.Children.Add(BuildRichBlock(doc.Returns, _fg, marginLeft: SectionContentIndent));
            }

            if (doc.Exceptions.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Exceptions:"));
                var grid = BuildParamGrid(SectionContentIndent);
                int row = 0;
                foreach (var ex in doc.Exceptions)
                    AddParamRow(grid, ex.Type, ex.Description, ref row, ex.FullCref);
                content.Children.Add(grid);
            }

            if (!string.IsNullOrWhiteSpace(doc.Permission))
            {
                var permLabel = string.IsNullOrEmpty(doc.PermissionCref)
                    ? "Permission:"
                    : $"Permission ({doc.PermissionCref}):";
                content.Children.Add(BuildSectionLabel(permLabel));
                content.Children.Add(BuildRichBlock(doc.Permission, _fg, marginLeft: SectionContentIndent));
            }

            if (doc.CompletionList.Count > 0)
            {
                content.Children.Add(BuildSectionLabel("Completion List:"));
                foreach (var cl in doc.CompletionList)
                    content.Children.Add(BuildRichBlock(cl, _paramNameBrush, marginLeft: SectionContentIndent));
            }

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

            Children.Add(outerGrid);
        }

        // ── Section label ─────────────────────────────────────────────────────────

        private UIElement BuildSectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = _sectionLabelBrush,
                FontFamily = _editorFont,
                FontSize = _fontSize,
                Margin = new Thickness(0, 6, 0, 2),
                FontStyle = FontStyles.Italic
            };
        }

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

        // ── Inline builder ────────────────────────────────────────────────────────
        public async Task<TResult> ProcessAsync<TResult>(
            object request,
            CancellationToken cancellationToken,
            object options = null,
            string emptyParam = null)
        {
            throw new NotImplementedException();
        }
        private void BuildInlines(string text, InlineCollection inlines, Brush fg)
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
                        var codeContainer = new InlineUIContainer(new Border
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
                        };
                        inlines.Add(codeContainer);
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
                        inlines.Add(new InlineUIContainer(BuildCodeBlock(seg.Value)) { BaselineAlignment = BaselineAlignment.Center });
                        break;
                }
            }
        }

        // ── Link / cref builder ───────────────────────────────────────────────────

        private Inline BuildLink(string label, string cref, string href)
        {
            var hl = new Hyperlink(new Run(label))
            {
                Foreground = _linkBrush,
                TextDecorations = TextDecorations.Underline
            };

            if (!string.IsNullOrEmpty(href))
            {
                // External URL — open browser
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
                // #2 — cref navigation: copy symbol to clipboard then GoToDefinition
                // This is the most reliable approach in VS full IDE without complex APIs
                var symbolName = DocCommentParser.SimplifyCref(cref);
                hl.Click += (s, e) =>
                {
                    try
                    {
                        var dte = Microsoft.VisualStudio.Shell.Package
                            .GetGlobalService(typeof(EnvDTE.DTE)) as EnvDTE80.DTE2;
                        if (dte == null) return;

                        // Get the active text view and insert symbol temporarily
                        var doc = dte.ActiveDocument;
                        var sel = doc?.Selection as EnvDTE.TextSelection;
                        if (sel == null) return;

                        // Save current position
                        var savedLine = sel.ActivePoint.Line;
                        var savedColumn = sel.ActivePoint.DisplayColumn;

                        // Find an occurrence of the symbol name in the file and GoToDefinition on it
                        // Fallback: use the Find/Navigate To box
                        var find = dte.Find;
                        find.FindWhat = symbolName;
                        find.MatchCase = true;
                        find.MatchWholeWord = true;
                        find.Target = EnvDTE.vsFindTarget.vsFindTargetCurrentDocument;
                        find.Action = EnvDTE.vsFindAction.vsFindActionFind;
                        var result = find.Execute();

                        if (result == EnvDTE.vsFindResult.vsFindResultFound)
                        {
                            // Symbol found in file — GoToDefinition on it
                            dte.ExecuteCommand("Edit.GoToDefinition");
                            // Restore — GoToDefinition may have opened a new file, that's fine
                        }
                        else
                        {
                            // Not in current file — open Navigate To dialog pre-filled
                            // Ctrl+, / Edit.NavigateTo
                            dte.ExecuteCommand("Edit.NavigateTo");
                        }
                    }
                    catch { }
                };
            }

            return hl;
        }

        // ── Code block ────────────────────────────────────────────────────────────

        private UIElement BuildCodeBlock(string code, double marginLeft = 0)
        {
            return new Border
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
        }

        // ── Param grid ────────────────────────────────────────────────────────────

        private Grid BuildParamGrid(double marginLeft = 0)
        {
            var grid = new Grid { Margin = new Thickness(marginLeft, 0, 0, 2) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            return grid;
        }

        private void AddParamRow(Grid grid, string name, string description,
            ref int row, string navCref = null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            if (!string.IsNullOrEmpty(navCref))
            {
                var tb = new TextBlock
                {
                    Margin = new Thickness(0, 1, 0, 1),
                    VerticalAlignment = VerticalAlignment.Top
                };
                tb.Inlines.Add(BuildLink(name, navCref, string.Empty));
                Grid.SetRow(tb, row); Grid.SetColumn(tb, 0);
                grid.Children.Add(tb);
            }
            else
            {
                var nameTb = new TextBlock
                {
                    Text = name,
                    Foreground = _paramNameBrush,
                    FontFamily = _monoFont,
                    FontSize = _fontSize - 1,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                Grid.SetRow(nameTb, row); Grid.SetColumn(nameTb, 0);
                grid.Children.Add(nameTb);
            }

            if (!string.IsNullOrWhiteSpace(description))
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
                Grid.SetRow(dashTb, row); Grid.SetColumn(dashTb, 1);
                grid.Children.Add(dashTb);

                var descTb = new TextBlock
                {
                    FontFamily = _editorFont,
                    FontSize = _fontSize,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(0, 1, 0, 1)
                };
                BuildInlines(description, descTb.Inlines, _fg);
                Grid.SetRow(descTb, row); Grid.SetColumn(descTb, 2);
                grid.Children.Add(descTb);
            }

            row++;
        }

        // ── Tokeniser ─────────────────────────────────────────────────────────────

        private enum SegKind
        {
            Text, InlineCode, Link, ParamRef, CodeBlock
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
            @"|\[CODE\](?<codeblock>[\s\S]*?)\[/CODE\]",
            RegexOptions.Compiled);

        private List<Seg> Tokenise(string input)
        {
            var result = new List<Seg>();
            int lastIndex = 0;

            foreach (Match m in TokenRegex.Matches(input))
            {
                if (m.Index > lastIndex)
                    result.Add(new Seg { Kind = SegKind.Text, Value = input.Substring(lastIndex, m.Index - lastIndex) });

                if (m.Groups["code"].Success)
                    result.Add(new Seg { Kind = SegKind.InlineCode, Value = m.Groups["code"].Value });
                else if (m.Groups["hlabel"].Success)
                    result.Add(new Seg { Kind = SegKind.Link, Label = m.Groups["hlabel"].Value, Value = m.Groups["hlabel"].Value, Href = m.Groups["href"].Value });
                else if (m.Groups["clabel"].Success)
                    result.Add(new Seg { Kind = SegKind.Link, Label = m.Groups["clabel"].Value, Value = m.Groups["clabel"].Value, Cref = m.Groups["cref"].Value });
                else if (m.Groups["paramref"].Success)
                    result.Add(new Seg { Kind = SegKind.ParamRef, Value = m.Groups["paramref"].Value });
                else if (m.Groups["codeblock"].Success)
                    result.Add(new Seg { Kind = SegKind.CodeBlock, Value = m.Groups["codeblock"].Value });

                lastIndex = m.Index + m.Length;
            }

            if (lastIndex < input.Length)
                result.Add(new Seg { Kind = SegKind.Text, Value = input.Substring(lastIndex) });

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