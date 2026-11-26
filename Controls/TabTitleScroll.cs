using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace KillerPDF.Controls
{
    /// <summary>
    /// A tab title too long for its tab (KillerPDF #528). The title fades out over its last few
    /// pixels instead of ending in an ellipsis, and while the pointer is on the tab it scrolls to its
    /// end so the whole name can be read, then slides back when the pointer leaves.
    /// </summary>
    /// <remarks>
    /// Set <c>HoverSource</c> on the title's clip, a ClipToBounds Border whose child is a horizontal
    /// StackPanel holding the title's TextBlock. The StackPanel lays the title out at its full
    /// length; arranged straight in the clip, WPF cuts the text at the clip's width and the scroll
    /// runs out of letters. HoverSource is the whole tab, so hovering the close button scrolls too.
    /// <code>
    /// &lt;Border ClipToBounds="True" local:TabTitleScroll.HoverSource="{Binding ElementName=tabBd}"&gt;
    ///     &lt;StackPanel Orientation="Horizontal"&gt;&lt;TextBlock Text="{Binding Title}"/&gt;&lt;/StackPanel&gt;
    /// &lt;/Border&gt;
    /// </code>
    /// </remarks>
    public static class TabTitleScroll
    {
        private const double FadeWidth = 18;
        private const double PixelsPerSecond = 40;

        public static readonly DependencyProperty HoverSourceProperty = DependencyProperty.RegisterAttached(
            "HoverSource", typeof(UIElement), typeof(TabTitleScroll), new PropertyMetadata(null, OnHoverSourceChanged));

        public static UIElement? GetHoverSource(DependencyObject element) => (UIElement?)element.GetValue(HoverSourceProperty);
        public static void SetHoverSource(DependencyObject element, UIElement? value) => element.SetValue(HoverSourceProperty, value);

        // The clip a hover source scrolls, kept on the source so its mouse handlers can find it.
        private static readonly DependencyProperty ClipProperty = DependencyProperty.RegisterAttached(
            "Clip", typeof(Border), typeof(TabTitleScroll), new PropertyMetadata(null));

        private static void OnHoverSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Border clip) return;
            if (e.OldValue is UIElement old)
            {
                old.MouseEnter -= OnEnter;
                old.MouseLeave -= OnLeave;
                old.ClearValue(ClipProperty);
            }
            clip.SizeChanged -= OnClipSizeChanged;
            if (e.NewValue is not UIElement source) return;
            source.SetValue(ClipProperty, clip);
            source.MouseEnter += OnEnter;
            source.MouseLeave += OnLeave;
            clip.SizeChanged += OnClipSizeChanged;
        }

        private static TextBlock? TitleIn(Border clip) => clip.Child switch
        {
            TextBlock title => title,
            Panel { Children.Count: > 0 } panel => panel.Children[0] as TextBlock,
            _ => null,
        };

        /// <summary>How much of the title the clip hides. The title is laid out at its full length.</summary>
        private static double Overflow(Border clip, TextBlock title) => Math.Max(0, title.ActualWidth - clip.ActualWidth);

        private static void OnClipSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (sender is not Border clip || TitleIn(clip) is not TextBlock title) return;
            UpdateFade(clip, title);
            if (GetHoverSource(clip) is not { IsMouseOver: true }) Reset(title, animate: false);
        }

        /// <summary>
        /// Fades the title's cut-off end: the right edge at rest, the left edge while it is scrolled
        /// to its end, so the last letters show in full and the hidden start fades instead.
        /// </summary>
        private static void UpdateFade(Border clip, TextBlock title, bool scrolled = false)
        {
            double width = clip.ActualWidth;
            if (width <= FadeWidth || Overflow(clip, title) <= 0)
            {
                clip.OpacityMask = null;
                return;
            }
            double edge = FadeWidth / width;
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(scrolled ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, scrolled ? edge : 1 - edge));
            mask.GradientStops.Add(new GradientStop(scrolled ? Colors.Black : Colors.Transparent, 1));
            mask.Freeze();
            clip.OpacityMask = mask;
        }

        private static void OnEnter(object sender, MouseEventArgs e)
        {
            if (sender is not UIElement source || source.GetValue(ClipProperty) is not Border clip || TitleIn(clip) is not TextBlock title) return;
            double overflow = Overflow(clip, title);
            if (overflow <= 0) return;
            // The scroll travels exactly the hidden width, so the name's last letter stops at the
            // clip's right edge. The fade moves to the left edge as the scroll starts.
            var slide = new DoubleAnimation(-overflow, TimeSpan.FromSeconds(overflow / PixelsPerSecond))
            {
                BeginTime = TimeSpan.FromMilliseconds(350),
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            slide.CurrentStateInvalidated += (s, _) =>
            {
                if (s is Clock { CurrentState: ClockState.Active } && source.IsMouseOver)
                    UpdateFade(clip, title, scrolled: true);
            };
            Shift(title).BeginAnimation(TranslateTransform.XProperty, slide);
        }

        private static void OnLeave(object sender, MouseEventArgs e)
        {
            if (sender is not UIElement source || source.GetValue(ClipProperty) is not Border clip || TitleIn(clip) is not TextBlock title) return;
            Reset(title, animate: true);
            UpdateFade(clip, title);
        }

        private static void Reset(TextBlock title, bool animate)
        {
            TranslateTransform shift = Shift(title);
            if (!animate || shift.X == 0)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.X = 0;
                return;
            }
            shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(250)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } });
        }

        private static TranslateTransform Shift(TextBlock title)
        {
            if (title.RenderTransform is TranslateTransform { IsFrozen: false } shift) return shift;
            var fresh = new TranslateTransform();
            title.RenderTransform = fresh;
            return fresh;
        }
    }
}
