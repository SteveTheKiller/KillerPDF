using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Docnet.Core;
using Docnet.Core.Models;
using Microsoft.Win32;
using PdfSharpCore.Drawing;
using PdfSharpCore.Pdf;
using PdfSharpCore.Pdf.IO;
using KillerPDF.Services;
using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF
{
    public partial class MainWindow
    {
        // ============================================================
        // Draw/Highlight settings bar
        // ============================================================

        // The quick-colors shown in the annotate bars. User-configurable via the color picker's swatch
        // row (shared "UserSwatches" setting); seeded with these 8 defaults, restorable via the picker's
        // Reset. SwatchColors reads the live set each time a bar is built, so edits show up immediately.
        private static readonly Color[] DefaultSwatchColors = UiKit.DefaultSwatches;
        private static Color[] SwatchColors => LoadUserSwatches();
        private static Color[] LoadUserSwatches()
        {
            var raw = App.GetSetting("UserSwatches");
            if (string.IsNullOrWhiteSpace(raw)) return [.. DefaultSwatchColors];
            List<Color> list = [];
            foreach (var part in raw!.Split(','))
            {
                var t = part.Trim().TrimStart('#');
                if (t.Length == 6 && int.TryParse(t, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out int v))
                    list.Add(Color.FromRgb((byte)((v >> 16) & 0xFF), (byte)((v >> 8) & 0xFF), (byte)(v & 0xFF)));
            }
            return list.Count > 0 ? [.. list] : [.. DefaultSwatchColors];
        }

        // Frozen cached brushes for hot-path UI construction
        private static readonly SolidColorBrush _swatchDimBorder = Freeze(new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)));
        private static readonly SolidColorBrush _drawBarBackground = Freeze(new SolidColorBrush(Color.FromRgb(0x1a, 0x1a, 0x1a)));
        private static readonly SolidColorBrush _thumbBorderBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)));

        private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // Wraps a floating annotation bar's content with the app's film-grain layer so these bars
        // carry the same texture as the Settings / signature / dialog surfaces. The grain extends
        // under the host border's 4px padding (negative margin) and matches its bottom corners.
        private Grid GrainWrap(UIElement content)
        {
            var g = new Grid();
            g.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Margin = new Thickness(-4),
                IsHitTestVisible = false,
                Opacity = (double)FindResource("GrainOpacity"),
                Background = (System.Windows.Media.Brush)FindResource("GrainBrushShared")
            });
            g.Children.Add(content);
            return g;
        }

        // A grab handle (vertical dots) placed at the left of an annotation bar so it can be slid
        // left/right along the top of the document. Returns the handle for EnableBarSlide.
        private Border MakeBarGrip(int dotCount = 3)
        {
            // Real ellipse dots (not a braille glyph, which didn't render on some fonts/themes - the
            // grip looked empty on the Light bars). Matches the sidebar splitter / minimized-bar dots.
            // dotCount scales with bar height: 3 for single-row bars, 4 for the double-height text bar.
            var dots = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
            var fill = (Brush)FindResource("MutedTextBrush");
            for (int i = 0; i < dotCount; i++)
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                { Width = 3, Height = 3, Margin = new Thickness(0, 1.5, 0, 1.5), Fill = fill });
            return new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Padding = new Thickness(1, 0, 10, 0),   // hard to the left edge, more gap before the labels
                VerticalAlignment = VerticalAlignment.Stretch,
                Child = dots
            };
        }

        // Wraps an annotate bar's content with a film-grain layer (always visible, even minimized) and a
        // hidden grip-dots strip (the same dots the sidebar splitter uses) revealed when minimized.
        private FrameworkElement BuildBarHost(FrameworkElement content)
        {
            var host = new Grid();

            // Grain stays put when the controls collapse, so the minimized strip keeps the texture.
            host.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Margin = new Thickness(-4),
                IsHitTestVisible = false,
                Opacity = (double)FindResource("GrainOpacity"),
                Background = (Brush)FindResource("GrainBrushShared")
            });

            host.Children.Add(content);   // the collapsible controls

            var dots = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false
            };
            var fill = TryFindResource("MutedTextBrush") as Brush ?? Brushes.Gray;   // match the sidebar handle dots
            for (int i = 0; i < 6; i++)
                dots.Children.Add(new System.Windows.Shapes.Ellipse
                { Width = 3, Height = 3, Margin = new Thickness(2, 0, 2, 0), Fill = fill });
            // The dots live in a transparent, hit-testable strip that fills the bar. While the bar is
            // minimized this strip is shown, so the whole peek strip can be dragged left/right (the slide
            // is wired to it in PlaceAnnotationBar) - same as dragging the grip on the expanded bar.
            var dotsStrip = new Border
            {
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Visibility = Visibility.Collapsed,
                Child = dots
            };
            host.Children.Add(dotsStrip);

            _annotBarContent = content;
            _annotBarDots = dotsStrip;
            return host;
        }

        // Drop shadow for the annotate bars: offset straight down with depth >= blur, so it falls on the
        // sides and bottom but never above the bar (no halo between it and the toolbar). Removed entirely
        // while minimized.
        private static System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow()
            => new() { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 3, Direction = 270, Opacity = 0.38 };

        // Lets the annotation bars slide horizontally along the top via their grip, clamped inside
        // the document area, with the X position remembered (shared across the draw/text bars).
        private void EnableBarSlide(FrameworkElement grip, Border bar, FrameworkElement bounds, bool backgroundOnly = false)
        {
            grip.MouseLeftButtonDown += (s, e) =>
            {
                // When wired on a content panel, only act on clicks that hit the panel's OWN background
                // (empty gaps). A click on a child control (slider, swatch, combo) reports that child as
                // the source, so we bail and let the control handle it - even controls that don't mark the
                // event handled never start a drag. The grip / peek strip pass backgroundOnly=false.
                if (backgroundOnly && !ReferenceEquals(e.OriginalSource, grip)) return;
                // Double-click any draggable surface (grip, peek strip, or an empty area of the bar)
                // toggles minimize - same gesture everywhere.
                if (e.ClickCount == 2) { e.Handled = true; ToggleAnnotBarMinimized(); return; }
                double w = bar.ActualWidth;
                // Drag uniformly in left-edge coordinates whatever the current anchor; the edge it
                // anchors to is decided on release from where it ends up.
                double curLeft = bar.HorizontalAlignment == HorizontalAlignment.Right
                    ? bounds.ActualWidth - bar.Margin.Right - w
                    : bar.Margin.Left;
                bar.HorizontalAlignment = HorizontalAlignment.Left;
                bar.Margin = new Thickness(curLeft, bar.Margin.Top, 0, 0);
                bar.Tag = (e.GetPosition(bounds).X, curLeft);   // (startX, origLeft)
                grip.CaptureMouse();
                e.Handled = true;
            };
            grip.MouseMove += (s, e) =>
            {
                if (bar.Tag is not (double startX, double origLeft) || !grip.IsMouseCaptured) return;
                double w = bar.ActualWidth;
                // Stop the drag at the scrollbar's left edge (not the pane edge) so the bar never
                // overshoots the scrollbar and then snaps back on release - the "bounce" the user saw.
                double sb = VerticalScrollBarInset();
                double maxLeft = Math.Max(0, bounds.ActualWidth - w - sb);
                double nl = Math.Max(0, Math.Min(maxLeft, origLeft + (e.GetPosition(bounds).X - startX)));
                bar.Margin = new Thickness(nl, bar.Margin.Top, 0, 0);
                // Merge the docked-side border with the pane border live while dragging (no footprint
                // change), so it doesn't pop in on release. The right side only docks flush when no
                // scrollbar sits between the bar and the pane edge.
                SetBarDockedBorder(bar, dockedLeft: nl <= 0.5, dockedRight: sb <= 0 && nl >= maxLeft - 0.5);
            };
            grip.MouseLeftButtonUp += (s, e) =>
            {
                if (!grip.IsMouseCaptured) return;
                grip.ReleaseMouseCapture();
                double w = bar.ActualWidth;
                double left = bar.Margin.Left;
                // Measure the right gap from the scrollbar's left edge (the usable content edge), so a
                // bar parked against the scrollbar records gap ~0 and PositionAnnotationBar re-adds the
                // scrollbar width once - no double inset, no jump.
                double sb = VerticalScrollBarInset();
                double rightGap = Math.Max(0, (bounds.ActualWidth - sb) - (left + w));
                const double snap = 24;   // within this many px of an edge, cling to that edge exactly
                if (left <= snap)
                {
                    _annotBarCenterFrac = null; _annotBarAnchorRight = false; _annotBarGap = Math.Max(0, left);
                }
                else if (rightGap <= snap)
                {
                    _annotBarCenterFrac = null; _annotBarAnchorRight = true; _annotBarGap = rightGap;
                }
                else
                {
                    // Away from both edges: remember it as a fraction of the width so resizing scales it
                    // smoothly rather than snapping it to an edge.
                    _annotBarCenterFrac = (left + w / 2) / bounds.ActualWidth;
                }
                App.SetSetting("AnnotBarFrac",
                    _annotBarCenterFrac?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                App.SetSetting("AnnotBarGap", ((int)(_annotBarGap ?? 8)).ToString());
                App.SetSetting("AnnotBarRightSide", _annotBarAnchorRight ? "1" : "0");
                if (PagePreviewPanel?.Parent is Grid area) PositionAnnotationBar(bar, area);
                e.Handled = true;
            };
        }

        // Saved horizontal placement for the floating draw/text settings bars (shared by both). The bar
        // anchors to whichever edge it sits nearer and remembers its gap from that edge, so it clings to
        // that edge on resize. RepositionAnnotationBars re-applies it - clamped fully inside the document
        // area - from the same window events that keep the Settings panel in-window, so it can never end
        // up off-screen regardless of which edge it was parked against.
        private double? _annotBarGap;
        private bool _annotBarAnchorRight = true;
        private double? _annotBarCenterFrac;   // set when parked away from both edges: hold this fraction of the width
        private bool _vScrollVisible;          // last-known document vertical scrollbar state, to reposition bars on change
        private EditTool? _annotBarTool;       // which tool the visible annotate bar is for (so we fade only on real switches)
        private bool _annotBarMinimized;       // annotate bar collapsed to a peek strip (toggled by re-clicking its tool)
        private double _annotBarFullHeight;    // remembered full height to expand back to
        private FrameworkElement? _annotBarContent;   // the bar's normal content (hidden while minimized)
        private FrameworkElement? _annotBarDots;      // grip-dots strip shown while minimized
        private readonly List<FrameworkElement> _annotBarDragInners = [];   // nested panels whose empty areas also drag the bar

        // Positions an annotation bar and wires up sliding. If we already know the X (this session or
        // saved), set it synchronously so the bar appears in place; only the very first time do we
        // defer to compute the default top-right from the laid-out width.
        private void PlaceAnnotationBar(Border bar, Border grip, bool fadeIn = false)
        {
            if (PagePreviewPanel.Parent is not Grid area) return;

            if (_annotBarGap is null && _annotBarCenterFrac is null)
            {
                if (double.TryParse(App.GetSetting("AnnotBarFrac"), System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out double f))
                {
                    _annotBarCenterFrac = f;   // parked away from both edges last time
                }
                else
                {
                    _annotBarGap = int.TryParse(App.GetSetting("AnnotBarGap"), out int sg) ? sg : 8;
                    _annotBarAnchorRight = App.GetSetting("AnnotBarRightSide") != "0";   // default: right edge
                }
            }
            EnableBarSlide(grip, bar, area);
            // The minimized peek strip drags the bar too, so a collapsed bar can be repositioned.
            if (_annotBarDots is not null) EnableBarSlide(_annotBarDots, bar, area);
            // Empty areas of the bar content drag it too (and double-click them to minimize). The content
            // panel(s) have a Transparent background, so only the gaps between controls trigger this -
            // clicks that land on a slider/swatch/combo still go to that control.
            if (_annotBarContent is not null) EnableBarSlide(_annotBarContent, bar, area, backgroundOnly: true);
            foreach (var inner in _annotBarDragInners)
                if (inner is not null) EnableBarSlide(inner, bar, area, backgroundOnly: true);
            // A freshly built bar has no measured width yet, so PositionAnnotationBar can't place it until
            // layout runs. Hide it for that one frame (Opacity 0 still lays out, so width measures), then
            // anchor and reveal it - otherwise it renders at its default right edge first and visibly
            // jumps to its saved spot on every tool switch.
            bar.Opacity = 0;
            PositionAnnotationBar(bar, area);   // sync position (edge-anchored modes are correct without a width)
            if (fadeIn)
            {
                // Start the fade-in immediately so it overlaps the outgoing bar's fade-out (a true
                // crossfade). Waiting for the deferred layout pass left a frame where neither bar was
                // visible, which read as a blink. Final clamp/centre still happens once laid out.
                bar.BeginAnimation(UIElement.OpacityProperty,
                    new DoubleAnimation(0, 1, new Duration(TimeSpan.FromMilliseconds(110)))
                    { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => PositionAnnotationBar(bar, area)));
            }
            else if (_annotBarCenterFrac is not null)
            {
                // Centre-parked needs a measured width to place, so stay hidden one layout frame so it
                // can't render at the default edge first and then jump.
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                    (Action)(() => { PositionAnnotationBar(bar, area); bar.Opacity = 1; }));
            }
            else
            {
                // Edge-anchored: already placed correctly above (no width needed), so reveal it right away
                // instead of hiding for a frame - that one hidden frame was the "blink" on a same-tool
                // refresh / same-family tool switch.
                bar.Opacity = 1;
            }
        }

        // Subtracts the annotate-bar chrome (padding + border + a gap from the scrollbar) from the document
        // area's width, giving the wrap panel the width it's actually allowed to occupy before wrapping.
        private static readonly InsetWidthConverter _barWidthInset = new();
        private sealed class InsetWidthConverter : System.Windows.Data.IValueConverter
        {
            public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c)
                => value is double d ? Math.Max(0.0, d - 28.0) : value;
            public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => value;
        }

        // Adapts a wrapping annotate bar to very narrow widths: once its content blocks can no longer sit on
        // a single row, the drag grip is hidden (non-essential at that size) and the blocks gain a little
        // vertical breathing room as they stack; the inline separators are hidden so they don't float as
        // stray ticks between stacked rows. The single-row fit test deliberately uses only fixed quantities
        // (block widths + a constant gap allowance, never the grip or separator visibility we toggle), so a
        // toggle can never change the test result and oscillate.
        private void WireBarWrapAdaptation(WrapPanel host, FrameworkElement grip, FrameworkElement primary,
                                           FrameworkElement sizeSource)
        {
            // The grip only shrinks to a thin draggable nub once the grip + the first block can no longer
            // share a row (the window's skinniest) - not merely when the bar wraps. The threshold is the
            // grip + first-block widths, measured ONCE at full size and frozen, so shrinking the grip can
            // never feed back into the decision and oscillate. Spacing between groups is handled by the
            // groups' own margins (not separators), so it survives wrapping with no stray ticks.
            double gripThreshold = 0;
            Thickness gripFull = (grip as Border)?.Padding ?? new Thickness();
            bool? lastGripMin = null;
            void SetGripMinimized(bool min)
            {
                if (grip is not Border gb) return;
                if (gb.Child is UIElement dots) dots.Visibility = min ? Visibility.Collapsed : Visibility.Visible;
                gb.Padding = min ? new Thickness(1, 0, 2, 0) : gripFull;   // keep a few draggable px
            }
            void Apply()
            {
                double avail = host.MaxWidth;
                if (double.IsNaN(avail) || double.IsInfinity(avail) || avail <= 0) return;
                if (gripThreshold <= 0)
                {
                    double g = grip.DesiredSize.Width, p = primary.DesiredSize.Width;
                    if (g <= 0 || p <= 0) return;   // not measured yet; a later pass will retry
                    gripThreshold = g + p + 8.0;
                }
                bool gripMin = avail + 0.5 < gripThreshold;
                if (lastGripMin == gripMin) return;
                lastGripMin = gripMin;
                SetGripMinimized(gripMin);
            }
            // Drive off the document area's width (the monotonic signal) rather than the panel's own size:
            // once the grip shrinks and the bar fits a row again, the panel stops resizing, so only the
            // source's width change tells us there is room to restore the grip. Unhook on teardown - the bar
            // is rebuilt often (every swatch click), so a lingering handler would leak.
            void onSource(object? sender, SizeChangedEventArgs e) => Apply();
            sizeSource.SizeChanged += onSource;
            host.Unloaded += (_, _) => sizeSource.SizeChanged -= onSource;
            host.SizeChanged += (_, _) => Apply();   // catches the first valid measurement
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, (Action)Apply);
        }

        private void ShowDrawSettings(EditTool tool)
        {
            // Fade the bar in only when it's genuinely appearing (no bar yet, or coming from the text
            // bar). Switching between tools that share this same draw bar (Highlight / Underline /
            // Strikethrough / Draw) swaps it in place instantly - otherwise the two near-identical bars
            // crossfade through ~50% opacity and read as a blink even though nothing visually changed.
            bool prevWasDrawBar = _annotBarTool is EditTool.Draw or EditTool.Highlight
                                                or EditTool.Underline or EditTool.Strikethrough or EditTool.Line
                                                or EditTool.Shape;
            bool appearing = _annotBarTool != tool && !prevWasDrawBar;
            if (_drawSettingsBar is not null)
            {
                // On a switch, fade the old bar out (crossfades with the new one); on a refresh, swap it
                // out instantly so clicking a swatch doesn't flicker the whole bar.
                if (appearing) FadeOutAndRemoveBar(_drawSettingsBar);
                else (PagePreviewPanel.Parent as Grid)?.Children.Remove(_drawSettingsBar);
                _drawSettingsBar = null;
            }

            // WrapPanel so that on a too-narrow window the control groups drop to a second/third row
            // instead of overflowing. Its MaxWidth is pinned to the document area below so it knows when
            // to wrap; controls are added in self-contained groups so a wrap never splits a label from
            // its slider.
            var panel = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 2, 8, 2), Background = Brushes.Transparent };

            // Drag grip so the bar can be slid left/right along the top.
            var drawGrip = MakeBarGrip();
            panel.Children.Add(drawGrip);

            // A small checkbox + label for the bar (Level on the Line tool, Eraser on Highlight / Draw).
            // Toggling rebuilds the bar to reflect the new state.
            StackPanel BarCheck(string label, bool active, string tip, Action onClick)
            {
                var p = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 18, 0),
                    Cursor = Cursors.Hand,
                    ToolTip = tip
                };
                var box = new Border
                {
                    Width = 15,
                    Height = 15,
                    CornerRadius = new CornerRadius(3),
                    BorderThickness = new Thickness(active ? 0 : 1),
                    Background = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                };
                if (active)
                {
                    // Live theme reference (not a snapshot) so the checked fill tracks the current theme's
                    // accent - the old AccentBrush() snapshot kept whatever accent was active when the bar
                    // was first built, so it showed the wrong (often green) color after a theme switch.
                    box.SetResourceReference(Border.BackgroundProperty, "SelectionAccent");
                    box.Child = new TextBlock
                    {
                        Text = "✓",
                        Foreground = Brushes.White,
                        FontSize = 10,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                }
                else box.BorderBrush = _swatchDimBorder;
                var lbl = new TextBlock
                {
                    Text = label,
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center
                };
                lbl.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                p.Children.Add(box);
                p.Children.Add(lbl);
                p.MouseLeftButtonDown += (_, _) => onClick();
                return p;
            }
            // Built here, added at the END of the bar (after Opacity) where there's more room than
            // squeezed in before the color swatches.
            StackPanel? barCheck = tool switch
            {
                EditTool.Line => BarCheck(Loc("Str_Bar_Level"), _lineLevel,
                    "Keep the line on the nearest axis (horizontal or vertical)",
                    () => { _lineLevel = !_lineLevel; ShowDrawSettings(tool); }),
                EditTool.Highlight => BarCheck(Loc("Str_Bar_Eraser"), _highlightErase,
                    "Drag a box to delete every annotation inside it",
                    () => { _highlightErase = !_highlightErase; ShowDrawSettings(tool); }),
                EditTool.Draw => BarCheck(Loc("Str_Bar_Eraser"), _drawErase,
                    "Brush over annotations to delete them",
                    () => { _drawErase = !_drawErase; ShowDrawSettings(tool); }),
                EditTool.Shape => ShapeKindPicker(),
                _ => null
            };

            // Shapes tool (#127 Phase 3): radio-style buttons whose glyphs are literal mini shapes
            // (drawn WPF elements, not font glyphs, so they render on every locale/font setup),
            // plus the Fill toggle. Clicking a shape switches the sub-mode and rebuilds the bar.
            StackPanel ShapeKindPicker()
            {
                Border KindBtn(ShapeKind kind, Shape glyph, string tip)
                {
                    bool active = _shapeKind == kind;
                    glyph.StrokeThickness = 1.5;
                    glyph.Fill = Brushes.Transparent;
                    glyph.HorizontalAlignment = HorizontalAlignment.Center;
                    glyph.VerticalAlignment = VerticalAlignment.Center;
                    glyph.SetResourceReference(Shape.StrokeProperty, active ? "SelectionAccent" : "MutedTextBrush");
                    var b = new Border
                    {
                        Width = 26,
                        Height = 20,
                        CornerRadius = new CornerRadius(3),
                        BorderThickness = new Thickness(active ? 1.5 : 1),
                        Background = Brushes.Transparent,
                        Margin = new Thickness(1, 0, 1, 0),
                        Cursor = Cursors.Hand,
                        ToolTip = tip,
                        Child = glyph
                    };
                    if (active) b.SetResourceReference(Border.BorderBrushProperty, "SelectionAccent");
                    else b.BorderBrush = _swatchDimBorder;
                    b.MouseLeftButtonDown += (_, _) =>
                    {
                        if (_shapeKind == kind) return;
                        _shapeKind = kind;
                        if (kind != ShapeKind.Polygon) CancelShapePolygon();
                        ShowDrawSettings(tool);
                    };
                    return b;
                }

                var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 0, 0) };
                row.Children.Add(KindBtn(ShapeKind.Rectangle, new Rectangle { Width = 13, Height = 9 },
                    "Box - drag a rectangle"));
                row.Children.Add(KindBtn(ShapeKind.Ellipse, new Ellipse { Width = 13, Height = 9 },
                    "Ellipse - drag an oval"));
                var pent = new Polygon
                {
                    Width = 13,
                    Height = 11,
                    Points = [new Point(6.5, 0), new Point(13, 4.5), new Point(10.5, 11), new Point(2.5, 11), new Point(0, 4.5)]
                };
                row.Children.Add(KindBtn(ShapeKind.Polygon, pent,
                    "Freeform - click points; click the first point or double-click to close, Esc cancels, Backspace removes the last point"));

                var fillCheck = BarCheck(Loc("Str_Bar_ShapeFill"), _shapeFill,
                    "Fill the inside of the shape",
                    () => { _shapeFill = !_shapeFill; ShowDrawSettings(tool); });
                fillCheck.Margin = new Thickness(10, 0, 18, 0);
                row.Children.Add(fillCheck);
                return row;
            }

            // Color group (label + swatches + more) - one wrap unit.
            var colorGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
            var colorLbl = new TextBlock
            {
                Text = Loc("Str_Bar_Color"),
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            colorLbl.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            colorGroup.Children.Add(colorLbl);

            // Color swatches
            bool isLineTool = tool == EditTool.Strikethrough || tool == EditTool.Underline;
            var activeColor = (tool is EditTool.Draw or EditTool.Line or EditTool.Shape) ? _drawColor
                            : isLineTool ? Color.FromRgb(_lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B)
                            : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            foreach (var color in SwatchColors)
            {
                bool isActive = color == activeColor;
                var swatch = new Border
                {
                    Width = 18,
                    Height = 18,
                    Background = Freeze(new SolidColorBrush(color)),
                    BorderThickness = new Thickness(isActive ? 2 : 1),
                    CornerRadius = new CornerRadius(3),
                    Margin = new Thickness(1),
                    Cursor = Cursors.Hand,
                    Tag = color
                };
                if (isActive)
                    swatch.SetResourceReference(Border.BorderBrushProperty, "PrimaryBrush");
                else
                    swatch.BorderBrush = _swatchDimBorder;
                swatch.MouseLeftButtonDown += (s, e) =>
                {
                    var c = (Color)((Border)s!).Tag;
                    if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape))
                        _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                    else if (isLineTool)
                        _lineAnnotColor = Color.FromArgb(_lineAnnotColor.A, c.R, c.G, c.B);
                    else
                        _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                    ApplyDrawStyleToSelection();   // edit the selected annotation, if any
                    ShowDrawSettings(tool); // refresh selection
                };
                colorGroup.Children.Add(swatch);
            }

            // "More colors..." -> full RGB picker, applied to whichever draw color this bar drives.
            var moreDraw = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(1),
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(1),
                ToolTip = Loc("Str_Bar_MoreColors"),
                BorderBrush = _swatchDimBorder,
                Background = new LinearGradientBrush
                {
                    StartPoint = new Point(0, 0),
                    EndPoint = new Point(1, 1),
                    GradientStops =
                    {
                        new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                        new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                        new GradientStop(Colors.Blue, 1)
                    }
                }
            };
            moreDraw.MouseLeftButtonDown += (_, _) => OpenColorPicker(activeColor, c =>
            {
                if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape)) _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                else if (isLineTool) _lineAnnotColor = Color.FromArgb(_lineAnnotColor.A, c.R, c.G, c.B);
                else _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                ApplyDrawStyleToSelection();
                ShowDrawSettings(tool);
            }, () => ShowDrawSettings(tool));
            colorGroup.Children.Add(moreDraw);
            panel.Children.Add(colorGroup);

            // Left-to-right collapse: colorGroup stays on row 1 with the grip; Size and the Opacity+toggle
            // unit live in a nested wrap panel that drops below colorGroup first, then splits Size / Opacity.
            // Opacity and the Level/Eraser toggle share one unit so the toggle never lands on a row by itself.
            var dRest = new WrapPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };
            var dOpacityUnit = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            _annotBarDragInners.Clear();
            _annotBarDragInners.Add(dRest);

            // Size slider (draw only)
            if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape))
            {
                var sizeGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
                var sizeLbl = new TextBlock
                {
                    Text = Loc("Str_Bar_Size"),
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 6, 0)
                };
                sizeLbl.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                sizeGroup.Children.Add(sizeLbl);

                var sizeSlider = new Slider
                {
                    Minimum = 1,
                    Maximum = 60,
                    Value = _drawWidth,
                    Width = 90,
                    VerticalAlignment = VerticalAlignment.Center,
                    TickFrequency = 1,
                    IsSnapToTickEnabled = true,
                    Style = (Style)FindResource("DarkSlider")
                };
                sizeSlider.ValueChanged += (s, e) => { _drawWidth = e.NewValue; ApplyDrawStyleToSelection(); };
                sizeGroup.Children.Add(sizeSlider);

                var sizeLabel = new TextBlock
                {
                    Text = $"{_drawWidth:F0}px",
                    FontFamily = UiKit.UiFont,
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 0, 0),
                    Width = 34,
                    TextAlignment = TextAlignment.Right
                };
                sizeLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
                sizeSlider.ValueChanged += (s, e) => sizeLabel.Text = $"{e.NewValue:F0}px";
                sizeGroup.Children.Add(sizeLabel);
                dRest.Children.Add(sizeGroup);
            }

            // Opacity group (label + slider + value) - one wrap unit.
            var opacityGroup = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 16, 2) };
            var opacityLbl = new TextBlock
            {
                Text = Loc("Str_Bar_Opacity"),
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0)
            };
            opacityLbl.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            opacityGroup.Children.Add(opacityLbl);

            byte currentOpacity = (tool is EditTool.Draw or EditTool.Line or EditTool.Shape) ? _drawOpacity : isLineTool ? _lineAnnotColor.A : _highlightColor.A;
            var opacitySlider = new Slider
            {
                Minimum = 10,
                Maximum = 255,
                Value = currentOpacity,
                Width = 90,
                VerticalAlignment = VerticalAlignment.Center,
                Style = (Style)FindResource("DarkSlider")
            };
            var opacityLabel = new TextBlock
            {
                Text = $"{(int)(currentOpacity / 255.0 * 100)}%",
                FontFamily = UiKit.UiFont,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0),
                Width = 40,
                TextAlignment = TextAlignment.Right
            };
            opacityLabel.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            opacitySlider.ValueChanged += (s, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityLabel.Text = $"{(int)(a / 255.0 * 100)}%";
                if ((tool is EditTool.Draw or EditTool.Line or EditTool.Shape))
                {
                    _drawOpacity = a;
                    _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B);
                }
                else if (isLineTool)
                {
                    _lineAnnotColor = Color.FromArgb(a, _lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B);
                }
                else
                {
                    _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
                }
                ApplyDrawStyleToSelection();   // edit the selected annotation, if any
            };
            opacityGroup.Children.Add(opacitySlider);
            opacityGroup.Children.Add(opacityLabel);
            dOpacityUnit.Children.Add(opacityGroup);

            // Level (Line) / Eraser (Highlight, Draw) toggle rides at the end of the Opacity unit, so it
            // stays beside Opacity as the bar collapses instead of dropping onto a row by itself.
            if (barCheck is not null)
            {
                barCheck.Margin = new Thickness(0, 2, 2, 2);
                dOpacityUnit.Children.Add(barCheck);
            }
            dRest.Children.Add(dOpacityUnit);
            panel.Children.Add(dRest);

            _drawSettingsBar = new Border
            {
                BorderThickness = new Thickness(1, 0, 1, 1),   // no top border - the toolbar above already separates
                HorizontalAlignment = HorizontalAlignment.Right,  // right-anchored; slid via the grip
                VerticalAlignment = VerticalAlignment.Top,
                CornerRadius = new CornerRadius(0, 0, 4, 4),
                Padding = new Thickness(4),
                Effect = AnnotBarShadow(),
                Child = BuildBarHost(panel),
                Margin = new Thickness(0, 0, 0, 0)
            };
            _drawSettingsBar.SetResourceReference(Border.BackgroundProperty, "BgFlyout");
            _drawSettingsBar.SetResourceReference(Border.BorderBrushProperty, "PaneBorderBrush");

            var previewArea = PagePreviewPanel.Parent as Grid;
            if (previewArea is not null)
            {
                Panel.SetZIndex(_drawSettingsBar, 100);
                previewArea.Children.Add(_drawSettingsBar);
                // Cap the wrap panel to the document area's width (less the bar chrome) so it wraps the
                // control groups to new rows once the window is too narrow to hold them on one line.
                panel.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { Source = previewArea, Converter = _barWidthInset });
                // Same cap on the nested Size+Opacity panel so, once it has dropped to its own row, it
                // splits Size / Opacity when that row is too narrow.
                dRest.SetBinding(FrameworkElement.MaxWidthProperty, new System.Windows.Data.Binding("ActualWidth")
                { Source = previewArea, Converter = _barWidthInset });
                WireBarWrapAdaptation(panel, drawGrip, colorGroup, previewArea);
                PlaceAnnotationBar(_drawSettingsBar, drawGrip, fadeIn: appearing);
            }
            _annotBarTool = tool;
            _annotBarMinimized = false;   // a freshly built bar is full-size
        }

        private void HideDrawSettings()
        {
            FadeOutAndRemoveBar(_drawSettingsBar);
            _drawSettingsBar = null;
            if (_annotBarTool is EditTool.Draw or EditTool.Highlight
                or EditTool.Strikethrough or EditTool.Underline or EditTool.Shape)
                _annotBarTool = null;
        }
    }
}
