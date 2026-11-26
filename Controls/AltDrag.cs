using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Linux-style Alt+drag: holding Alt and pressing the left button anywhere in a window moves
    /// it, as if the press had landed on the title bar, and holding Alt and dragging with the right
    /// button resizes it from the nearest edge or corner. One class handler covers every window the
    /// app opens, dialogs included, so nothing is wired per window. Call <see cref="Register"/>
    /// once at startup.
    /// </summary>
    /// <remarks>
    /// The move is the system's own (DragMove, the same SC_MOVE loop a title-bar drag starts), so
    /// snapping and multi-monitor behaviour are Windows'. A maximized window restores first and
    /// keeps the cursor at the same relative spot across its width, as Windows' title-bar drag
    /// does. Letting go of Alt after a drag would normally open the window menu (SC_KEYMENU) or
    /// put WPF into access-key mode; both are swallowed for that one release. A window that must
    /// stay put, such as a full-screen overlay, sets <c>AltDrag.IsEnabled="False"</c>.
    /// Resizing picks its edges by where the press lands: the outer thirds of the window pick that
    /// edge, so a corner third picks the corner, and the middle third picks the nearest corner. Only
    /// a window that can be resized and is not maximized is resized, never below Windows' minimum
    /// window size or the window's own MinWidth and MinHeight. The right button's release is
    /// swallowed so it opens no context menu.
    /// </remarks>
    public static class AltDrag
    {
        private const int WM_SYSCOMMAND = 0x0112;
        private const int SC_KEYMENU = 0xF100;
        private const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;

        /// <summary>The window edges an Alt+right-drag moves.</summary>
        [Flags]
        internal enum ResizeEdges { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8 }

        private static Window? _sizing;
        private static ResizeEdges _edges;
        private static Point _sizeStart;
        private static Rect _sizeStartRect;

        public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(AltDrag), new PropertyMetadata(true));

        public static bool GetIsEnabled(Window window) => (bool)window.GetValue(IsEnabledProperty);
        public static void SetIsEnabled(Window window, bool value) => window.SetValue(IsEnabledProperty, value);

        private static bool _registered;
        private static bool _swallowAltRelease;

        public static void Register()
        {
            if (_registered) return;
            _registered = true;
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseLeftButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseLeftButtonDown), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseRightButtonDownEvent,
                new MouseButtonEventHandler(OnPreviewMouseRightButtonDown), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseMoveEvent,
                new MouseEventHandler(OnPreviewMouseMove), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewMouseRightButtonUpEvent,
                new MouseButtonEventHandler(OnPreviewMouseRightButtonUp), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.LostMouseCaptureEvent,
                new MouseEventHandler(OnLostMouseCapture), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), UIElement.PreviewKeyUpEvent,
                new KeyEventHandler(OnPreviewKeyUp), handledEventsToo: true);
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler(OnLoaded));
        }

        private static void OnLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window && PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.RemoveHook(SwallowKeyMenu);
                source.AddHook(SwallowKeyMenu);
            }
        }

        private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Window window || !ShouldHandle(window) || Keyboard.Modifiers != ModifierKeys.Alt || e.ButtonState != MouseButtonState.Pressed) return;
            e.Handled = true;
            _swallowAltRelease = true;
            if (window.WindowState == WindowState.Maximized)
            {
                // Restore under the cursor at the same fraction of the width, then carry on moving.
                Point inside = e.GetPosition(window);
                double fraction = window.ActualWidth > 0 ? inside.X / window.ActualWidth : 0.5;
                Point screen = window.PointToScreen(inside);
                var source = PresentationSource.FromVisual(window);
                if (source?.CompositionTarget != null) screen = source.CompositionTarget.TransformFromDevice.Transform(screen);
                window.WindowState = WindowState.Normal;
                window.Left = screen.X - window.RestoreBounds.Width * fraction;
                window.Top = screen.Y - inside.Y;
            }
            try { window.DragMove(); }
            catch (InvalidOperationException) { }   // the button was already up
        }

        /// <summary>Whether an Alt+press in <paramref name="window"/> starts a move.</summary>
        internal static bool ShouldHandle(Window window) => GetIsEnabled(window);

        /// <summary>Whether an Alt+right-press in <paramref name="window"/> starts a resize.</summary>
        internal static bool CanResize(Window window)
            => window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip
               && window.WindowState == WindowState.Normal;

        /// <summary>The edges a resize started at <paramref name="inside"/> moves: the outer thirds
        /// pick their edge, and the middle third, across or down, picks the nearer side.</summary>
        internal static ResizeEdges EdgesAt(Point inside, Size size)
        {
            double fx = size.Width > 0 ? inside.X / size.Width : 0.5;
            double fy = size.Height > 0 ? inside.Y / size.Height : 0.5;
            ResizeEdges edges = ResizeEdges.None;
            if (fx < 1 / 3.0) edges |= ResizeEdges.Left; else if (fx > 2 / 3.0) edges |= ResizeEdges.Right;
            if (fy < 1 / 3.0) edges |= ResizeEdges.Top; else if (fy > 2 / 3.0) edges |= ResizeEdges.Bottom;
            if (edges == ResizeEdges.None)
                edges = (fx < 0.5 ? ResizeEdges.Left : ResizeEdges.Right) | (fy < 0.5 ? ResizeEdges.Top : ResizeEdges.Bottom);
            return edges;
        }

        /// <summary>The window's bounds after its <paramref name="edges"/> move by
        /// <paramref name="delta"/>, held between <paramref name="min"/> and <paramref name="max"/>.
        /// The opposite edges stay put.</summary>
        internal static Rect Resized(Rect start, Vector delta, ResizeEdges edges, Size min, Size max)
        {
            double maxW = Math.Max(max.Width, min.Width), maxH = Math.Max(max.Height, min.Height);
            double left = start.Left, top = start.Top, right = start.Right, bottom = start.Bottom;
            if (edges.HasFlag(ResizeEdges.Left)) left = Clamp(left + delta.X, right - maxW, right - min.Width);
            else if (edges.HasFlag(ResizeEdges.Right)) right = Clamp(right + delta.X, left + min.Width, left + maxW);
            if (edges.HasFlag(ResizeEdges.Top)) top = Clamp(top + delta.Y, bottom - maxH, bottom - min.Height);
            else if (edges.HasFlag(ResizeEdges.Bottom)) bottom = Clamp(bottom + delta.Y, top + min.Height, top + maxH);
            return new Rect(left, top, right - left, bottom - top);
        }

        // Math.Clamp isn't in .NET Framework, which some of the family still targets.
        private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

        private static void OnPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not Window window || !ShouldHandle(window) || Keyboard.Modifiers != ModifierKeys.Alt
                || !CanResize(window) || _sizing != null) return;
            e.Handled = true;
            _swallowAltRelease = true;
            Point inside = e.GetPosition(window);
            _edges = EdgesAt(inside, new Size(window.ActualWidth, window.ActualHeight));
            _sizeStart = ScreenDip(window, inside);
            _sizeStartRect = new Rect(window.Left, window.Top, window.ActualWidth, window.ActualHeight);
            _sizing = window;
            if (!window.CaptureMouse()) _sizing = null;
        }

        private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (_sizing == null || !ReferenceEquals(sender, _sizing)) return;
            Window window = _sizing;
            if (e.RightButton != MouseButtonState.Pressed) { EndResize(); return; }
            e.Handled = true;
            Vector delta = ScreenDip(window, e.GetPosition(window)) - _sizeStart;
            var min = new Size(Math.Max(window.MinWidth, SystemParameters.MinimumWindowWidth),
                               Math.Max(window.MinHeight, SystemParameters.MinimumWindowHeight));
            Rect bounds = Resized(_sizeStartRect, delta, _edges, min, new Size(window.MaxWidth, window.MaxHeight));
            if (PresentationSource.FromVisual(window) is HwndSource { CompositionTarget: { } target } source)
            {
                // One SetWindowPos, so a left or top edge moves and resizes in the same step instead
                // of jittering between a move and a resize.
                Point topLeft = target.TransformToDevice.Transform(bounds.TopLeft);
                Point bottomRight = target.TransformToDevice.Transform(bounds.BottomRight);
                SetWindowPos(source.Handle, IntPtr.Zero, (int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
                    (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y),
                    SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        private static void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (_sizing == null || !ReferenceEquals(sender, _sizing)) return;
            e.Handled = true;   // a handled right-button release opens no context menu
            EndResize();
        }

        private static void OnLostMouseCapture(object sender, MouseEventArgs e)
        {
            if (_sizing != null && ReferenceEquals(sender, _sizing)) _sizing = null;
        }

        private static void EndResize()
        {
            Window? window = _sizing;
            _sizing = null;
            if (window?.IsMouseCaptured == true) window.ReleaseMouseCapture();
        }

        /// <summary>A point in <paramref name="window"/> as screen DIPs.</summary>
        private static Point ScreenDip(Window window, Point inside)
        {
            Point screen = window.PointToScreen(inside);
            var source = PresentationSource.FromVisual(window);
            return source?.CompositionTarget != null ? source.CompositionTarget.TransformFromDevice.Transform(screen) : screen;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        private static void OnPreviewKeyUp(object sender, KeyEventArgs e)
        {
            if (!_swallowAltRelease) return;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is not (Key.LeftAlt or Key.RightAlt)) return;
            e.Handled = true;   // keeps WPF out of access-key mode
            _swallowAltRelease = false;
        }

        private static IntPtr SwallowKeyMenu(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // The window menu Alt's release asks for, right after an Alt+drag.
            if (msg == WM_SYSCOMMAND && (wParam.ToInt64() & 0xFFF0) == SC_KEYMENU && lParam == IntPtr.Zero && _swallowAltRelease)
            {
                _swallowAltRelease = false;
                handled = true;
            }
            return IntPtr.Zero;
        }
    }
}
