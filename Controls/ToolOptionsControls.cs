using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The small controls of the docked options bars: color swatches, icon toggles, checks and
    /// steppers. Each is a real control with a name, a Tab stop and a hit area of at least 24 x 24,
    /// so the keyboard reaches it and Space or Enter presses it. The family focus ring is applied
    /// to every control by AccessibleControls, so none of these turns its focus visual off.
    /// </summary>
    public static class ToolOptionsControls
    {
        /// <summary>The height of every control in the row.</summary>
        public const double ControlHeight = 24;

        private static SolidColorBrush Frozen(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }

        /// <summary>A quick color: an 18 x 18 chip in a 24 x 24 toggle that is checked when it is the
        /// current color.</summary>
        public static FrameworkElement Swatch(Color color, bool selected, string name, Action click)
            => Pressable(new ToggleButton { IsChecked = selected }, Chip(Frozen(color), selected), 24, name, name, click);

        /// <summary>The string key of a quick color's name, for its accessible name. Null for a color
        /// with no name of its own (a custom swatch), which is then named by its hex value.</summary>
        public static string? ColorNameKey(Color color) => (color.R, color.G, color.B) switch
        {
            (0xE0, 0x3C, 0x3C) => "Str_Color_Red",
            (0xE8, 0x7A, 0x1E) => "Str_Color_Orange",
            (0xF2, 0xC0, 0x1E) => "Str_Color_Yellow",
            (0x2E, 0xA5, 0x4C) => "Str_Color_Green",
            (0x2E, 0x86, 0xDE) => "Str_Color_Blue",
            (0x8E, 0x5B, 0xD6) => "Str_Color_Purple",
            (0xE0, 0x4A, 0x9A) => "Str_Color_Pink",
            (0x00, 0x00, 0x00) => "Str_Color_Black",
            (0xFF, 0xFF, 0xFF) => "Str_Color_White",
            _ => null,
        };

        /// <summary>The color group's compact form on a narrow pane: one chip of the current color
        /// that opens the swatches below itself.</summary>
        public static ButtonBase ColorChip(Color color, string name)
            => (ButtonBase)Pressable(new Button(), Chip(Frozen(Color.FromRgb(color.R, color.G, color.B)), true), 24, name, name, () => { });

        /// <summary>The "More colors..." chip that opens the full picker. When the current color is a
        /// custom one it shows that color, ringed, with a rainbow corner.</summary>
        public static FrameworkElement MoreColors(string name, Action click, Color? custom = null)
        {
            var chip = Chip(custom is Color c ? Frozen(Color.FromRgb(c.R, c.G, c.B)) : Rainbow(), custom is not null);
            FrameworkElement face = chip;
            if (custom is not null)
            {
                var grid = new Grid { Width = 18, Height = 18 };
                grid.Children.Add(chip);
                grid.Children.Add(new System.Windows.Shapes.Polygon
                {
                    Points = [new Point(18, 7), new Point(18, 18), new Point(7, 18)],
                    Fill = Rainbow(), IsHitTestVisible = false
                });
                face = grid;
            }
            return Pressable(new Button(), face, 24, name, name, click);
        }

        /// <summary>The text fill's "no fill" chip: white with a red stroke through it.</summary>
        public static FrameworkElement NoFill(bool selected, string name, Action click)
        {
            var grid = new Grid { Width = 18, Height = 18 };
            grid.Children.Add(Chip(Brushes.White, selected));
            grid.Children.Add(new System.Windows.Shapes.Line
            { X1 = 3, Y1 = 15, X2 = 15, Y2 = 3, Stroke = Brushes.Red, StrokeThickness = 1.5, IsHitTestVisible = false });
            return Pressable(new ToggleButton { IsChecked = selected }, grid, 24, name, name, click);
        }

        /// <summary>A 28 x 24 toggle with a drawn glyph: the measure types, the shape kinds and
        /// Bold, Italic, Strikethrough and Underline. Checked shows the accent edge and fill.</summary>
        public static FrameworkElement IconToggle(UIElement glyph, bool isChecked, string name, Action click,
            object? toolTip = null)
        {
            var button = new ToggleButton { IsChecked = isChecked };
            Pressable(button, glyph, 28, name, toolTip ?? name, click);
            button.Template = FaceTemplate(typeof(ToggleButton), checkedEdge: true);
            return button;
        }

        /// <summary>A 24 x 24 stepper button, such as the text size minus and plus.</summary>
        public static FrameworkElement Step(string glyph, string name, Action click)
        {
            var text = new TextBlock
            {
                Text = glyph, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            text.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
            var button = (Button)Pressable(new Button(), text, 24, name, name, click);
            button.Template = FaceTemplate(typeof(Button), checkedEdge: false, outlined: true);
            return button;
        }

        /// <summary>A compact check row (Level, Eraser, Fill): a 15 x 15 box and its label, both in
        /// the 24 tall hit area.</summary>
        public static FrameworkElement Check(string label, bool isChecked, string name, Action<bool> changed,
            object? toolTip = null)
        {
            var check = new CheckBox
            {
                IsChecked = isChecked,
                Content = label,
                MinHeight = ControlHeight,
                FontSize = 11,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                ToolTip = toolTip ?? name,
                Template = CheckTemplate()
            };
            check.SetResourceReference(Control.FontFamilyProperty, "UiFont");
            check.SetResourceReference(Control.ForegroundProperty, "MutedTextBrush");
            AutomationProperties.SetName(check, name);
            check.Checked += (_, _) => changed(true);
            check.Unchecked += (_, _) => changed(false);
            return check;
        }

        private static Border Chip(Brush fill, bool selected)
        {
            var chip = new Border
            {
                Width = 18, Height = 18, CornerRadius = new CornerRadius(3), Background = fill,
                BorderThickness = new Thickness(selected ? 2 : 1)
            };
            chip.SetResourceReference(Border.BorderBrushProperty, selected ? "PrimaryBrush" : "CardBorderBrush");
            return chip;
        }

        private static LinearGradientBrush Rainbow()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 1),
                GradientStops =
                {
                    new GradientStop(Colors.Red, 0), new GradientStop(Colors.Yellow, 0.25),
                    new GradientStop(Colors.Lime, 0.5), new GradientStop(Colors.Cyan, 0.7),
                    new GradientStop(Colors.Blue, 1)
                }
            };
            brush.Freeze();
            return brush;
        }

        private static FrameworkElement Pressable(ButtonBase button, UIElement face, double width, string name,
            object toolTip, Action click)
        {
            button.Content = face;
            button.Width = width;
            button.Height = ControlHeight;
            button.Padding = new Thickness(0);
            button.Cursor = Cursors.Hand;
            button.VerticalAlignment = VerticalAlignment.Center;
            button.ToolTip = toolTip;
            button.Template = FaceTemplate(button.GetType(), checkedEdge: false);
            AutomationProperties.SetName(button, name);
            button.Click += (_, _) => click();
            return button;
        }

        // A transparent face with a hover wash; the checked mode buttons add the accent edge and fill.
        private static ControlTemplate FaceTemplate(Type owner, bool checkedEdge, bool outlined = false)
        {
            var face = new FrameworkElementFactory(typeof(Border)) { Name = "face" };
            face.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            face.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            face.SetValue(Border.BorderThicknessProperty, new Thickness(checkedEdge || outlined ? 1 : 0));
            face.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            var wash = new FrameworkElementFactory(typeof(Border)) { Name = "wash" };
            wash.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
            wash.SetValue(UIElement.OpacityProperty, 0.0);
            wash.SetResourceReference(Border.BackgroundProperty, "SelectionBg");
            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            var grid = new FrameworkElementFactory(typeof(Grid));
            grid.AppendChild(wash);
            grid.AppendChild(content);
            face.AppendChild(grid);

            var template = new ControlTemplate(owner) { VisualTree = face };
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("RowHoverBrush"), "face"));
            template.Triggers.Add(hover);
            if (checkedEdge)
            {
                var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
                on.Setters.Add(new Setter(Border.BorderBrushProperty, new DynamicResourceExtension("SelectionAccent"), "face"));
                on.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(1.5), "face"));
                on.Setters.Add(new Setter(UIElement.OpacityProperty, 0.35, "wash"));
                template.Triggers.Add(on);
            }
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4, "face"));
            template.Triggers.Add(disabled);
            return template;
        }

        private static ControlTemplate CheckTemplate()
        {
            var row = new FrameworkElementFactory(typeof(StackPanel));
            row.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            row.SetValue(Panel.BackgroundProperty, Brushes.Transparent);
            row.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);

            var box = new FrameworkElementFactory(typeof(Border)) { Name = "box" };
            box.SetValue(FrameworkElement.WidthProperty, 15.0);
            box.SetValue(FrameworkElement.HeightProperty, 15.0);
            box.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetResourceReference(Border.BorderBrushProperty, "CardBorderBrush");
            box.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            box.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            box.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 5, 0));
            var mark = new FrameworkElementFactory(typeof(TextBlock)) { Name = "mark" };
            mark.SetValue(TextBlock.TextProperty, "✓");
            mark.SetValue(TextBlock.FontSizeProperty, 10.0);
            mark.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            mark.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            mark.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            mark.SetResourceReference(TextBlock.ForegroundProperty, "SelectionFg");
            box.AppendChild(mark);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            row.AppendChild(box);
            row.AppendChild(content);

            var template = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "mark"));
            on.Setters.Add(new Setter(Border.BorderThicknessProperty, new Thickness(0), "box"));
            on.Setters.Add(new Setter(Border.BackgroundProperty, new DynamicResourceExtension("SelectionBg"), "box"));
            template.Triggers.Add(on);
            return template;
        }
    }

    /// <summary>
    /// One user gesture on an options bar slider: a thumb drag, a held or pressed key, a track click, or one wheel tick. A bar
    /// records one undo step per gesture, not one per tick, so a drag cannot fill the undo history by itself.
    /// </summary>
    internal sealed class SliderGesture
    {
        private bool _open;

        public SliderGesture(Slider slider)
        {
            ArgumentNullException.ThrowIfNull(slider);
            // Handled events count too: the thumb and track mark their own input handled.
            slider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) => _open = false), true);
            slider.AddHandler(UIElement.PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) => _open = false), true);
            slider.AddHandler(UIElement.KeyUpEvent, new KeyEventHandler((_, _) => _open = false), true);
            slider.AddHandler(UIElement.LostKeyboardFocusEvent,
                new KeyboardFocusChangedEventHandler((_, _) => _open = false), true);
            // The app's wheel handler changes the value without focusing the slider or raising any event above, so a
            // wheel tick would leave the gesture open. Class handlers run first: the tick opens a gesture while the
            // value changes and this closes it right after, one undo step per tick.
            slider.AddHandler(UIElement.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, _) => _open = false), true);
        }

        /// <summary>True for the first change of a gesture, false for the rest of it.</summary>
        public bool Starts()
        {
            if (_open) return false;
            _open = true;
            return true;
        }
    }
}
