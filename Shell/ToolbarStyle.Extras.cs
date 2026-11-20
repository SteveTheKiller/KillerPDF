using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using KillerFamily.Accessibility;

namespace KillerPDF.Shell
{
    /// <summary>
    /// APP-OWNED: KillerPDF's side of the family toolbar (Shell/ToolbarStyle.cs): which buttons
    /// are on the bar and their captions, the three glyphs drawn instead of typeset (Transform,
    /// Compare, Form field), the picker's shortcuts and its Customize and Hide rows, the Open,
    /// Save and OCR split buttons, the caption shed order, and the reflow after each restyle.
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>The kit's settings store, over App's.</summary>
        private static class Settings
        {
            internal static string? Get(string key) => App.GetSetting(key);
            internal static void Set(string key, string value) => App.SetSetting(key, value);
        }

        private IEnumerable<(Button Btn, string Glyph, string LabelKey)> ToolbarItems() => _toolbarButtons;

        // Each toolbar icon button paired with its glyph and label-resource key, built once so the
        // appearance can be rebuilt without re-walking the tree.
        private readonly List<(Button btn, string glyph, string labelKey)> _toolbarButtons = [];
        private const string TransformGlyph = "TransformBoxMove";
        private const string CompareGlyph = "CompareDocuments";
        private const string FormFieldGlyph = "FillableTextField";

        // Maps each toolbar glyph (Segoe MDL2 Assets code point) to its caption string key. Buttons
        // whose glyph isn't listed keep their icon with no caption.
        private static readonly Dictionary<string, string> _toolbarLabelKeys = new()
        {
            [""] = "Str_Lbl_New",
            [""] = "Str_Lbl_Open",
            [""] = "Str_Lbl_Close",
            [""] = "Str_Lbl_Save",
            [""] = "Str_Lbl_Flatten",
            [""] = "Str_Lbl_Ocr",
            [""] = "Str_Lbl_Print",
            [""] = "Str_Lbl_Merge",
            [""] = "Str_Lbl_Extract",
            [""] = "Str_Lbl_Delete",
            [""] = "Str_Lbl_MoveUp",
            [""] = "Str_Lbl_MoveDown",
            [""] = "Str_Lbl_Select",
            [""] = "Str_Lbl_Text",
            [""] = "Str_Lbl_Highlight",
            [""] = "Str_Lbl_Strike",
            [""] = "Str_Lbl_Underline",
            [""] = "Str_Lbl_Draw",
            [""] = "Str_Lbl_Crop",
            [TransformGlyph] = "Str_Lbl_Rotate",
            [""] = "Str_Lbl_Image",
            [""] = "Str_Lbl_Signature",
            [""] = "Str_Lbl_Undo",
            [""] = "Str_Lbl_Clear",
            [""] = "Str_Lbl_ZoomOut",
            [""] = "Str_Lbl_ZoomIn",
            [""] = "Str_Lbl_Highlight",   // current highlighter glyph (see ToolHighlightBtn)
            [""] = "Str_Lbl_Line",   // repurposed ToolUnderlineBtn glyph = the Line tool
            [""] = "Str_Lbl_ZoomOut",   // boxed minus (RemoveFrom) - new zoom-out glyph
            [""] = "Str_Lbl_ZoomIn",    // boxed plus  (AddTo)      - new zoom-in glyph
            [""] = "Str_Lbl_Search",    // magnifier - toolbar search button
            [""] = "Str_Lbl_Stamp",     // page-number / watermark stamp tool
            [""] = "Str_Lbl_Shape",   // Shapes tool (rect / ellipse / polygon)
            ["\uED5E"] = "Str_Lbl_Measure",
            ["\uED1A"] = "Str_Lbl_Redact",
            ["\uE790"] = "Str_ColorCorrection",
            [""] = "Str_Ctx_Redo",
        };

        // Walks LeftBar + RightBar once and records each icon button with its glyph + label key.
        private void IndexToolbarButtons()
        {
            _toolbarButtons.Clear();
            foreach (Panel? bar in new Panel?[] { LeftBar, RightBar })
            {
                if (bar is null) continue;
                foreach (var btn in DescendantButtons(bar))
                    if (ReferenceEquals(btn, ToolRotateBtn))
                        _toolbarButtons.Add((btn, TransformGlyph, "Str_Lbl_Rotate"));
                    else if (ReferenceEquals(btn, ComparePdfBtn))
                        _toolbarButtons.Add((btn, CompareGlyph, "Str_TT_ComparePDFs"));
                    else if (ReferenceEquals(btn, ToolFormFieldBtn))
                        _toolbarButtons.Add((btn, FormFieldGlyph, "Str_Lbl_FormField"));
                    else if (btn.Content is string g && g.Length > 0 && _toolbarLabelKeys.TryGetValue(g, out var key))
                        _toolbarButtons.Add((btn, g, key));
            }
        }

        private static IEnumerable<Button> DescendantButtons(DependencyObject root)
        {
            foreach (var obj in LogicalTreeHelper.GetChildren(root))
            {
                if (obj is Button b) yield return b;
                if (obj is DependencyObject d)
                    foreach (var nested in DescendantButtons(d)) yield return nested;
            }
        }

        // Every toolbar glyph is drawn here, not only the three the icon font cannot draw, so the
        // icon-font ones keep following the theme's IconFont resource (UiKit.IconFont).
        partial void ToolbarGlyph(string glyph, double size, ref FrameworkElement? element)
            => element = DrawToolbarGlyph(glyph, size);

        private static FrameworkElement DrawToolbarGlyph(string glyph, double glyphSize)
        {
            if (glyph == FormFieldGlyph)
            {
                double scale = glyphSize / 16;
                var field = new Grid { Width = 18 * scale, Height = glyphSize };
                var border = new Border
                {
                    BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1),
                    Margin = new Thickness(scale, 2 * scale, scale, scale)
                };
                border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                field.Children.Add(border);
                // The T is drawn, not typeset, so it stays centred in the box whatever the UI font is.
                var tee = new System.Windows.Shapes.Path
                {
                    // Drawn on half-pixel centres at 1x so both strokes land on whole pixels.
                    Data = Geometry.Parse("M0,0.5 L7,0.5 M3.5,0.5 L3.5,7"),
                    Width = 7, Height = 7, Stretch = Stretch.None, StrokeThickness = 1,
                    LayoutTransform = new ScaleTransform(scale, scale),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, scale, 0, 0),
                    SnapsToDevicePixels = true
                };
                tee.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
                });
                field.Children.Add(tee);
                return field;
            }
            if (glyph == CompareGlyph)
            {
                var documents = new Grid { Width = glyphSize * 18 / 16, Height = glyphSize };
                foreach (bool right in new[] { false, true })
                {
                    string brush = right ? "PrimaryBrush" : "TextBrush";
                    var line = new Rectangle
                    {
                        Height = 1, Margin = new Thickness(1),
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    line.SetResourceReference(Shape.FillProperty, brush);
                    var document = new Border
                    {
                        Width = glyphSize * 7 / 16, Height = glyphSize * 13 / 16,
                        HorizontalAlignment = right ? HorizontalAlignment.Right : HorizontalAlignment.Left,
                        VerticalAlignment = VerticalAlignment.Center,
                        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(1),
                        Child = line
                    };
                    document.SetResourceReference(Border.BorderBrushProperty, brush);
                    documents.Children.Add(document);
                }
                return documents;
            }
            if (glyph != TransformGlyph)
            {
                return new Glyph
                {
                    Text = glyph,
                    FontFamily = UiKit.IconFont,
                    FontSize = glyphSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }

            var icon = new Grid { Width = glyphSize + 2, Height = glyphSize + 2 };
            var box = new Border
            {
                Width = glyphSize,
                Height = glyphSize,
                BorderThickness = new Thickness(1),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            box.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
            icon.Children.Add(box);
            // Four arrows drawn as vector strokes. The Segoe MDL2 move glyph shrunk to fit the box
            // rendered its bottom arrowhead as a single pixel, so it read as cut off.
            var arrows = new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse("M8,2.5 L8,6 M8,10 L8,13.5 M2.5,8 L6,8 M10,8 L13.5,8 " +
                                      "M6,4.5 L8,2.5 L10,4.5 M6,11.5 L8,13.5 L10,11.5 " +
                                      "M4.5,6 L2.5,8 L4.5,10 M11.5,6 L13.5,8 L11.5,10"),
                Width = glyphSize * 0.7,
                Height = glyphSize * 0.7,
                Stretch = Stretch.Uniform,
                StrokeThickness = 1,
                StrokeStartLineCap = PenLineCap.Square,
                StrokeEndLineCap = PenLineCap.Square,
                StrokeLineJoin = PenLineJoin.Miter,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true
            };
            arrows.SetBinding(Shape.StrokeProperty, new System.Windows.Data.Binding(nameof(Button.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
            icon.Children.Add(arrows);
            return icon;
        }

        // The XAML tooltips carry the shortcut already; keep them.
        partial void ToolbarTooltip(Button btn, string label, ref object tip)
        {
            if (btn.ToolTip is { } existing) tip = existing;
        }

        partial void ToolbarMenuGesture(string id, ref string gesture) => gesture = GestureFor(id);

        partial void OnToolbarStyleRestored()
        {
            // Restore directly - SetToolbarHidden would overwrite the "Ready" status line at startup.
            if (App.GetSetting("ToolbarHidden") == "1")
            { _toolbarHidden = true; ToolbarRowBorder.Visibility = Visibility.Collapsed; }
        }

        // The picker card's last row: Customize, Hide toolbar, and the tool menu placement a click switches to.
        partial void AddToolbarMenuTiles(List<KillerFamily.Controls.CardTile> tiles)
        {
            tiles.Add(KitTile(new CardTile("\uE771", Loc("Str_Toolbar_Customize"), null, OpenCustomizeToolbar, false, true)));
            tiles.Add(KitTile(new CardTile("\uE73F", Loc("Str_Toolbar_Hide"), "Alt:M", ToggleToolbarHidden, false, true)));
            tiles.Add(KitTile(new CardTile(ToolMenuFloating ? "\uE737" : "\uE8A7",
                Loc(ToolMenuFloating ? "Str_ToolMenu_Docked" : "Str_ToolMenu_Floating"), "Alt:B", ToggleToolMenuFloating, false, true)));
        }

        // Open / Save / OCR are split buttons (main half + overlapping dropdown chevron); the kit
        // applies their chrome from this one list so the three never drift apart.
        partial void ToolbarSplitButtons(List<(Button? Main, Button? Chevron, string SplitStyle, string PlainStyle)> splits)
        {
            splits.Add((OpenFileBtn, OpenRecentBtn, "ToolbarSplitMain",       "ToolbarButton"));
            splits.Add((SaveAsBtn,   SaveMenuBtn,   "ToolbarSplitMainAccent", "ToolbarButtonAccent"));
            splits.Add((OcrBtn,      OcrMenuBtn,    "ToolbarSplitMain",       "ToolbarButton"));
        }

        // Caption shed order when the bar runs short of room: lowest rank sheds first. Zoom and
        // Select go first (their icons are obvious); the annotation tools keep their captions
        // longest because that is where the labels earn their space.
        partial void ToolbarCaptionShedRank(string labelKey, ref int rank) => rank = labelKey switch
        {
            "Str_Lbl_ZoomOut" or "Str_Lbl_ZoomIn" => 0,
            "Str_Lbl_Select" => 1,
            "Str_Lbl_Undo" or "Str_Ctx_Redo" or "Str_Lbl_Clear" => 2,
            "Str_Lbl_New" or "Str_Lbl_Open" or "Str_Lbl_Close"
                or "Str_Lbl_Save" or "Str_Lbl_Flatten" or "Str_Lbl_Ocr" or "Str_Lbl_Print" => 3,
            "Str_Lbl_MoveUp" or "Str_Lbl_MoveDown" or "Str_Lbl_Delete"
                or "Str_Lbl_Merge" or "Str_Lbl_Extract" => 4,
            _ => 5,
        };

        partial void OnToolbarStyleApplied()
        {
            if (_toolbarButtons.Count == 0) return;   // the ctor's restore runs before IndexToolbarButtons
            InvalidateToolbarReflow();   // the buttons themselves changed, so the last decision is void
            ReflowToolbar();
        }
    }
}
