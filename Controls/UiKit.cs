using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
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
        public static FontFamily BrandSansFont => Res("BrandSansFont", _brandSansFallback);
        /// <summary>Primary headings: Lacquer with Killer Sans UI behind it (ThemeManager; Killer Sans on 98SE).</summary>
        public static FontFamily HeadingFont => Res("HeadingFont", LacquerHeadingStack);
        /// <summary>Killer Sans UI (Killer Sans scaled to Segoe UI proportions) with the Segoe fallback stack.</summary>
        public static readonly FontFamily KillerSansUiStack = new(new Uri($"pack://application:,,,/{typeof(UiKit).Assembly.GetName().Name};component/"), "./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        public static readonly FontFamily LacquerHeadingStack = new(new Uri("pack://application:,,,/"), "./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        private static readonly FontFamily _uiFallback   = new("Segoe UI, Microsoft JhengHei UI, Nirmala UI");
        private static readonly FontFamily _monoFallback = new("Consolas");
        private static readonly FontFamily _iconFallback = new("Segoe MDL2 Assets");
        private static readonly FontFamily _wordmarkFallback = new("Punktype, Consolas");
        private static readonly FontFamily _wordmarkPdfFallback = new("Lacquer, Consolas");
        private static readonly FontFamily _brandSansFallback = new("Killer Sans, Consolas");

        public static CornerRadius RadControl => Rad("RadControl", 4);
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

        // The themed combo box style lives in the family Controls.xaml, merged app-wide.
        private static Style? _darkComboStyle;
        public static Style? DarkComboStyle => _darkComboStyle ??= PickerStyle("DarkComboBox");

        private static Style? PickerStyle(string key)
            => Application.Current?.TryFindResource(key) as Style;

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
            var root = new FrameworkElementFactory(typeof(Grid)) { Name = "InputRoot" };
            root.SetResourceReference(FrameworkElement.TagProperty, "UseDialogCaption");
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, RadControl);
            var sv = new FrameworkElementFactory(typeof(KillerFamily.Controls.InputContentHost)) { Name = "PART_ContentHost" };
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
            var innerDark = new FrameworkElementFactory(typeof(Border));
            innerDark.SetValue(UIElement.IsHitTestVisibleProperty, false);
            innerDark.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            innerDark.SetResourceReference(Border.BorderBrushProperty, "FrameOuterDarkBrush");
            innerDark.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenDarkThickness");
            root.AppendChild(innerDark);

            var innerLight = new FrameworkElementFactory(typeof(Border));
            innerLight.SetValue(UIElement.IsHitTestVisibleProperty, false);
            innerLight.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            innerLight.SetResourceReference(Border.BorderBrushProperty, "FrameOuterLightBrush");
            innerLight.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenLightThickness");
            root.AppendChild(innerLight);
            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = root };
            AddCompactInputTrigger(template);
            return template;
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
            var root = new FrameworkElementFactory(typeof(Grid)) { Name = "InputRoot" };
            root.SetResourceReference(FrameworkElement.TagProperty, "UseDialogCaption");
            var b = new FrameworkElementFactory(typeof(Border));
            b.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            b.SetValue(Border.CornerRadiusProperty, RadControl);
            var sv = new FrameworkElementFactory(typeof(KillerFamily.Controls.InputContentHost)) { Name = "PART_ContentHost" };
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
            var innerDark = new FrameworkElementFactory(typeof(Border));
            innerDark.SetValue(UIElement.IsHitTestVisibleProperty, false);
            innerDark.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            innerDark.SetResourceReference(Border.BorderBrushProperty, "FrameOuterDarkBrush");
            innerDark.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenDarkThickness");
            root.AppendChild(innerDark);

            var innerLight = new FrameworkElementFactory(typeof(Border));
            innerLight.SetValue(UIElement.IsHitTestVisibleProperty, false);
            innerLight.SetValue(FrameworkElement.MarginProperty, new Thickness(1));
            innerLight.SetResourceReference(Border.BorderBrushProperty, "FrameOuterLightBrush");
            innerLight.SetResourceReference(Border.BorderThicknessProperty, "CheckSunkenLightThickness");
            root.AppendChild(innerLight);
            var template = new ControlTemplate(typeof(PasswordBox)) { VisualTree = root };
            AddCompactInputTrigger(template);
            return template;
        }

        private static void AddCompactInputTrigger(ControlTemplate template)
        {
            var compact = new MultiDataTrigger();
            compact.Conditions.Add(new Condition(new Binding("Tag") { ElementName = "InputRoot" }, true));
            if (template.TargetType == typeof(TextBox))
                compact.Conditions.Add(new Condition(new Binding("AcceptsReturn")
                    { RelativeSource = new RelativeSource(RelativeSourceMode.Self) }, false));
            compact.Setters.Add(new Setter(FrameworkElement.MinHeightProperty,
                new DynamicResourceExtension("CompactInputMinHeight")) { TargetName = "InputRoot" });
            compact.Setters.Add(new Setter(KillerFamily.Controls.InputContentHost.CompactPaddingProperty,
                new DynamicResourceExtension("CompactInputPadding")) { TargetName = "PART_ContentHost" });
            compact.Setters.Add(new Setter(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center)
                { TargetName = "PART_ContentHost" });
            template.Triggers.Add(compact);
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

        // Dialog/popup buttons. accent==true is the primary: hollow in the accent at rest, filled with the
        // family button gradient on hover, the same as OutlineButton. false is secondary.
        public static Button Make(object content, bool accent)
            => new ThemedButton(content, accent);

        private sealed class ThemedButton : Button
        {
            private readonly bool _accent;
            private static readonly DependencyProperty ClassicProperty = DependencyProperty.Register(
                "Classic", typeof(bool), typeof(ThemedButton), new PropertyMetadata(false, RefreshTheme));
            private static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
                "Radius", typeof(CornerRadius), typeof(ThemedButton), new PropertyMetadata(new CornerRadius(4), RefreshTheme));
            private bool Classic => (bool)GetValue(ClassicProperty);

            internal ThemedButton(object content, bool accent)
            {
                _accent = accent;
                Content = content;
                Padding = new Thickness(18, 6, 18, 6);
                BorderThickness = new Thickness(1);
                Cursor = Cursors.Hand;
                FontSize = 12;
                FocusVisualStyle = null;
                SetResourceReference(FontFamilyProperty, "UiFont");
                SetResourceReference(ClassicProperty, "UseDialogCaption");
                SetResourceReference(RadiusProperty, "RadControl");
                MouseEnter += (_, _) => ApplyBrushes();
                MouseLeave += (_, _) => ApplyBrushes();
                Refresh();
            }

            private static void RefreshTheme(DependencyObject target, DependencyPropertyChangedEventArgs _)
                => ((ThemedButton)target).Refresh();

            private void Refresh()
            {
                // Keep the same action control while its decorative template follows live
                // classic/radius resources. No application-wide theme subscription retains it.
                Template = Classic ? BeveledButtonTemplate()
                    : ButtonTemplate(outlined: !_accent, radius: (CornerRadius)GetValue(RadiusProperty));
                ApplyBrushes();
            }

            private void ApplyBrushes()
            {
                if (Classic)
                {
                    SetResourceReference(BackgroundProperty, "ChipFaceBrush");
                    SetResourceReference(ForegroundProperty, "TextBrush");
                    BorderBrush = Brushes.Transparent;
                    return;
                }
                bool hover = IsMouseOver;
                SetResourceReference(BackgroundProperty, _accent
                    ? hover ? "OutlineHoverBrush" : "OutlineFaceBrush" : hover ? "SurfaceHoverBrush" : "PaneBrush");
                SetResourceReference(ForegroundProperty, _accent
                    ? hover ? "OutlineHoverTextBrush" : "OutlineTextBrush" : "TextBrush");
                SetResourceReference(BorderBrushProperty, _accent
                    ? hover ? "OutlineHoverBrush" : "OutlineRestBrush" : "CardBorderBrush");
            }
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
                Template = ButtonTemplate(outlined: border != null),
            };
            btn.MouseEnter += (_, _) => { btn.Background = hoverBg; btn.Foreground = hoverFg; };
            btn.MouseLeave += (_, _) => { btn.Background = normalBg; btn.Foreground = normalFg; };
            return btn;
        }

        // outlined: the edge is drawn as an outer fill with the face inset by BorderThickness, not as a
        // Border stroke. A 1px stroke on a small radius lets the antialiased face show through at
        // each corner, which reads as a nick in the outline.
        internal static ControlTemplate ButtonTemplate(bool outlined = false, CornerRadius? radius = null)
        {
            var bf = new FrameworkElementFactory(typeof(Border)) { Name = "bd" };
            bf.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
            bf.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });

            var cp = new FrameworkElementFactory(typeof(ContentPresenter));
            cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            cp.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
            bf.AppendChild(cp);

            FrameworkElementFactory root = bf;
            if (outlined)
            {
                CornerRadius r = radius ?? RadControl;
                bf.SetValue(Border.CornerRadiusProperty, new CornerRadius(
                    Math.Max(0, r.TopLeft - 1), Math.Max(0, r.TopRight - 1),
                    Math.Max(0, r.BottomRight - 1), Math.Max(0, r.BottomLeft - 1)));
                root = new FrameworkElementFactory(typeof(Border)) { Name = "edge" };
                root.SetBinding(Border.BackgroundProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                root.SetBinding(Border.PaddingProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                root.SetValue(Border.CornerRadiusProperty, r);
                root.AppendChild(bf);
            }
            else
            {
                bf.SetBinding(Border.BorderBrushProperty, new Binding("BorderBrush") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                bf.SetBinding(Border.BorderThicknessProperty, new Binding("BorderThickness") { RelativeSource = new RelativeSource(RelativeSourceMode.TemplatedParent) });
                bf.SetValue(Border.CornerRadiusProperty, radius ?? RadControl);
            }

            // Buttons are flat (KillerUI 5e8cdd9): no lift shadow under the face.
            var ct = new ControlTemplate(typeof(Button)) { VisualTree = root };
            var dis = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            dis.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45) { TargetName = outlined ? "edge" : "bd" });
            ct.Triggers.Add(dis);
            return ct;
        }
    }
}
