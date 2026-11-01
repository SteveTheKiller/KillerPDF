#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;

namespace KillerFamily.Accessibility
{
    internal static class AccessibleControls
    {
        private static bool _initialized;
        private static readonly IValueConverter TooltipName = new TooltipNameConverter();
        private static readonly IMultiValueConverter RunsName = new RunsNameConverter();
        private static Style? _focusStyle;
        private static Style? _compactFocusStyle;
        private static DataTemplate? _glyphTemplate;
        private static readonly DependencyProperty OwnedNameProperty = DependencyProperty.RegisterAttached(
            "OwnedName", typeof(BindingBase), typeof(AccessibleControls));
        private static readonly DependencyProperty TrackedTooltipProperty = DependencyProperty.RegisterAttached(
            "TrackedTooltip", typeof(bool), typeof(AccessibleControls), new PropertyMetadata(false));
        private static readonly DependencyProperty TrackedLoadProperty = DependencyProperty.RegisterAttached(
            "TrackedLoad", typeof(bool), typeof(AccessibleControls), new PropertyMetadata(false));
        private static readonly DependencyPropertyDescriptor TooltipDescriptor =
            DependencyPropertyDescriptor.FromProperty(FrameworkElement.ToolTipProperty, typeof(Control));

        internal static void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            HighContrastResources.Initialize();
            EventManager.RegisterClassHandler(typeof(Control), FrameworkElement.SizeChangedEvent,
                new SizeChangedEventHandler(OnControlSizeChanged), true);
            // WPF still gives Tab focus to a hyperlink whose TextBlock is collapsed: an invisible
            // stop that a screen reader announces as the window. Focus moves on past it instead.
            EventManager.RegisterClassHandler(typeof(System.Windows.Documents.Hyperlink),
                System.Windows.Input.Keyboard.PreviewGotKeyboardFocusEvent,
                new System.Windows.Input.KeyboardFocusChangedEventHandler(OnHyperlinkPreviewGotFocus));
            // An element with no size still reports itself on screen, which Accessibility Insights
            // fails as a null bounding rectangle: a scroll bar's page button at the end of its
            // track, an arrow button a theme hides, an empty status line. FromClip has UI
            // Automation report an element with no visible area as off screen.
            foreach (var type in new[] { typeof(TextBlock), typeof(RepeatButton) })
            {
                try
                {
                    AutomationProperties.IsOffscreenBehaviorProperty.OverrideMetadata(type,
                        new FrameworkPropertyMetadata(IsOffscreenBehavior.FromClip));
                }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            }
        }

        private static void PrepareHyperlink(Hyperlink link)
        {
            var source = DependencyPropertyHelper.GetValueSource(link, FrameworkContentElement.FocusVisualStyleProperty);
            if (link.FocusVisualStyle == null || source.BaseValueSource is BaseValueSource.Default or BaseValueSource.DefaultStyle)
                link.SetCurrentValue(FrameworkContentElement.FocusVisualStyleProperty, CompactFocusStyle);
        }

        private static void OnHyperlinkPreviewGotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args)
        {
            if (sender is not System.Windows.Documents.Hyperlink link || !ReferenceEquals(args.NewFocus, link)) return;
            PrepareHyperlink(link);
            DependencyObject? host = link.Parent;
            while (host is FrameworkContentElement content) host = content.Parent;
            if (host is not UIElement element || element.IsVisible) return;
            args.Handled = true;
            var direction = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0
                ? System.Windows.Input.FocusNavigationDirection.Previous
                : System.Windows.Input.FocusNavigationDirection.Next;
            link.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => link.MoveFocus(new System.Windows.Input.TraversalRequest(direction))));
        }

        // Tab into a radio group lands on its selected option, as in Windows, rather than on the
        // option that last had focus or the first one. Only for the keyboard: a click focuses the
        // option it lands on.
        private static void OnRadioGroupPreviewGotFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs args)
        {
            if (sender is not Panel group || args.NewFocus is not RadioButton target || target.IsChecked == true) return;
            if (args.OldFocus is Visual old && group.IsAncestorOf(old)) return;
            if (System.Windows.Input.InputManager.Current.MostRecentInputDevice is not System.Windows.Input.KeyboardDevice) return;
            var selected = group.Children.OfType<RadioButton>()
                .FirstOrDefault(option => option.IsChecked == true && option.IsEnabled && option.IsVisible);
            if (selected == null) return;
            args.Handled = true;
            selected.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                new Action(() => selected.Focus()));
        }

        private static void OnControlSizeChanged(object sender, SizeChangedEventArgs args)
        {
            if (sender is not Control control) return;
            // A class Loaded handler alone does not make WPF broadcast Loaded to every child.
            // The first layout attaches an instance handler, including for later popup content.
            if (!(bool)control.GetValue(TrackedLoadProperty))
            {
                control.Loaded += OnControlLoaded;
                control.SetValue(TrackedLoadProperty, true);
            }
            Prepare(control);
        }

        private static void OnControlLoaded(object sender, RoutedEventArgs args)
        {
            if (sender is not Control control) return;
            Prepare(control);
            if (control is Window window)
                window.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle,
                    new Action(() => AccessibilityAudit.Record(window)));
        }

        internal static void Prepare(Control control)
        {
            if (control is Window) HighContrastResources.Initialize();
            else HighContrastResources.PrepareCaption(control);
            // The focus adorner is shown by WPF for keyboard navigation, without changing layout.
            var focusSource = DependencyPropertyHelper.GetValueSource(control, FrameworkElement.FocusVisualStyleProperty);
            if (control is not Window && control.Focusable &&
                (control.FocusVisualStyle == null || focusSource.BaseValueSource is BaseValueSource.Default or BaseValueSource.DefaultStyle))
                control.SetCurrentValue(Control.FocusVisualStyleProperty, IsCompact(control) ? CompactFocusStyle : FocusStyle);

            // A group of radio buttons is one Tab stop, as everywhere else in Windows, and the arrow
            // keys move between its options. Menus and flyouts, which are not hosted in the window
            // itself, keep their own keyboard handling.
            if (control is RadioButton radio && VisualTreeHelper.GetParent(radio) is Panel group &&
                PresentationSource.FromVisual(radio)?.RootVisual is Window && group.Children.Count > 1 &&
                group.ReadLocalValue(System.Windows.Input.KeyboardNavigation.TabNavigationProperty) == DependencyProperty.UnsetValue &&
                group.Children.Cast<UIElement>().All(child => child is RadioButton option && option.GroupName == radio.GroupName))
            {
                System.Windows.Input.KeyboardNavigation.SetTabNavigation(group, System.Windows.Input.KeyboardNavigationMode.Once);
                System.Windows.Input.KeyboardNavigation.SetDirectionalNavigation(group, System.Windows.Input.KeyboardNavigationMode.Cycle);
                group.PreviewGotKeyboardFocus += OnRadioGroupPreviewGotFocus;
            }

            // An icon given as string content ("&#xE8BB;") is drawn by the content presenter's
            // own TextBlock, which UI Automation reads out as a private-use character. Drawing it
            // through a Glyph keeps the same text on screen and leaves the name to the control.
            // A selector rather than a template: when the content later becomes anything else
            // (a toolbar swapping in an icon and label panel), the presenter's default applies.
            if (control is ContentControl glyphHost && glyphHost.Content is string glyphText &&
                IsGlyph(glyphText) && glyphHost.ContentTemplate == null && glyphHost.ContentTemplateSelector == null)
                glyphHost.SetCurrentValue(ContentControl.ContentTemplateSelectorProperty, GlyphTemplates);

            UpdateName(control);
        }

        private static void UpdateName(Control control)
        {
            var existing = BindingOperations.GetBindingBase(control, AutomationProperties.NameProperty);
            bool owned = existing != null && ReferenceEquals(existing, control.GetValue(OwnedNameProperty));
            if (!owned && IsReadable(AutomationProperties.GetName(control))) return;
            object? content = control is ContentControl button ? button.Content :
                control is HeaderedItemsControl items ? items.Header :
                control is HeaderedContentControl header ? header.Header : null;
            if (!(bool)control.GetValue(TrackedTooltipProperty))
            {
                TooltipDescriptor.AddValueChanged(control, OnTooltipChanged);
                control.Unloaded += OnControlUnloaded;
                control.SetValue(TrackedTooltipProperty, true);
            }
            if (IsReadable(ReadText(content)))
            {
                // WPF names a control from its tooltip before its content, and a rich tooltip
                // names it after its own type ("System.Windows.Controls.TextBlock"). With such a
                // tooltip, the readable content has to be given as the name explicitly.
                if (control.ToolTip == null || control.ToolTip is string)
                {
                    if (owned) BindingOperations.ClearBinding(control, AutomationProperties.NameProperty);
                    control.ClearValue(OwnedNameProperty);
                    return;
                }
                var contentName = new Binding(control is ContentControl ? nameof(ContentControl.Content)
                    : nameof(HeaderedItemsControl.Header)) { Source = control, Converter = TooltipName };
                BindingOperations.SetBinding(control, AutomationProperties.NameProperty, contentName);
                control.SetValue(OwnedNameProperty, contentName);
                return;
            }
            if (!IsReadable(ReadText(control.ToolTip)))
            {
                if (owned) BindingOperations.ClearBinding(control, AutomationProperties.NameProperty);
                control.ClearValue(OwnedNameProperty);
                return;
            }

            // A rich tooltip's localized TextBlock can change without replacing the tooltip. A
            // TextBlock built from Runs keeps its words in the Runs, so the name follows each one.
            TextBlock? label = FindTextBlock(control.ToolTip);
            BindingBase binding;
            if (label != null && string.IsNullOrWhiteSpace(label.Text) && RunsOf(label.Inlines).Any())
            {
                var joined = new MultiBinding { Converter = RunsName };
                foreach (Run run in RunsOf(label.Inlines))
                    joined.Bindings.Add(new Binding(nameof(Run.Text)) { Source = run });
                binding = joined;
            }
            else if (label != null)
                binding = new Binding(nameof(TextBlock.Text)) { Source = label };
            else
                binding = new Binding(nameof(FrameworkElement.ToolTip)) { Source = control, Converter = TooltipName };
            BindingOperations.SetBinding(control, AutomationProperties.NameProperty, binding);
            control.SetValue(OwnedNameProperty, binding);
        }

        private static TextBlock? FindTextBlock(object? value)
        {
            if (value is TextBlock block && IsReadable(BlockText(block))) return block;
            if (value is ContentControl content) return FindTextBlock(content.Content);
            if (value is Panel panel)
                foreach (UIElement child in panel.Children)
                {
                    TextBlock? found = FindTextBlock(child);
                    if (found != null) return found;
                }
            return null;
        }

        private static void OnTooltipChanged(object? sender, EventArgs args)
        {
            if (sender is Control control) UpdateName(control);
        }

        private static void OnControlUnloaded(object sender, RoutedEventArgs args)
        {
            if (sender is not Control control) return;
            TooltipDescriptor.RemoveValueChanged(control, OnTooltipChanged);
            control.Unloaded -= OnControlUnloaded;
            control.SetValue(TrackedTooltipProperty, false);
        }

        internal static string ReadText(object? value)
        {
            if (value is string text) return text.Trim();
            if (value is TextBlock block) return BlockText(block).Trim();
            if (value is ContentControl content) return ReadText(content.Content);
            if (value is Panel panel)
                foreach (UIElement child in panel.Children)
                {
                    string label = ReadText(child);
                    if (IsReadable(label)) return label;
                }
            return string.Empty;
        }

        /// <summary>A TextBlock's words, including those it holds in Runs rather than in Text.</summary>
        private static string BlockText(TextBlock block)
        {
            if (!string.IsNullOrWhiteSpace(block.Text)) return block.Text;
            return string.Concat(RunsOf(block.Inlines).Select(run => run.Text));
        }

        private static IEnumerable<Run> RunsOf(InlineCollection inlines)
        {
            foreach (Inline inline in inlines)
            {
                if (inline is Run run) yield return run;
                else if (inline is Span span)
                    foreach (Run inner in RunsOf(span.Inlines)) yield return inner;
            }
        }

        private static bool IsReadable(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.StartsWith("System.Windows.", StringComparison.Ordinal))
                return false;
            foreach (char character in text)
                if (char.IsLetterOrDigit(character) &&
                    CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.PrivateUse)
                    return true;
            return false;
        }

        private static bool IsGlyph(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            foreach (char character in text)
                if (!char.IsWhiteSpace(character) &&
                    CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.PrivateUse)
                    return false;
            return true;
        }

        private static readonly DataTemplateSelector GlyphTemplates = new GlyphTemplateSelector();

        private static DataTemplate GlyphTemplate => _glyphTemplate ??= BuildGlyphTemplate();

        private static DataTemplate BuildGlyphTemplate()
        {
            var glyph = new FrameworkElementFactory(typeof(Glyph));
            glyph.SetBinding(TextBlock.TextProperty, new Binding());
            var template = new DataTemplate { VisualTree = glyph };
            template.Seal();
            return template;
        }

        private static Style FocusStyle => _focusStyle ??= BuildFocusStyle(2);

        /// <summary>
        /// Icon-sized controls such as rail buttons have no room inside for the outline, which
        /// then lands on the glyph itself. Theirs is drawn just outside the control instead.
        /// </summary>
        private static Style CompactFocusStyle => _compactFocusStyle ??= BuildFocusStyle(-3);

        private static bool IsCompact(FrameworkElement control) =>
            control.ActualWidth > 0 && control.ActualHeight > 0 &&
            (control.ActualWidth < CompactSize || control.ActualHeight < CompactSize);

        /// <summary>The family focus ring for a focusable element that is not a Control, such as a
        /// code-built appointment chip. Controls receive theirs automatically.</summary>
        internal static Style FocusStyleFor(FrameworkElement element) =>
            IsCompact(element) ? CompactFocusStyle : FocusStyle;

        private const double CompactSize = 21;

        private static Style BuildFocusStyle(double inset)
        {
            var ring = new FrameworkElementFactory(typeof(FocusRing));
            ring.SetValue(FrameworkElement.MarginProperty, new Thickness(inset));
            ring.SetValue(FocusRing.InsetProperty, inset);
            var template = new ControlTemplate(typeof(Control)) { VisualTree = ring };
            var style = new Style(typeof(Control));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        private sealed class GlyphTemplateSelector : DataTemplateSelector
        {
            public override DataTemplate? SelectTemplate(object? item, DependencyObject container) =>
                item is string text && IsGlyph(text) ? GlyphTemplate : null;
        }

        private sealed class TooltipNameConverter : IValueConverter
        {
            public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
                ReadText(value);

            public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
                Binding.DoNothing;
        }

        private sealed class RunsNameConverter : IMultiValueConverter
        {
            public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
                string.Concat(values.OfType<string>()).Trim();

            public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
                [];
        }
    }

    /// <summary>
    /// The keyboard focus outline: a thin dotted ring in the theme accent, one pixel on and one
    /// off, rounded like the controls. When the accent is too faint against the window surfaces
    /// to read as an outline, the ring uses the text color instead, and High Contrast uses the
    /// system highlight. WPF builds a new outline each time focus moves, so it follows theme
    /// changes.
    /// </summary>
    internal sealed class FocusRing : FrameworkElement
    {
        internal static readonly DependencyProperty InsetProperty = DependencyProperty.Register(
            nameof(Inset), typeof(double), typeof(FocusRing), new PropertyMetadata(0.0));

        private const double MinimumContrast = 3;
        private double _radius;

        public FocusRing()
        {
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            IsHitTestVisible = false;
            Loaded += (_, _) => Apply();
        }

        internal double Inset
        {
            get => (double)GetValue(InsetProperty);
            set => SetValue(InsetProperty, value);
        }

        /// <summary>The ring's color, chosen when it loads.</summary>
        internal Brush? Stroke { get; private set; }

        internal double StrokeThickness => 1;

        /// <summary>One pixel on, one pixel off.</summary>
        internal DoubleCollection DashArray { get; } = new DoubleCollection { 1, 1 };

        private void Apply()
        {
            Stroke = RingBrush();
            double radius = TryFindResource("ControlCornerRadius") is CornerRadius corner ? corner.TopLeft : 0;
            // An outline inside the control sits on a smaller curve, one outside it on a larger one.
            _radius = radius <= 0 ? 0 : Math.Max(0, radius - Inset);
            InvalidateVisual();
        }

        // The dots are placed on whole device pixels rather than drawn with a dashed pen, whose
        // dashes fall between pixels and smear into a faint solid line. A rounded ring leaves out
        // its corner pixels.
        protected override void OnRender(DrawingContext dc)
        {
            if (Stroke == null) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            double px = StrokeThickness / dpi.DpiScaleX, py = StrokeThickness / dpi.DpiScaleY;
            int w = (int)Math.Round(ActualWidth / px), h = (int)Math.Round(ActualHeight / py);
            if (w < 3 || h < 3) return;
            int skip = _radius > 0 ? 1 : 0;
            int step = (int)(DashArray[0] + DashArray[1]);
            for (int x = skip; x < w - skip; x += step)
            {
                dc.DrawRectangle(Stroke, null, new Rect(x * px, 0, px, py));
                dc.DrawRectangle(Stroke, null, new Rect(x * px, (h - 1) * py, px, py));
            }
            for (int y = 1 + skip + 1; y < h - 1 - skip; y += step)
            {
                dc.DrawRectangle(Stroke, null, new Rect(0, y * py, px, py));
                dc.DrawRectangle(Stroke, null, new Rect((w - 1) * px, y * py, px, py));
            }
        }

        private Brush RingBrush()
        {
            if (SystemParameters.HighContrast) return SystemColors.HighlightBrush;
            Brush? text = TryFindResource("TextBrush") as Brush;
            if (TryFindResource("PrimaryBrush") is SolidColorBrush accent)
            {
                var surfaces = new[] { "BackgroundBrush", "PaneBrush" }
                    .SelectMany(key => SurfaceColors(TryFindResource(key) as Brush)).ToList();
                if (surfaces.All(surface => Contrast(Over(accent.Color, surface), surface) >= MinimumContrast))
                    return accent;
            }
            return text ?? SystemColors.WindowTextBrush;
        }

        private static IEnumerable<Color> SurfaceColors(Brush? brush) => brush switch
        {
            SolidColorBrush solid => new[] { solid.Color },
            GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color),
            _ => Enumerable.Empty<Color>(),
        };

        private static Color Over(Color top, Color bottom)
        {
            double alpha = top.A / 255.0;
            byte Mix(byte a, byte b) => (byte)Math.Round(a * alpha + b * (1 - alpha));
            return Color.FromRgb(Mix(top.R, bottom.R), Mix(top.G, bottom.G), Mix(top.B, bottom.B));
        }

        private static double Contrast(Color a, Color b)
        {
            double la = Luminance(a), lb = Luminance(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        private static double Luminance(Color color)
        {
            double Channel(byte value)
            {
                double c = value / 255.0;
                return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
            }
            return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        }
    }

    /// <summary>
    /// An icon drawn from a symbol font. The control it decorates carries the name, so the
    /// glyph stays out of the automation tree instead of being read as a private-use character.
    /// </summary>
    public sealed class Glyph : TextBlock
    {
        protected override void OnInitialized(EventArgs e)
        {
            base.OnInitialized(e);
            // A subclass misses the implicit TextBlock style in scope, so it is referenced here.
            // A TextBlock created by a template does not take implicit styles from outside it, and
            // an explicit Style wins, so in both cases the glyph is left exactly as a TextBlock is.
            if (TemplatedParent == null && ReadLocalValue(StyleProperty) == DependencyProperty.UnsetValue)
                SetResourceReference(StyleProperty, typeof(TextBlock));
        }

        protected override AutomationPeer OnCreateAutomationPeer() => new GlyphAutomationPeer(this);

        private sealed class GlyphAutomationPeer : TextBlockAutomationPeer
        {
            internal GlyphAutomationPeer(TextBlock owner) : base(owner) { }

            protected override bool IsControlElementCore() => false;

            protected override bool IsContentElementCore() => false;
        }
    }
}
