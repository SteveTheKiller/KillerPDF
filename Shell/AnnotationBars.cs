using System.Diagnostics;
using KillerPDF.Controls;
using KillerPDF.Shell;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Microsoft.Win32;
using KillerPDF.Services;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    public partial class MainWindow
    {
        // ============================================================
        // Docked tool options bars (Draw, Highlight, Underline, Strikethrough, Line, Shapes, Measure)
        // ============================================================

        // The quick-colors shown in the options bars. User-configurable via the color picker's swatch
        // row (shared "UserSwatches" setting); seeded with these defaults, restorable via the picker's
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

        private static T Freeze<T>(T freezable) where T : System.Windows.Freezable
        {
            freezable.Freeze();
            return freezable;
        }

        // Wraps a floating panel's content (the search bar) with the app's film-grain layer, so it
        // carries the same texture as the Settings / signature / dialog surfaces.
        private static Grid GrainWrap(UIElement content)
        {
            var g = new Grid();
            // SetResourceReference, not a FindResource snapshot: these hosts are built once and
            // outlive theme switches, so a snapshot kept the grain painting on 98SE (whose
            // GrainOpacity is 0) and its -4 margin overhang made the bevel read as misaligned.
            var grain = new Border { Margin = new Thickness(-4), IsHitTestVisible = false };
            grain.SetResourceReference(Border.CornerRadiusProperty, "AnnotationBarCornerRadius");
            grain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            grain.SetResourceReference(Border.BackgroundProperty, "GrainBrushShared");
            g.Children.Add(grain);
            g.Children.Add(content);
            return g;
        }

        // The grain and 98SE dark edge for the viewer's notice bars (native viewing, restrictions).
        // Tool options no longer use it: they dock in their pane's ToolOptionsBar.
        private Grid BuildBarHost(FrameworkElement content)
        {
            var host = new Grid();
            var darkEdge = new Border { IsHitTestVisible = false };
            darkEdge.SetResourceReference(Border.BorderBrushProperty, "BarEdgeDarkBrush");
            darkEdge.SetResourceReference(Border.BorderThicknessProperty, "BarEdgeDarkThickness");
            Panel.SetZIndex(darkEdge, 20);
            host.Children.Add(darkEdge);
            var hostGrain = new Border { Margin = new Thickness(-4), IsHitTestVisible = false };
            hostGrain.SetResourceReference(Border.CornerRadiusProperty, "AnnotationBarCornerRadius");
            hostGrain.SetResourceReference(UIElement.OpacityProperty, "GrainOpacity");
            hostGrain.SetResourceReference(Border.BackgroundProperty, "GrainBrushShared");
            host.Children.Add(hostGrain);
            host.Children.Add(content);
            return host;
        }

        // Drop shadow for the viewer's floating notice bars: straight down, never above the bar.
        private static System.Windows.Media.Effects.DropShadowEffect AnnotBarShadow()
            => new() { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 3, Direction = 270,
                Opacity = Application.Current.TryFindResource("BarShadowOpacity") is double opacity ? opacity : 0.38 };

        private EditTool? _annotBarTool { get => ActiveViewer.AnnotBarToolRef; set => ActiveViewer.AnnotBarToolRef = value; }

        // Set while FocusPane re-applies the pane's tool, so SetTool keeps a bar that is already docked.
        private bool _focusingPane;

        // The floating bar remembered where it was slid to. The docked row has no position, so these
        // are dropped the first time 1.9.3 runs.
        private static void RetireFloatingBarSettings()
        {
            foreach (string key in new[] { "AnnotBarFrac", "AnnotBarGap", "AnnotBarRightSide" })
                if (App.GetSetting(key) is not null) App.RemoveSetting(key);
        }

        // ---- shared row parts ----------------------------------------------------------------

        private static string ToolNameKey(EditTool tool) => tool switch
        {
            EditTool.Text => "Str_Lbl_Text",
            EditTool.Highlight => "Str_Lbl_Highlight",
            EditTool.Underline => "Str_Lbl_Underline",
            EditTool.Strikethrough => "Str_Lbl_Strike",
            EditTool.Line => "Str_Lbl_Line",
            EditTool.Shape => "Str_Lbl_Shape",
            EditTool.Draw => "Str_Lbl_Draw",
            EditTool.Measure => "Str_Lbl_Measure",
            EditTool.Crop => "Str_Lbl_Crop",
            _ => "Str_Lbl_Redact",
        };

        private ToolOptionsBar NewToolBar(EditTool tool) => new(tool, Loc(ToolNameKey(tool)), Loc("Str_Bar_More"));

        // Docks a bar in the active pane, replacing whatever that pane's row showed.
        private void DockToolBar(ToolOptionsBar bar) => ActiveViewer.ToolOptionsDockRef.Bar = bar;

        // Takes a bar out of the active pane's row, if it is still the one shown there.
        private void UndockToolBar(Border? bar)
        {
            var dock = ActiveViewer.ToolOptionsDockRef;
            if (bar is not null && ReferenceEquals(dock.Bar, bar)) dock.Bar = null;
        }

        // The label in front of a group ("Color:", "Size:"), 6 from its control.
        private static TextBlock BarLabel(string text)
        {
            var label = new TextBlock
            {
                Text = text, FontFamily = UiKit.UiFont, FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0)
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return label;
        }

        private static StackPanel BarGroup(params UIElement[] children)
        {
            var group = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            foreach (var child in children) group.Children.Add(child);
            return group;
        }

        // An 80-wide slider named after its label, so a screen reader says what it changes. The app's
        // DarkSlider style is not focusable; in the row a slider is a Tab stop the arrows move.
        private Slider BarSlider(double min, double max, double value, string name)
        {
            var slider = new Slider
            {
                Minimum = min, Maximum = max, Value = value, Width = 80,
                VerticalAlignment = VerticalAlignment.Center, Style = (Style)FindResource("DarkSlider")
            };
            slider.Focusable = true;
            slider.IsTabStop = true;
            slider.Height = ToolOptionsControls.ControlHeight;
            slider.SmallChange = Math.Max(slider.SmallChange, (max - min) <= 3 ? 0.05 : 1);
            AutomationProperties.SetName(slider, name);
            return slider;
        }

        // The mono readout after a slider, fixed at 36 and right-aligned so the row never shifts.
        private static TextBlock BarReadout(string text, double width = 36)
        {
            var readout = new TextBlock
            {
                Text = text, FontFamily = UiKit.MonoFont, FontSize = 11, Width = width,
                TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0)
            };
            readout.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            return readout;
        }

        private static string LabelName(string label) => label.Trim().TrimEnd(':', '：').Trim();

        private static string Hex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

        // A swatch's accessible name: its group and its color's name in the app's language, or the
        // hex value for a color without a name ("Color: Red", "Fill: #123456").
        private string SwatchName(string groupLabel, Color c) =>
            string.Format(Loc("Str_Bar_SwatchName"), LabelName(groupLabel),
                ToolOptionsControls.ColorNameKey(c) is { } key ? Loc(key) : Hex(c));

        // A combo 24 tall and as wide as its longest item plus the chevron, capped at 160 (spec 3.3).
        private static ComboBox CompactCombo(IEnumerable<string> texts, string name)
        {
            var combo = new ComboBox
            {
                Height = ToolOptionsControls.ControlHeight, FontSize = 12, VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center, ToolTip = name
            };
            if (UiKit.DarkComboStyle is Style style) combo.Style = style;
            combo.Height = ToolOptionsControls.ControlHeight;
            AutomationProperties.SetName(combo, name);
            var typeface = new Typeface(UiKit.UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            double widest = 0;
            foreach (string text in texts)
                widest = Math.Max(widest, new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, typeface, 12, Brushes.Black, 1.0).WidthIncludingTrailingWhitespace);
            combo.Width = Math.Min(160, Math.Ceiling(widest) + 8 + 22 + 6);
            return combo;
        }

        // The quick colors, then More colors... A swatch is checked when it is the current color.
        // The chip is the group's compact form for a narrow pane. Ids let a rebuilt bar give the
        // keyboard focus back to the swatch that was pressed.
        private (StackPanel Group, ButtonBase Chip) BuildColorGroup(string idPrefix, Color current, bool showCurrent,
            Action<Color> pick, Action openMore, UIElement? leading = null)
        {
            string label = Loc("Str_Bar_Color");
            if (idPrefix == "Fill") label = Loc("Str_Bar_Fill");
            var group = BarGroup(BarLabel(label));
            if (leading is not null) group.Children.Add(leading);
            var swatches = SwatchColors;
            for (int i = 0; i < swatches.Length; i++)
            {
                var c = swatches[i];
                bool on = showCurrent && c.R == current.R && c.G == current.G && c.B == current.B;
                var swatch = ToolOptionsControls.Swatch(c, on, SwatchName(label, c), () => pick(c));
                AutomationProperties.SetAutomationId(swatch, idPrefix + "Swatch" + i);
                group.Children.Add(swatch);
            }
            bool custom = showCurrent && !swatches.Any(c => c.R == current.R && c.G == current.G && c.B == current.B);
            var more = ToolOptionsControls.MoreColors(Loc("Str_Bar_MoreColors"), openMore, custom ? current : null);
            AutomationProperties.SetAutomationId(more, idPrefix + "MoreColors");
            group.Children.Add(more);
            var chip = ToolOptionsControls.ColorChip(showCurrent ? current : Colors.White,
                showCurrent ? SwatchName(label, current) : LabelName(label));
            AutomationProperties.SetAutomationId(chip, idPrefix + "Chip");
            return (group, chip);
        }

        // ---- the draw bar family -------------------------------------------------------------

        private void ShowDrawSettings(EditTool tool)
        {
            var bar = NewToolBar(tool);
            bool usesDrawStyle = tool is EditTool.Draw or EditTool.Line or EditTool.Shape or EditTool.Measure;
            bool isLineTool = tool is EditTool.Strikethrough or EditTool.Underline;

            // The Measure tool leads with its own controls; color, size and opacity follow.
            if (tool == EditTool.Measure)
                foreach (var (id, group) in BuildMeasureGroups()) bar.Add(id, group);
            else
            {
                _measureScaleButton = null;
                _measurePageSizeText = null;
            }

            // Shapes (#127 Phase 3): radio-style toggles whose glyphs are literal mini shapes (drawn
            // WPF elements, so they render on every locale and font), then the Fill check.
            if (tool == EditTool.Shape)
            {
                var kinds = BarGroup();
                foreach (var (kind, glyph, tipKey) in new (ShapeKind, Shape, string)[]
                {
                    (ShapeKind.Rectangle, new Rectangle { Width = 13, Height = 9 }, "Str_Bar_TT_ShapeBox"),
                    (ShapeKind.Ellipse, new Ellipse { Width = 13, Height = 9 }, "Str_Bar_TT_ShapeEllipse"),
                    (ShapeKind.Polygon, new Polygon
                    {
                        Width = 13, Height = 11,
                        Points = [new Point(6.5, 0), new Point(13, 4.5), new Point(10.5, 11), new Point(2.5, 11), new Point(0, 4.5)]
                    }, "Str_Bar_TT_ShapeFreeform"),
                })
                {
                    bool active = _shapeKind == kind;
                    glyph.StrokeThickness = 1.5;
                    glyph.Fill = Brushes.Transparent;
                    glyph.SetResourceReference(Shape.StrokeProperty, active ? "SelectionAccent" : "MutedTextBrush");
                    var toggle = ToolOptionsControls.IconToggle(glyph, active, Loc(tipKey), () =>
                    {
                        if (_shapeKind == kind) { ShowDrawSettings(tool); return; }
                        _shapeKind = kind;
                        if (kind != ShapeKind.Polygon) CancelShapePolygon();
                        ShowDrawSettings(tool);
                    });
                    AutomationProperties.SetAutomationId(toggle, "ShapeKind" + kind);
                    kinds.Children.Add(toggle);
                }
                bar.Add(ToolOptionGroup.Kinds, kinds);
                bar.Add(ToolOptionGroup.Fill, ToolOptionsControls.Check(Loc("Str_Bar_ShapeFill"), _shapeFill,
                    Loc("Str_Bar_ShapeFill"), value => _shapeFill = value, Loc("Str_Bar_TT_ShapeFill")));
            }

            var activeColor = usesDrawStyle ? _drawColor
                            : isLineTool ? Color.FromRgb(_lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B)
                            : Color.FromRgb(_highlightColor.R, _highlightColor.G, _highlightColor.B);
            void ApplyPick(Color c)
            {
                if (usesDrawStyle) _drawColor = Color.FromArgb(_drawOpacity, c.R, c.G, c.B);
                else if (isLineTool) _lineAnnotColor = Color.FromArgb(_lineAnnotColor.A, c.R, c.G, c.B);
                else _highlightColor = Color.FromArgb(_highlightColor.A, c.R, c.G, c.B);
                ApplyDrawStyleToSelection();   // edit the selected annotation, if any
                ShowDrawSettings(tool);        // show the new current swatch
            }
            var (colorGroup, colorChip) = BuildColorGroup("Draw", activeColor, true, ApplyPick,
                () => OpenColorPicker(activeColor, ApplyPick, () => ShowDrawSettings(tool)));
            bar.Add(ToolOptionGroup.Color, colorGroup, colorChip);

            if (usesDrawStyle)
            {
                var sizeSlider = BarSlider(1, 60, _drawWidth, LabelName(Loc("Str_Bar_Size")));
                sizeSlider.TickFrequency = 1;
                sizeSlider.IsSnapToTickEnabled = true;
                var sizeReadout = BarReadout($"{_drawWidth:F0}px");
                var sizeGesture = new Controls.SliderGesture(sizeSlider);
                sizeSlider.ValueChanged += (_, e) =>
                {
                    _drawWidth = e.NewValue;
                    sizeReadout.Text = $"{e.NewValue:F0}px";
                    ApplyDrawStyleToSelection(sizeGesture);
                };
                bar.Add(ToolOptionGroup.Size, BarGroup(BarLabel(Loc("Str_Bar_Size")), sizeSlider, sizeReadout));
            }

            byte currentOpacity = usesDrawStyle ? _drawOpacity : isLineTool ? _lineAnnotColor.A : _highlightColor.A;
            var opacitySlider = BarSlider(10, 255, currentOpacity, LabelName(Loc("Str_Bar_Opacity")));
            var opacityReadout = BarReadout($"{(int)(currentOpacity / 255.0 * 100)}%");
            var opacityGesture = new Controls.SliderGesture(opacitySlider);
            opacitySlider.ValueChanged += (_, e) =>
            {
                byte a = (byte)e.NewValue;
                opacityReadout.Text = $"{(int)(a / 255.0 * 100)}%";
                if (usesDrawStyle)
                {
                    _drawOpacity = a;
                    _drawColor = Color.FromArgb(a, _drawColor.R, _drawColor.G, _drawColor.B);
                }
                else if (isLineTool) _lineAnnotColor = Color.FromArgb(a, _lineAnnotColor.R, _lineAnnotColor.G, _lineAnnotColor.B);
                else _highlightColor = Color.FromArgb(a, _highlightColor.R, _highlightColor.G, _highlightColor.B);
                ApplyDrawStyleToSelection(opacityGesture);
            };
            var opacityGroup = BarGroup(BarLabel(Loc("Str_Bar_Opacity")), opacitySlider, opacityReadout);
            // Level (Line) and the brush eraser (Draw) ride with Opacity, so they shed together.
            FrameworkElement? ridingCheck = tool switch
            {
                EditTool.Line => ToolOptionsControls.Check(Loc("Str_Bar_Level"), _lineLevel, Loc("Str_Bar_Level"),
                    value => _lineLevel = value, Loc("Str_Bar_TT_Level")),
                EditTool.Draw => ToolOptionsControls.Check(Loc("Str_Bar_Eraser"), _drawErase, Loc("Str_Bar_Eraser"),
                    value => _drawErase = value, Loc("Str_Bar_TT_EraserBrush")),
                _ => null
            };
            if (ridingCheck is not null)
            {
                ridingCheck.Margin = new Thickness(12, 0, 0, 0);
                opacityGroup.Children.Add(ridingCheck);
            }
            bar.Add(ToolOptionGroup.Opacity, opacityGroup);

            if (tool == EditTool.Highlight)
                bar.Add(ToolOptionGroup.Eraser, ToolOptionsControls.Check(Loc("Str_Bar_AnnotationEraser"), _highlightErase,
                    Loc("Str_Bar_AnnotationEraser"), value => _highlightErase = value, Loc("Str_Bar_TT_EraserBox")));

            // The replacement layer grid is three rows tall, so it always lives in the "..." menu.
            if (BuildReplacementLayerControls() is { } replacementLayers)
                bar.Add(ToolOptionGroup.Layers, replacementLayers, overflowOnly: true);

            DockToolBar(bar);
            _drawSettingsBar = bar;
            _annotBarTool = tool;
        }

        private void HideDrawSettings()
        {
            UndockToolBar(_drawSettingsBar);
            _drawSettingsBar = null;
            if (ToolOptionsCatalog.BarFor(_annotBarTool) == ToolOptionsBarKind.Draw)
                _annotBarTool = null;
            _measureScaleButton = null;
            _measurePageSizeText = null;
        }
    }
}
