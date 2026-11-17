using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>Which motif an element paints when the theme has one (Trash Polka).</summary>
    internal enum MotifPart { None, Window, Chrome, Split }

    /// <summary>
    /// Paints Trash Polka's motif into an app's own layout. An app marks its window root <c>Window</c>, each raised
    /// chrome pane (title bar, toolbar, sidebar) <c>Chrome</c>, and the second pane in split view <c>Split</c>, and
    /// names the brush key the element normally uses:
    /// <code>&lt;Border c:ThemeMotif.Part="Chrome" c:ThemeMotif.BaseBrush="PaneBrush"/&gt;</code>
    /// While the theme publishes <c>ThemeMotif</c> = true, the element paints its base color with its part of the motif:
    /// the window the full sleeve, the chrome panes ONE continuous piece mapped to the window so the bars and sidebar
    /// join up, the split pane its own corner cluster. Under every other theme the element keeps its base brush.
    /// <c>MotifBrush</c> names a different brush key to paint under the motif, for an element whose everyday brush is
    /// ink or transparent but which is a charcoal card under Trash Polka; <c>BaseBrush</c> is still what it restores.
    /// A <c>Window</c> element anywhere in the window shows its own stretch of the one sleeve, so a document card
    /// lines up with the window around it.
    /// </summary>
    internal static class ThemeMotif
    {
        public static readonly DependencyProperty PartProperty = DependencyProperty.RegisterAttached(
            "Part", typeof(MotifPart), typeof(ThemeMotif), new PropertyMetadata(MotifPart.None, OnPartChanged));

        public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.RegisterAttached(
            "BaseBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata("BackgroundBrush"));

        private static readonly DependencyProperty PaintedProperty = DependencyProperty.RegisterAttached(
            "Painted", typeof(string), typeof(ThemeMotif), new PropertyMetadata(null));

        public static MotifPart GetPart(DependencyObject element) => (MotifPart)element.GetValue(PartProperty);
        public static void SetPart(DependencyObject element, MotifPart value) => element.SetValue(PartProperty, value);
        public static string GetBaseBrush(DependencyObject element) => (string)element.GetValue(BaseBrushProperty);
        public static void SetBaseBrush(DependencyObject element, string value) => element.SetValue(BaseBrushProperty, value);

        public static readonly DependencyProperty MotifBrushProperty = DependencyProperty.RegisterAttached(
            "MotifBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata(null));

        public static string? GetMotifBrush(DependencyObject element) => (string?)element.GetValue(MotifBrushProperty);
        public static void SetMotifBrush(DependencyObject element, string? value) => element.SetValue(MotifBrushProperty, value);

        private static readonly Dictionary<MotifPart, (Color Accent, Size Size, Drawing Piece)> Pieces = [];

        private static void OnPartChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element) return;
            element.Loaded -= Refresh;
            element.SizeChanged -= Refresh;
            element.Unloaded -= Detach;
            if ((MotifPart)e.NewValue == MotifPart.None) { Restore(element); return; }
            element.Loaded += Refresh;
            element.SizeChanged += Refresh;
            element.Unloaded += Detach;
            ThemeManager.ThemeChanged -= Rebind(element);
            ThemeManager.ThemeChanged += Rebind(element);
            WatchBackground(element);
            if (element.IsLoaded) Paint(element);
        }

        // Apps restyle their own panes (KillerPDF resets the document card's Background when a pane takes focus), which
        // would wipe the motif and leave the stamp claiming it is still there. Any outside Background change while the
        // motif is painted repaints it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> Watched = new();

        private static void WatchBackground(FrameworkElement element)
        {
            if (BackgroundProperty(element) is not { } property || Watched.TryGetValue(element, out _)) return;
            Watched.Add(element, property);
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(property, element.GetType())
                ?.AddValueChanged(element, OnBackgroundChanged);
        }

        private static void OnBackgroundChanged(object? sender, EventArgs e)
        {
            if (sender is not FrameworkElement element || element.GetValue(PaintedProperty) is null) return;
            if (BackgroundProperty(element) is { } property && ReferenceEquals(element.GetValue(property), element.GetValue(PaintedBrushProperty))) return;
            element.ClearValue(PaintedProperty);
            Paint(element);
        }

        private static DependencyProperty? BackgroundProperty(FrameworkElement element) => element switch
        {
            Border => Border.BackgroundProperty,
            Panel => Panel.BackgroundProperty,
            Control => Control.BackgroundProperty,
            _ => null,
        };

        private static readonly DependencyProperty PaintedBrushProperty = DependencyProperty.RegisterAttached(
            "PaintedBrush", typeof(Brush), typeof(ThemeMotif), new PropertyMetadata(null));

        // One handler per element, kept so Detach can remove exactly it.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, Action> Handlers = new();

        private static Action Rebind(FrameworkElement element)
        {
            var handler = Handlers.GetValue(element, e => () => Paint(e));
            return handler;
        }

        private static void Detach(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && Handlers.TryGetValue(element, out var handler))
                ThemeManager.ThemeChanged -= handler;
        }

        private static void Refresh(object sender, EventArgs e)
        {
            if (sender is FrameworkElement element) Paint(element);
        }

        private static void Paint(FrameworkElement element)
        {
            var part = GetPart(element);
            if (part == MotifPart.None || element.TryFindResource("ThemeMotif") is not true
                || element.TryFindResource(GetMotifBrush(element) ?? GetBaseBrush(element)) is not SolidColorBrush baseBrush
                || element.TryFindResource("PrimaryBrush") is not SolidColorBrush accent
                || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                Restore(element);
                return;
            }

            Rect pane;
            Size window;
            if (part == MotifPart.Split)
            {
                pane = new Rect(0, 0, element.ActualWidth, element.ActualHeight);
                window = pane.Size;
            }
            else
            {
                if (Window.GetWindow(element) is not { Content: FrameworkElement root } host) return;
                Point at = element.TranslatePoint(new Point(0, 0), root);
                pane = new Rect(at, new Size(element.ActualWidth, element.ActualHeight));
                window = new Size(root.ActualWidth, root.ActualHeight);
            }

            string stamp = $"{part}|{accent.Color}|{baseBrush.Color}|{pane}|{window}";
            if ((string?)element.GetValue(PaintedProperty) == stamp) return;

            // Every part carries a full ink piece (TrashPolkaInk): the document canvas and the split pane their own, the
            // window background and its chrome (title bar, toolbar, sidebar, frame) a third one, so the raised panes never
            // continue the background's image. Each is drawn ONCE at screen size and pinned to the window, so resizing
            // only shows more or less of the same image (Steve, 2026-11-16: "the images scrunch up and change").
            var size = new Size(Math.Ceiling(SystemParameters.PrimaryScreenWidth / TrashPolkaInk.Scale),
                Math.Ceiling(SystemParameters.PrimaryScreenHeight / TrashPolkaInk.Scale));
            if (!Pieces.TryGetValue(part, out var cached) || cached.Accent != accent.Color || cached.Size != size)
            {
                int seed = part switch { MotifPart.Window => TrashPolkaInk.WindowSeed, MotifPart.Split => TrashPolkaInk.SplitSeed, _ => TrashPolkaInk.BackgroundSeed };
                cached = (accent.Color, size, TrashPolkaInk.Draw(accent.Color, size, seed));
                Pieces[part] = cached;
            }
            var brush = TrashPolkaMotif.Compose(baseBrush.Color, cached.Piece, pane, window, scale: TrashPolkaInk.Scale,
                opacity: part == MotifPart.Chrome ? TrashPolkaInk.BackgroundOpacity : TrashPolkaInk.PaneOpacity);
            element.SetValue(PaintedBrushProperty, brush);
            element.SetValue(PaintedProperty, stamp);
            SetBackground(element, brush);
        }

        private static void Restore(FrameworkElement element)
        {
            if (element.GetValue(PaintedProperty) is null) return;
            element.ClearValue(PaintedProperty);
            element.ClearValue(PaintedBrushProperty);
            string key = GetBaseBrush(element);
            switch (element)
            {
                case Border border: border.SetResourceReference(Border.BackgroundProperty, key); break;
                case Panel panel: panel.SetResourceReference(Panel.BackgroundProperty, key); break;
                case Control control: control.SetResourceReference(Control.BackgroundProperty, key); break;
            }
        }

        private static void SetBackground(FrameworkElement element, Brush brush)
        {
            switch (element)
            {
                case Border border: border.Background = brush; break;
                case Panel panel: panel.Background = brush; break;
                case Control control: control.Background = brush; break;
            }
        }
    }
}
