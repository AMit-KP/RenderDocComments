/* ═══════════════════════════════════════════════════════════════════════════════
 *  File:    BannerFactory.cs
 *  Purpose: Shared banner visual builders for every banner-family feature —
 *           comment banners (CommentBoxTagger) and region header banners
 *           (RegionBannerTagger). Hosts the banner style enums, the rail-style
 *           mapping, the font/colour resolution, and the trailing-line builders
 *           so the two banner looks cannot diverge.
 *
 *  When to Edit:
 *    • New trail treatment   — add a RailStyle case in BuildTrail + MapRail.
 *    • New banner consumer   — call ResolveStyle + BuildTrail; never re-implement.
 * ═══════════════════════════════════════════════════════════════════════════════ */
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using RenderDocComments.Options;

namespace RenderDocComments.DocCommentRenderer
{
    /// <summary>
    /// Shared builders for the banner visual language: style enums, rail mapping,
    /// font/colour resolution, and the trailing-line treatments (single/double
    /// hairline, picket fence, sparkle trail, wave).
    /// </summary>
    internal static class BannerFactory
    {
        /// <summary>How a banner presents itself, per the comment shape it came from.</summary>
        internal enum BannerStyle
        {
            /// <summary>Flanked single line: title + trailing rule.</summary>
            Heading,
            /// <summary>Bare fill rule: textless divider line.</summary>
            Divider,
            /// <summary>Ruled block: line above, title, line below.</summary>
            Sandwich,
            /// <summary>ASCII/unicode box: thin outlined box around the title.</summary>
            Box,
            /// <summary>One-line asterisk box: small pill badge.</summary>
            Badge,
        }

        /// <summary>Trailing-line treatment, driven by the leading rail character:
        /// ─ ━ ┄ ┈ - → single hairline; ═ = → double hairline; # → picket fence;
        /// * → sparkle trail; ~ → wavy line.</summary>
        internal enum RailStyle
        {
            Single,
            Double,
            Fence,
            Sparkle,
            Wave,
        }

        /// <summary>Maps a leading rail character to its trail treatment.</summary>
        internal static RailStyle MapRail(char c)
        {
            switch (c)
            {
                case '═':
                case '=':
                    return RailStyle.Double;
                case '#':
                    return RailStyle.Fence;
                case '*':
                    return RailStyle.Sparkle;
                case '~':
                    return RailStyle.Wave;
                default:
                    return RailStyle.Single;
            }
        }

        /// <summary>
        /// Resolves the banner font (banner override → comment box font → editor
        /// font), font size (banner override → editor size), and the text colour
        /// and rule colour — from the banner swatches, or from the theme's comment
        /// classification when <c>BannerUseThemeColors</c> is on.
        /// </summary>
        internal static void ResolveStyle(
            IWpfTextView view,
            out FontFamily fontFamily, out double fontSize,
            out Brush textBrush, out Color ruleColor)
        {
            var opts = RenderDocOptions.Instance;

            string famOverride = opts.EffectiveBannerFontFamily;
            fontFamily = null;
            if (string.IsNullOrEmpty(famOverride))
                famOverride = opts.EffectiveCommentBoxFontFamily;
            if (!string.IsNullOrEmpty(famOverride))
            {
                try { fontFamily = new FontFamily(famOverride); }
                catch { fontFamily = null; }
            }

            fontSize = opts.EffectiveBannerFontSize;
            Typeface editorTypeface = null;
            try
            {
                var tp = view.FormattedLineSource?.DefaultTextProperties;
                if (tp != null && tp.FontRenderingEmSize > 0)
                {
                    if (fontSize <= 0) fontSize = tp.FontRenderingEmSize;
                    editorTypeface = tp.Typeface;
                }
            }
            catch { }
            if (fontSize <= 0) fontSize = 13.0;
            if (fontFamily == null)
                fontFamily = editorTypeface?.FontFamily ?? new FontFamily("Consolas");

            if (opts.EffectiveBannerUseThemeColors)
            {
                var theme = ResolveThemeCommentBrush(view);
                textBrush = theme;
                ruleColor = SolidColorOf(theme, opts.EffectiveBannerRuleColor);
            }
            else
            {
                textBrush = Frozen(opts.EffectiveBannerTextColor);
                ruleColor = opts.EffectiveBannerRuleColor;
            }
        }

        /// <summary>Extracts a solid colour from a brush (theme brushes are
        /// normally solid; gradient brushes fall back to the given colour).</summary>
        private static Color SolidColorOf(Brush brush, Color fallback)
            => brush is SolidColorBrush scb ? scb.Color : fallback;

        /// <summary>Theme comment foreground brush from the view's format map,
        /// falling back to the default VS comment green.</summary>
        private static Brush ResolveThemeCommentBrush(IWpfTextView view)
        {
            try
            {
                var formatMap = view.Properties
                    .GetProperty<IEditorFormatMap>(typeof(IEditorFormatMap));
                if (formatMap != null)
                {
                    var props = formatMap.GetProperties("Comment");
                    if (props != null &&
                        props.Contains(EditorFormatDefinition.ForegroundBrushId) &&
                        props[EditorFormatDefinition.ForegroundBrushId] is Brush b)
                        return EnsureFrozen(b);
                }
            }
            catch { }
            return Frozen(Color.FromRgb(0x57, 0xA6, 0x4A));
        }

        private static Brush EnsureFrozen(Brush brush)
        {
            if (brush.IsFrozen) return brush;
            var clone = brush.Clone();
            clone.Freeze();
            return clone;
        }

        /// <summary>
        /// Builds the trailing-line element for a rail style: a fading hairline
        /// (single), two fading hairlines (double), a picket fence (vertical
        /// pickets joined by two inset rails), a sparkle trail (✦ glyphs fading
        /// out), or a wavy line (sine polyline fading out).
        /// <paramref name="fullWidth"/> stretches fence/wave/sparkle runs for
        /// full-width sandwich and divider lines.
        /// </summary>
        internal static FrameworkElement BuildTrail(RailStyle rail, Color color, double thickness, bool fullWidth)
        {
            double t = Math.Max(1.0, thickness);
            switch (rail)
            {
                case RailStyle.Double:
                {
                    // Two hairlines with a modest gap, centred in the row — a
                    // spacer of the full thickness read as one misaligned blob.
                    var lines = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                    double hair = Math.Max(1.0, Math.Min(2.0, t));
                    double gap = Math.Max(2.0, Math.Min(4.0, t));
                    for (int i = 0; i < 2; i++)
                    {
                        lines.Children.Add(new Border
                        {
                            Height = hair,
                            Background = FadeBrush(color, 0.55),
                        });
                        if (i == 0)
                            lines.Children.Add(new Border { Height = gap });
                    }
                    return lines;
                }

                case RailStyle.Fence:
                {
                    // Picket fence: full-height vertical pickets with two
                    // horizontal rails INSET at ~18%/82% — rails flush at the
                    // edges close the pickets into little boxes. Fades right.
                    double railH = Math.Max(1.0, Math.Min(2.0, t * 0.5));
                    double picketH = Math.Max(10.0, Math.Min(16.0, t * 2 + 8));
                    double inset = picketH * 0.18;

                    var pickets = new DrawingBrush
                    {
                        TileMode = TileMode.Tile,
                        Viewport = new Rect(0, 0, 9, picketH),
                        Viewbox = new Rect(0, 0, 9, picketH),
                        ViewportUnits = BrushMappingMode.Absolute,
                        ViewboxUnits = BrushMappingMode.Absolute,
                        Drawing = new GeometryDrawing
                        {
                            Brush = Frozen(Color.FromArgb(0xC0, color.R, color.G, color.B)),
                            Geometry = new RectangleGeometry(new Rect(0, 0, 2, picketH)),
                        },
                    };
                    pickets.Freeze();

                    var grid = new Grid { Height = picketH, VerticalAlignment = VerticalAlignment.Center, ClipToBounds = true };

                    var picketRect = new Rectangle { Fill = pickets };
                    grid.Children.Add(picketRect);

                    var topRail = new Border
                    {
                        Height = railH,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, inset, 0, 0),
                        Background = FadeBrush(color, 0.65),
                    };
                    grid.Children.Add(topRail);

                    var bottomRail = new Border
                    {
                        Height = railH,
                        VerticalAlignment = VerticalAlignment.Bottom,
                        Margin = new Thickness(0, 0, 0, inset),
                        Background = FadeBrush(color, 0.65),
                    };
                    grid.Children.Add(bottomRail);

                    grid.OpacityMask = FadeMask();
                    return grid;
                }

                case RailStyle.Sparkle:
                {
                    var trail = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    int count = fullWidth ? 24 : 8;
                    for (int i = 0; i < count; i++)
                    {
                        trail.Children.Add(new TextBlock
                        {
                            Text = "✦",
                            FontSize = 11,
                            Foreground = Frozen(color),
                            Opacity = Math.Max(0.05, 0.95 - i * (0.9 / count)),
                            Margin = new Thickness(0, 0, 7, 0),
                        });
                    }
                    return trail;
                }

                case RailStyle.Wave:
                {
                    // Sine line, 7px period, 2.5px amplitude. The polyline is
                    // REGENERATED at the trail's actual width on SizeChanged — a
                    // fixed run stops short on wide viewports (and must not be
                    // sampled coarsely: 3px sampling aliased the 6px period to a
                    // flat line).
                    var clip = new Grid { ClipToBounds = true, VerticalAlignment = VerticalAlignment.Center, Height = 8 };
                    Polyline wave = null;

                    clip.SizeChanged += (s, e) =>
                    {
                        double width = Math.Max(10.0, e.NewSize.Width);
                        var points = new PointCollection();
                        for (int x = 0; x <= width; x++)
                            points.Add(new Point(x, 4 - 2.5 * Math.Sin(2 * Math.PI * x / 7.0)));
                        points.Freeze();

                        if (wave != null)
                            clip.Children.Remove(wave);
                        wave = new Polyline
                        {
                            Points = points,
                            Stroke = FadeBrush(color, 0.75),
                            StrokeThickness = Math.Min(2.5, Math.Max(1.0, t)),
                        };
                        clip.Children.Add(wave);
                    };

                    return clip;
                }

                default:
                    return new Border { Height = t, Background = FadeBrush(color, 0.55), VerticalAlignment = VerticalAlignment.Center };
            }
        }

        /// <summary>Horizontal gradient from the colour at the given alpha to
        /// fully transparent.</summary>
        internal static Brush FadeBrush(Color color, double alpha)
        {
            var b = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb((byte)Math.Round(255 * alpha), color.R, color.G, color.B), 0.0),
                    new GradientStop(Color.FromArgb(0, color.R, color.G, color.B), 1.0),
                },
                new Point(0, 0), new Point(1, 0));
            b.Freeze();
            return b;
        }

        /// <summary>Horizontal opacity mask: opaque, then fading out.</summary>
        private static Brush FadeMask()
        {
            var b = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Colors.Black, 0.0),
                    new GradientStop(Colors.Black, 0.2),
                    new GradientStop(Colors.Transparent, 0.97),
                },
                new Point(0, 0), new Point(1, 0));
            b.Freeze();
            return b;
        }

        /// <summary>Creates a frozen solid-colour brush.</summary>
        internal static SolidColorBrush Frozen(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }
    }
}
