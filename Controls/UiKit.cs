using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace KillerPDF
{
    // WPF has no built-in animation for GridLength, so grid columns (the sidebar) can only snap.
    // This drives a pixel-unit GridLength between From and To so a column glides instead.
    // From/To/Easing MUST be dependency properties: starting the clock freezes/clones the
    // timeline, and the clone only carries DPs - as plain CLR properties they were lost and
    // the "animation" held a constant until the completion snap.
    internal sealed class GridLengthAnimation : AnimationTimeline
    {
        public static readonly DependencyProperty FromProperty =
            DependencyProperty.Register(nameof(From), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty ToProperty =
            DependencyProperty.Register(nameof(To), typeof(GridLength), typeof(GridLengthAnimation));
        public static readonly DependencyProperty EasingProperty =
            DependencyProperty.Register(nameof(Easing), typeof(IEasingFunction), typeof(GridLengthAnimation));

        public GridLength From { get => (GridLength)GetValue(FromProperty); set => SetValue(FromProperty, value); }
        public GridLength To   { get => (GridLength)GetValue(ToProperty);   set => SetValue(ToProperty, value); }
        public IEasingFunction? Easing { get => (IEasingFunction?)GetValue(EasingProperty); set => SetValue(EasingProperty, value); }

        public override Type TargetPropertyType => typeof(GridLength);
        protected override Freezable CreateInstanceCore() => new GridLengthAnimation();
        public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock animationClock)
        {
            double p = animationClock.CurrentProgress ?? 0.0;
            if (Easing is { } ease) p = ease.Ease(p);
            return new GridLength(From.Value + (To.Value - From.Value) * p);
        }
    }

    // Design tokens (fonts, radii, shadows) and code-built controls (buttons, checkboxes, fields, labels)
    // for dialogs and tools. Tokens resolve from App.xaml's resource dictionary.
    internal static class UiKit
    {
        // Mouse-wheel over ANY slider (on hover) nudges its value - one global class handler covers every
        // slider in the app and all dialogs. Handled so the wheel doesn't also scroll an enclosing panel
        // while the cursor is on the slider. Registered once when UiKit is first touched (early in startup).
        static UiKit()
        {
            EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseWheelEvent,
                new MouseWheelEventHandler(SliderWheelAdjust));
        }

        private static void SliderWheelAdjust(object sender, MouseWheelEventArgs e)
        {
            if (sender is not Slider s || !s.IsEnabled) return;
            double step = s.SmallChange > 0 ? s.SmallChange : (s.Maximum - s.Minimum) / 20.0;
            if (step <= 0) return;
            s.Value = Math.Max(s.Minimum, Math.Min(s.Maximum, s.Value + (e.Delta > 0 ? step : -step)));
            e.Handled = true;
        }

        // ---- token + theme accessors -------------------------------------------------------------
        public static FontFamily UiFont   => Res("UiFont",   _uiFallback);
        public static FontFamily MonoFont => Res("MonoFont", _monoFallback);
        public static FontFamily IconFont => Res("IconFont", _iconFallback);
        public static FontFamily WordmarkFont => Res("WordmarkFont", _wordmarkFallback);
        public static FontFamily WordmarkFontPdf => Res("WordmarkFontPdf", _wordmarkPdfFallback);
        private static readonly FontFamily _uiFallback   = new("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        private static readonly FontFamily _monoFallback = new("Consolas");
        private static readonly FontFamily _iconFallback = new("Segoe MDL2 Assets");
        private static readonly FontFamily _wordmarkFallback = new("Typewriter - a602 (dead postman 2004), Consolas");
        private static readonly FontFamily _wordmarkPdfFallback = new("Typewriter - a602 (dead postman 2004), Consolas");

        public static CornerRadius RadControl => Rad("RadControl", 3);
        public static CornerRadius RadCard    => Rad("RadCard", 6);
        public static CornerRadius RadWindow  => Rad("RadWindow", 7);

        // Fresh shadow instances (cheap) matching App.xaml's Shadow* resources, for code that builds Effects.
        public static DropShadowEffect ShadowText()   => Shadow(3,  1, 0.6);
        public static DropShadowEffect ShadowIcon()   => Shadow(4,  1, 0.9);
        public static DropShadowEffect ShadowBar()    => Shadow(6,  3, Opacity("BarShadowOpacity", 0.38));
        public static DropShadowEffect ShadowDialog() => Shadow(18, 3, Opacity("FlyoutShadowOpacity", 0.6));

        // Active-theme brush by key, with a safe fallback so the kit never throws before the theme loads.
        public static Brush Brush(string key, Brush? fallback = null)
            => Application.Current?.TryFindResource(key) as Brush ?? fallback ?? Brushes.Gray;

        // ---- inline flyout -----------------------------------------------------------------------
        // The style for small helpers that float ON the document itself (the form font-size
        // stepper; future on-page controls): a translucent dark pill that reads over any page
        // content without shouting. Slightly see-through at rest so the page underneath stays
        // visible; hovering solidifies it (animated). Deliberately theme-independent - pages are
        // usually white whatever the app theme, so one consistent dark pill (with fixed light
        // text inside) reads best everywhere.
        public const double InlineFlyoutRestOpacity = 0.85;

        public static Border InlineFlyout(FrameworkElement content)
        {
            var b = new Border
            {
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(7, 1, 7, 1),
                BorderThickness = new Thickness(1),
                Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x16, 0x16, 0x16)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                Effect = Shadow(10, 2, 0.25),
                Child = content,
                SnapsToDevicePixels = true,
            };
            b.MouseEnter += (_, _) => b.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(1.0, new Duration(TimeSpan.FromMilliseconds(110)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            b.MouseLeave += (_, _) => b.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(InlineFlyoutRestOpacity, new Duration(TimeSpan.FromMilliseconds(220)))
                { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
            return b;
        }

        private static T Res<T>(string key, T fallback) where T : class
            => Application.Current?.TryFindResource(key) as T ?? fallback;
        private static CornerRadius Rad(string key, double fb)
            => Application.Current?.TryFindResource(key) is CornerRadius c ? c : new CornerRadius(fb);
        private static DropShadowEffect Shadow(double blur, double depth, double opacity)
            => new() { Color = Colors.Black, BlurRadius = blur, ShadowDepth = depth, Direction = 270, Opacity = opacity };
        private static double Opacity(string key, double fallback)
            => Application.Current?.TryFindResource(key) is double value ? value : fallback;

        // The default quick-color palette, shared by the annotate bars and the color picker's swatch row
        // (the "UserSwatches" setting seeds from this). One source so the two can't drift.
        public static readonly Color[] DefaultSwatches =
        [
            Color.FromRgb(0xE0, 0x3C, 0x3C), Color.FromRgb(0xE8, 0x7A, 0x1E), Color.FromRgb(0xF2, 0xC0, 0x1E),
            Color.FromRgb(0x2E, 0xA5, 0x4C), Color.FromRgb(0x2E, 0x86, 0xDE), Color.FromRgb(0x8E, 0x5B, 0xD6),
            Color.FromRgb(0xE0, 0x4A, 0x9A), Colors.Black, Colors.White
        ];

        // ---- control factories -------------------------------------------------------------------

        // Themed checkbox: rounded box with an accent check mark when checked. Replaces the per-dialog
        // StyleCheckBox/ThemedCheckTemplate copies so every checkbox in the app is identical.
        public static CheckBox CheckBox(string label) => new()
        {
            Content                  = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap },
            Foreground               = Brush("TextBrush"),
            FontFamily               = UiFont,
            FontSize                 = 12,
            Cursor                   = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template                 = CheckTemplate()
        };

        private static ControlTemplate CheckTemplate()
        {
            // DockPanel, not a horizontal StackPanel. A horizontal StackPanel measures its children
            // at infinite width, so the label could never wrap however it was configured. Docking the
            // box to the left leaves the label a real width to wrap inside (#223).
            var row = new FrameworkElementFactory(typeof(DockPanel)) { Name = "root" };

            var boxHost = new FrameworkElementFactory(typeof(Grid));
            boxHost.SetValue(FrameworkElement.WidthProperty, 16.0);
            boxHost.SetValue(FrameworkElement.HeightProperty, 16.0);
            boxHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            boxHost.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 8, 0));
            boxHost.SetValue(DockPanel.DockProperty, Dock.Left);

            var box = new FrameworkElementFactory(typeof(Border));
            box.SetValue(Border.CornerRadiusProperty, RadControl);
            box.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            box.SetValue(Border.BorderBrushProperty, Brush("CardBorderBrush"));
            box.SetValue(Border.BackgroundProperty, Brush("RadioWellBrush"));
            boxHost.AppendChild(box);

            var sunkenDark = new FrameworkElementFactory(typeof(Border));
            sunkenDark.SetValue(UIElement.IsHitTestVisibleProperty, false);
            sunkenDark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            sunkenDark.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenDarkThickness");
            boxHost.AppendChild(sunkenDark);

            var sunkenLight = new FrameworkElementFactory(typeof(Border));
            sunkenLight.SetValue(UIElement.IsHitTestVisibleProperty, false);
            sunkenLight.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
            sunkenLight.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenLightThickness");
            boxHost.AppendChild(sunkenLight);

            var check = new FrameworkElementFactory(typeof(TextBlock)) { Name = "chk" };
            check.SetValue(TextBlock.TextProperty, "");   // Segoe MDL2 CheckMark
            check.SetValue(TextBlock.FontFamilyProperty, IconFont);
            check.SetValue(TextBlock.FontSizeProperty, 14.0);
            check.SetValue(TextBlock.FontWeightProperty, FontWeights.Bold);
            check.SetValue(TextBlock.ForegroundProperty, Brush("RadioAccent"));
            check.SetValue(TextBlock.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            check.SetValue(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            boxHost.AppendChild(check);

            var content = new FrameworkElementFactory(typeof(ContentPresenter));
            content.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            row.AppendChild(boxHost);
            row.AppendChild(content);

            var ct = new ControlTemplate(typeof(CheckBox)) { VisualTree = row };
            var trig = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            trig.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "chk" });
            ct.Triggers.Add(trig);
            // Disabled state: dim the whole control (box + label) so it's obviously inactive.
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4) { TargetName = "root" });
            ct.Triggers.Add(disabled);
            return ct;
        }

        // Themed radio button with a clean horizontal layout (ring + accent dot + label), built from the
        // theme brushes. Unlike the settings-panel ThemeRadio (a full-width vertical row), this lays out
        // tightly for inline/horizontal use.
        public static RadioButton Radio(string text) => new()
        {
            Content                  = text,
            Foreground               = Brush("TextBrush"),
            FontFamily               = UiFont,
            FontSize                 = 12,
            Cursor                   = Cursors.Hand,
            VerticalContentAlignment = VerticalAlignment.Center,
            Template                 = RadioTemplate()
        };

        private static ControlTemplate RadioTemplate()
        {
            var sp = new FrameworkElementFactory(typeof(StackPanel)) { Name = "root" };
            sp.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
            sp.SetValue(Panel.BackgroundProperty, Brushes.Transparent);

            var ring = new FrameworkElementFactory(typeof(Border)) { Name = "ring" };
            ring.SetValue(Border.WidthProperty, 15.0);
            ring.SetValue(Border.HeightProperty, 15.0);
            ring.SetValue(Border.CornerRadiusProperty, new CornerRadius(7.5));
            ring.SetValue(Border.BorderThicknessProperty, new Thickness(1.5));
            ring.SetValue(Border.BorderBrushProperty, Brush("DimTextBrush"));
            ring.SetValue(Border.BackgroundProperty, Brush("RadioWellBrush"));
            ring.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            ring.SetValue(Border.MarginProperty, new Thickness(0, 1, 7, 0));   // +1 top settles it against the text optical center

            var dot = new FrameworkElementFactory(typeof(Border)) { Name = "dot" };
            dot.SetValue(Border.WidthProperty, 7.0);
            dot.SetValue(Border.HeightProperty, 7.0);
            dot.SetValue(Border.CornerRadiusProperty, new CornerRadius(3.5));
            dot.SetValue(Border.BackgroundProperty, Brush("RadioAccent"));
            dot.SetValue(Border.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            dot.SetValue(Border.VerticalAlignmentProperty, VerticalAlignment.Center);
            dot.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            ring.AppendChild(dot);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);

            sp.AppendChild(ring);
            sp.AppendChild(cp);

            var ct = new ControlTemplate(typeof(RadioButton)) { VisualTree = sp };
            var on = new Trigger { Property = ToggleButton.IsCheckedProperty, Value = true };
            on.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible) { TargetName = "dot" });
            on.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("RadioAccent")) { TargetName = "ring" });
            ct.Triggers.Add(on);
            var off = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            off.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4) { TargetName = "root" });
            ct.Triggers.Add(off);
            return ct;
        }

        // Themed single-line input, fully self-contained (templated from the theme brushes) so it renders
        // correctly in ANY window without depending on a window-scoped XAML style. Kills the OS-default
        // white box / blue focus + selection chrome.
        public static TextBox Field(double width = double.NaN)
        {
            var tb = new TextBox
            {
                FontFamily         = UiFont,
                FontSize           = 12,
                Background         = Brush("TextFieldBrush", Brush("BgCanvas")),
                Foreground         = Brush("TextBrush"),
                BorderBrush        = Brush("CardBorderBrush"),
                BorderThickness    = new Thickness(1),
                Padding            = new Thickness(6, 4, 6, 4),
                CaretBrush         = Brush("TextBrush"),
                SelectionBrush     = Brush("RowSelectedBrush"),
                SelectionTextBrush = Brush("TextBrush"),
                Template           = FieldTemplate()
            };
            if (!double.IsNaN(width)) tb.Width = width;
            return tb;
        }

        private static ControlTemplate FieldTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, RadControl);
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            sv.SetValue(Control.PaddingProperty, new Thickness(0));
            b.AppendChild(sv);
            root.AppendChild(b);

            var dark = new FrameworkElementFactory(typeof(Border));
            dark.SetValue(UIElement.IsHitTestVisibleProperty, false);
            dark.SetValue(Border.CornerRadiusProperty, RadControl);
            dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            dark.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenDarkThickness");
            root.AppendChild(dark);

            var light = new FrameworkElementFactory(typeof(Border));
            light.SetValue(UIElement.IsHitTestVisibleProperty, false);
            light.SetValue(Border.CornerRadiusProperty, RadControl);
            light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
            light.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenLightThickness");
            root.AppendChild(light);
            return new ControlTemplate(typeof(TextBox)) { VisualTree = root };
        }

        // Themed PasswordBox matching Field(): our border/fill, no OS white box or blue focus chrome.
        public static PasswordBox PasswordField(double width = double.NaN)
        {
            var pb = new PasswordBox
            {
                FontFamily      = UiFont,
                FontSize        = 12,
                Background      = Brush("TextFieldBrush", Brush("BgCanvas")),
                Foreground      = Brush("TextBrush"),
                BorderBrush     = Brush("CardBorderBrush"),
                BorderThickness = new Thickness(1),
                Padding         = new Thickness(6, 5, 6, 5),
                CaretBrush      = Brush("TextBrush"),
                Template        = PasswordFieldTemplate()
            };
            if (!double.IsNaN(width)) pb.Width = width;
            return pb;
        }

        private static ControlTemplate PasswordFieldTemplate()
        {
            var root = new FrameworkElementFactory(typeof(Grid));
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, RadControl);
            var sv = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
            sv.SetValue(Control.PaddingProperty, new Thickness(0));
            b.AppendChild(sv);
            root.AppendChild(b);

            var dark = new FrameworkElementFactory(typeof(Border));
            dark.SetValue(UIElement.IsHitTestVisibleProperty, false);
            dark.SetValue(Border.CornerRadiusProperty, RadControl);
            dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            dark.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenDarkThickness");
            root.AppendChild(dark);

            var light = new FrameworkElementFactory(typeof(Border));
            light.SetValue(UIElement.IsHitTestVisibleProperty, false);
            light.SetValue(Border.CornerRadiusProperty, RadControl);
            light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
            light.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenLightThickness");
            root.AppendChild(light);
            return new ControlTemplate(typeof(PasswordBox)) { VisualTree = root };
        }

        // Wraps a dialog's document/preview pane with the family drop shadow: a SEPARATE sibling
        // border underneath (content must never render through a bitmap effect or it loses
        // ClearType), carrying the per-theme PaneShadowEffect - which is null on 98SE, so the
        // classic theme stays flat. Dialogs read as mini main windows this way.
        public static Grid PaneWithShadow(Border pane)
        {
            // Code-built preview panes used to carry their own hard-coded radius, so 98SE could
            // never square them. The theme only supplies this override when it needs one.
            if (Application.Current?.TryFindResource("PaneCornerRadiusValue") is double radius)
                pane.CornerRadius = new CornerRadius(radius);

            var shadow = new Border
            {
                Margin = pane.Margin,
                CornerRadius = pane.CornerRadius,
                IsHitTestVisible = false,
            };
            shadow.SetResourceReference(Border.BackgroundProperty, "BgCanvas");
            shadow.SetResourceReference(UIElement.EffectProperty, "PaneShadowEffect");
            var host = new Grid();
            host.Children.Add(shadow);
            host.Children.Add(pane);

            // The main document pane uses these same four bevel rings. They are transparent and
            // zero-width on modern themes, while 98SE gets its square two-stage classic recess.
            var bevels = new Grid { Margin = pane.Margin, IsHitTestVisible = false };
            Border Ring(string brushKey, string thicknessKey, bool inner = false)
            {
                var ring = new Border { CornerRadius = pane.CornerRadius };
                ring.SetResourceReference(Border.BorderBrushProperty, brushKey);
                ring.SetResourceReference(Border.BorderThicknessProperty, thicknessKey);
                if (inner)
                    ring.SetResourceReference(FrameworkElement.MarginProperty, "PaneBevelInnerMargin");
                return ring;
            }
            bevels.Children.Add(Ring("PaneBevelDarkBrush", "PaneBevelLightThickness"));
            bevels.Children.Add(Ring("PaneBevelLightBrush", "PaneBevelDarkThickness"));
            bevels.Children.Add(Ring("PaneBevelDark2Brush", "PaneBevel2LightThickness", inner: true));
            bevels.Children.Add(Ring("PaneBevelLight2Brush", "PaneBevel2DarkThickness", inner: true));
            host.Children.Add(bevels);
            return host;
        }

        // A dialog section heading (e.g. "ROTATE", "PAGE NUMBERS").
        public static TextBlock SectionHeader(string text) => new()
        {
            Text       = text,
            FontFamily = MonoFont,
            FontSize   = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = Brush("TextBrush"),
            Margin     = new Thickness(0, 0, 0, 6),
            Effect     = ShadowText()
        };

        // A small secondary label sitting above/beside a field.
        public static TextBlock GroupLabel(string text) => new()
        {
            Text       = text,
            FontFamily = UiFont,
            FontSize   = 11,
            Foreground = Brush("MutedTextBrush"),
            Margin     = new Thickness(0, 0, 0, 2)
        };

        // Right-aligned row of dialog buttons with a consistent 8px gap. Pass buttons left-to-right.
        public static StackPanel ButtonRow(params Button[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            for (int i = 0; i < buttons.Length; i++)
            {
                if (i > 0) buttons[i].Margin = new Thickness(8, 0, 0, 0);
                row.Children.Add(buttons[i]);
            }
            return row;
        }

        // A flat text "link" (e.g. "Reset all") with an accent hover, for low-emphasis dialog actions.
        public static TextBlock LinkLabel(string text, Action onClick)
        {
            var link = new TextBlock
            {
                Text       = text,
                FontFamily = UiFont,
                FontSize   = 12,
                Foreground = Brush("MutedTextBrush"),
                Cursor     = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center
            };
            link.MouseEnter += (_, _2) => link.Foreground = Brush("PrimaryBrush");
            link.MouseLeave += (_, _2) => link.Foreground = Brush("MutedTextBrush");
            link.MouseLeftButtonUp += (_, _2) => onClick();
            return link;
        }

        // Dialog/popup buttons. accent==true is the primary (fills solid accent on hover); false is secondary.
        public static Button Make(object content, bool accent)
        {
            if (KillerPDF.Services.ThemeManager.Current == KillerPDF.Services.Theme.SE98)
            {
                var button = Make(content, Brush("ChipFaceBrush"), Brush("ChipFaceBrush"),
                                  Brush("TextBrush"), Brush("TextBrush"), Brushes.Transparent);
                // Keep an already-open code-built surface attached to the live palette. The
                // explicit-color factory below is also used by pre-theme startup dialogs, so the
                // resource references belong here in the normal themed overload.
                button.SetResourceReference(Control.BackgroundProperty, "ChipFaceBrush");
                button.SetResourceReference(Control.ForegroundProperty, "TextBrush");
                button.Template = BeveledButtonTemplate();
                return button;
            }
            var themed = accent
                ? Make(content, Brush("SelectionBg"), Brush("PrimaryBrush"), Brush("SelectionFg"), Brush("OnPrimaryBrush"), Brush("SelectionButtonBorderBrush"))
                : Make(content, Brush("PaneBrush"), Brush("SurfaceHoverBrush"), Brush("TextBrush"), Brush("TextBrush"), Brush("CardBorderBrush"));

            // Make(object,bool) is used by long-lived annotation bars and modeless tool windows.
            // A local brush value would preserve the palette that happened to be active when the
            // control was constructed. Re-attach each state to resource keys so theme and accent
            // changes repaint the existing button instead of leaving an old-colored island.
            void ApplyRest()
            {
                themed.SetResourceReference(Control.BackgroundProperty, accent ? "SelectionBg" : "PaneBrush");
                themed.SetResourceReference(Control.ForegroundProperty, accent ? "SelectionFg" : "TextBrush");
                themed.SetResourceReference(Control.BorderBrushProperty, accent ? "SelectionButtonBorderBrush" : "CardBorderBrush");
            }
            void ApplyHover()
            {
                themed.SetResourceReference(Control.BackgroundProperty, accent ? "PrimaryBrush" : "SurfaceHoverBrush");
                themed.SetResourceReference(Control.ForegroundProperty, accent ? "OnPrimaryBrush" : "TextBrush");
                themed.SetResourceReference(Control.BorderBrushProperty, accent ? "SelectionButtonBorderBrush" : "CardBorderBrush");
            }

            // These handlers are registered after the explicit-color factory's handlers, so the
            // resource-backed values win and remain live for the current palette.
            themed.MouseEnter += (_, _) => ApplyHover();
            themed.MouseLeave += (_, _) => ApplyRest();
            ApplyRest();
            return themed;
        }

        private static ControlTemplate BeveledButtonTemplate()
        {
            var grid = new FrameworkElementFactory(typeof(Grid));
            var face = new FrameworkElementFactory(typeof(Border)) { Name = "face" };
            face.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            face.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            face.AppendChild(cp);
            grid.AppendChild(face);
            var light = new FrameworkElementFactory(typeof(Border)) { Name = "light" };
            light.SetResourceReference(Border.BorderBrushProperty, "BevelLightBrush");
            light.SetResourceReference(Border.BorderThicknessProperty, "ButtonBevelLightThickness");
            grid.AppendChild(light);
            var dark = new FrameworkElementFactory(typeof(Border)) { Name = "dark" };
            dark.SetResourceReference(Border.BorderBrushProperty, "BevelDarkBrush");
            dark.SetResourceReference(Border.BorderThicknessProperty, "ButtonBevelDarkThickness");
            grid.AppendChild(dark);
            var template = new ControlTemplate(typeof(Button)) { VisualTree = grid };
            var pressed = new Trigger { Property = Button.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("BevelDarkBrush"), "light"));
            pressed.Setters.Add(new Setter(Border.BorderBrushProperty, Brush("BevelLightBrush"), "dark"));
            template.Triggers.Add(pressed);
            return template;
        }

        // Explicit-color overload for pre-theme windows (startup/crash/About). border==null = borderless.
        public static Button Make(object content, Brush normalBg, Brush hoverBg, Brush normalFg, Brush hoverFg, Brush? border = null)
        {
            var btn = new Button
            {
                Content = content,
                Padding = new Thickness(18, 6, 18, 6),
                Background = normalBg,
                Foreground = normalFg,
                BorderBrush = border ?? Brushes.Transparent,
                BorderThickness = new Thickness(border == null ? 0 : 1),
                Cursor = Cursors.Hand,
                FontFamily = UiFont,
                FontSize = 12,
                FocusVisualStyle = null,
                Template = ButtonTemplate(),
            };
            btn.MouseEnter += (_, _) => { btn.Background = hoverBg; btn.Foreground = hoverFg; };
            btn.MouseLeave += (_, _) => { btn.Background = normalBg; btn.Foreground = normalFg; };
            return btn;
        }

        internal static ControlTemplate ButtonTemplate()
        {
            var bf = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
            bf.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetValue(Border.CornerRadiusProperty, RadControl);

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);
            var ct = new ControlTemplate(typeof(Button)) { VisualTree = bf };
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45) { TargetName = "bd" });
            ct.Triggers.Add(dis);
            return ct;
        }
    }
}
