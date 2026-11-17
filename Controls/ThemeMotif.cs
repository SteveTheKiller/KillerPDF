using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>Which motif an element paints when the theme has one (Trash Polka).</summary>
    internal enum MotifPart { None, Window, Chrome, Split, Popup }

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
            "BaseBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata("BackgroundBrush", OnBaseBrushChanged));

        // A template trigger can flip BaseBrush with Part (the active document tab); repaint on the new base.
        private static void OnBaseBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not FrameworkElement element || GetPart(element) == MotifPart.None || element.GetValue(PaintedProperty) is null) return;
            element.SetValue(PaintedProperty, "");
            if (element.IsLoaded) Paint(element);
        }

        private static readonly DependencyProperty PaintedProperty = DependencyProperty.RegisterAttached(
            "Painted", typeof(string), typeof(ThemeMotif), new PropertyMetadata(null));

        /// <summary>
        /// Inherited: set on the second document pane in split view. Its <c>Window</c> parts (the card and its active
        /// tab) then draw the second pane's own piece, pinned to the window like the first, so each pane's image runs up
        /// into its own active tab.
        /// </summary>
        public static readonly DependencyProperty SecondPaneProperty = DependencyProperty.RegisterAttached(
            "SecondPane", typeof(bool), typeof(ThemeMotif), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.Inherits));

        public static bool GetSecondPane(DependencyObject element) => (bool)element.GetValue(SecondPaneProperty);
        public static void SetSecondPane(DependencyObject element, bool value) => element.SetValue(SecondPaneProperty, value);

        // Whether the element's Background was a local value before the first paint. Template- and trigger-driven
        // backgrounds (the document tabs) are not, and Restore clears back to them instead of pinning a brush key.
        private static readonly DependencyProperty HadLocalBackgroundProperty = DependencyProperty.RegisterAttached(
            "HadLocalBackground", typeof(bool), typeof(ThemeMotif), new PropertyMetadata(true));

        public static MotifPart GetPart(DependencyObject element) => (MotifPart)element.GetValue(PartProperty);
        public static void SetPart(DependencyObject element, MotifPart value) => element.SetValue(PartProperty, value);
        public static string GetBaseBrush(DependencyObject element) => (string)element.GetValue(BaseBrushProperty);
        public static void SetBaseBrush(DependencyObject element, string value) => element.SetValue(BaseBrushProperty, value);

        public static readonly DependencyProperty MotifBrushProperty = DependencyProperty.RegisterAttached(
            "MotifBrush", typeof(string), typeof(ThemeMotif), new PropertyMetadata(null));

        public static string? GetMotifBrush(DependencyObject element) => (string?)element.GetValue(MotifBrushProperty);
        public static void SetMotifBrush(DependencyObject element, string? value) => element.SetValue(MotifBrushProperty, value);

        private static readonly Dictionary<MotifPart, (Color Accent, Size Size, Drawing Piece)> Pieces = [];

        // The bitmap pieces, one per set of layers, tinted for the accent they were last painted with.
        private sealed class TintedPiece(Color accent, Drawing piece)
        {
            internal Color Accent { get; } = accent;
            internal Drawing Piece { get; } = piece;
        }

        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<TrashPolkaRaster.Layers, TintedPiece> RasterPieces = new();

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
            Retuned -= Rebind(element);
            Retuned += Rebind(element);
            WatchBackground(element);
            WatchHighContrast(element);
            if (element.IsLoaded) Paint(element);
        }

        // High contrast swaps every palette brush for a system brush; the motif steps aside so the element shows it, and
        // comes back when high contrast turns off.
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<FrameworkElement, object> WatchedContrast = new();

        private static void WatchHighContrast(FrameworkElement element)
        {
            if (WatchedContrast.TryGetValue(element, out _)) return;
            WatchedContrast.Add(element, element);
            System.ComponentModel.DependencyPropertyDescriptor
                .FromProperty(KillerFamily.Accessibility.HighContrastResources.IsActiveProperty, element.GetType())
                ?.AddValueChanged(element, OnHighContrastChanged);
        }

        private static void OnHighContrastChanged(object? sender, EventArgs e)
        {
            if (sender is FrameworkElement element) Paint(element);
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

        // ---- Every other window --------------------------------------------------------------------------------------

        // Which part a surface painting a palette brush becomes, so every window mirrors the app: dark surfaces carry the
        // background piece, canvas-gray preview frames the pane piece, menus and bars the popup piece.
        private static readonly (string Key, MotifPart Part)[] SurfaceParts =
        [
            ("BackgroundBrush", MotifPart.Chrome), ("TitleBarBrush", MotifPart.Chrome),
            ("BgCanvas", MotifPart.Window), ("ContentPaneBrush", MotifPart.Window),
            ("MenuBackgroundBrush", MotifPart.Popup), ("AnnotationBarBrush", MotifPart.Popup),
        ];

        private static (string Key, MotifPart Part)? SurfaceOf(FrameworkElement el)
        {
            if (!el.IsHitTestVisible || GetPart(el) != MotifPart.None) return null;
            var brush = el switch { Border b => b.Background, Panel p => p.Background, Control c => c.Background, _ => null };
            if (brush is not SolidColorBrush { Color.A: 255 }) return null;
            foreach (var s in SurfaceParts)
                if (ReferenceEquals(el.TryFindResource(s.Key), brush)) return s;
            return null;
        }

        private static void Mark(FrameworkElement el, (string Key, MotifPart Part) s)
        {
            SetBaseBrush(el, s.Key);
            SetPart(el, s.Part);
        }

        // Every opaque palette surface in a tree. Stacked opaque surfaces each paint the same pinned piece, and the top
        // one hides the rest, so marking them all never doubles the ink.
        private static void MarkTree(FrameworkElement root, int budget = 6000)
        {
            var queue = new Queue<DependencyObject>();
            queue.Enqueue(root);
            while (queue.Count > 0 && budget-- > 0)
            {
                var node = queue.Dequeue();
                if (node is FrameworkElement el && SurfaceOf(el) is { } s) Mark(el, s);
                for (int i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) queue.Enqueue(VisualTreeHelper.GetChild(node, i));
            }
        }

        private static bool _allWindows;

        /// <summary>
        /// Gives every window the app opens (dialogs, print preview, pickers) the background piece, without marking each
        /// one: when a window loads, its outermost opaque Border or Panel painting a palette brush is marked
        /// <c>Chrome</c> with that brush as its base. A window whose root is already marked (the main window) is left
        /// alone. Call once at startup.
        /// </summary>
        public static void EnableForAllWindows()
        {
            if (_allWindows) return;
            _allWindows = true;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnWindowLoaded));
            // Flyouts and context menus live in popups, not windows: the top element of each popup gets its own piece.
            EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent, new RoutedEventHandler(OnPopupContentLoaded));
        }

        private static void OnPopupContentLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element is Window) return;
            if (IsPopupTop(element)) { MarkTree(element); return; }
            // Bars built at runtime inside a window (annotation tool bars) paint the menu brush: mark them as they load.
            if (SurfaceOf(element) is { Part: MotifPart.Popup } s) Mark(element, s);
        }

        // True for the element a popup hosts: its visual parent (or the one above) is WPF's internal PopupRoot.
        private static bool IsPopupTop(FrameworkElement element)
        {
            DependencyObject? up = VisualTreeHelper.GetParent(element);
            for (int i = 0; i < 2 && up is not null; i++, up = VisualTreeHelper.GetParent(up))
                if (up.GetType().Name == "PopupRoot") return true;
            return false;
        }

        private static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is not Window window || window.Content is not FrameworkElement root) return;
            if (GetPart(root) != MotifPart.None) return;
            MarkTree(root);
        }

        private static event Action? Retuned;

        /// <summary>Repaints every motif element after TrashPolkaInk's opacities change.</summary>
        internal static void Retune() => Retuned?.Invoke();

        private static Action Rebind(FrameworkElement element)
        {
            var handler = Handlers.GetValue(element, e => () => Paint(e));
            return handler;
        }

        private static void Detach(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && Handlers.TryGetValue(element, out var handler))
            {
                ThemeManager.ThemeChanged -= handler;
                Retuned -= handler;
            }
        }

        private static void Refresh(object sender, EventArgs e)
        {
            if (sender is FrameworkElement element) Paint(element);
        }

        private static void Paint(FrameworkElement element)
        {
            var part = GetPart(element);
            if (part == MotifPart.Window && GetSecondPane(element)) part = MotifPart.Split;
            if (part == MotifPart.None || element.TryFindResource("ThemeMotif") is not true
                || KillerFamily.Accessibility.HighContrastResources.GetIsActive(element) || SystemParameters.HighContrast
                || element.TryFindResource(GetMotifBrush(element) ?? GetBaseBrush(element)) is not SolidColorBrush baseBrush
                || element.TryFindResource("PrimaryBrush") is not SolidColorBrush accent
                || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                Restore(element);
                return;
            }

            Rect pane;
            Size window;
            // Popups float free of the window, so they draw from their own corner; everything else is pinned to it.
            if (part is MotifPart.Popup)
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

            double opacity = part switch
            {
                MotifPart.Chrome => TrashPolkaInk.BackgroundOpacity,
                MotifPart.Popup => TrashPolkaInk.PopupOpacity,
                _ => TrashPolkaInk.PaneOpacity,
            };
            var size = new Size(Math.Ceiling(SystemParameters.PrimaryScreenWidth / TrashPolkaInk.Scale),
                Math.Ceiling(SystemParameters.PrimaryScreenHeight / TrashPolkaInk.Scale));
            int seed = part switch { MotifPart.Window => TrashPolkaInk.WindowSeed, MotifPart.Split => TrashPolkaInk.SplitSeed, _ => TrashPolkaInk.BackgroundSeed };
            // The bitmap layers (TrashPolkaRaster) are built in the background at this monitor's scale; until they land the
            // element shows the vector piece, and Retune repaints every element once they do.
            double dpi = VisualTreeHelper.GetDpi(element).DpiScaleX;
            var layers = TrashPolkaRaster.Get(seed, size, TrashPolkaInk.Scale, dpi, Retune);
            string stamp = $"{part}|{accent.Color}|{baseBrush.Color}|{pane}|{window}|{opacity}|{(layers is null ? "vector" : "raster " + dpi)}";
            if ((string?)element.GetValue(PaintedProperty) == stamp) return;

            // Every part carries a full ink piece (TrashPolkaInk): the document canvas and the split pane their own, the
            // window background and its chrome (title bar, toolbar, sidebar, frame) a third one, so the raised panes never
            // continue the background's image. Each is drawn ONCE at screen size and pinned to the window, so resizing
            // only shows more or less of the same image (Steve, 2026-11-16: "the images scrunch up and change").
            // An accent switch only recolors the shared accent brushes; the pieces are redrawn only for a new screen size.
            Drawing piece;
            if (layers is not null)
            {
                var tint = Color.FromRgb(accent.Color.R, accent.Color.G, accent.Color.B);
                if (!RasterPieces.TryGetValue(layers, out var composed) || composed.Accent != tint)
                {
                    RasterPieces.Remove(layers);
                    RasterPieces.Add(layers, composed = new TintedPiece(tint, TrashPolkaRaster.Compose(layers, size, tint)));
                }
                piece = composed.Piece;
            }
            else
            {
                TrashPolkaInk.SetAccent(accent.Color);
                if (!Pieces.TryGetValue(part, out var cached) || cached.Size != size)
                {
                    cached = (accent.Color, size, TrashPolkaInk.Draw(accent.Color, size, seed));
                    Pieces[part] = cached;
                }
                piece = cached.Piece;
            }
            var brush = TrashPolkaMotif.Compose(baseBrush.Color, piece, pane, window, scale: TrashPolkaInk.Scale, opacity: opacity);
            element.SetValue(PaintedProperty, stamp);
            // First paint since the last restore: remember whether the background we are about to replace was local.
            if (BackgroundProperty(element) is { } bg && element.ReadLocalValue(PaintedBrushProperty) == DependencyProperty.UnsetValue)
                element.SetValue(HadLocalBackgroundProperty, element.ReadLocalValue(bg) != DependencyProperty.UnsetValue);
            element.SetValue(PaintedBrushProperty, brush);
            SetBackground(element, brush);
        }

        private static void Restore(FrameworkElement element)
        {
            if (element.GetValue(PaintedProperty) is null) return;
            element.ClearValue(PaintedProperty);
            element.ClearValue(PaintedBrushProperty);
            if (!(bool)element.GetValue(HadLocalBackgroundProperty))
            {
                if (BackgroundProperty(element) is { } bg) element.ClearValue(bg);
                return;
            }
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
