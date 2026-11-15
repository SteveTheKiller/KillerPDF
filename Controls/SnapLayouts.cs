using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Interop;

// ============================================================
// WINDOWS 11 SNAP LAYOUTS ON THE CUSTOM MAXIMIZE BUTTON
//
// Windows 11 opens the snap layouts flyout only when the window answers WM_NCHITTEST with
// HTMAXBUTTON over its maximize button. A WindowStyle="None" window draws that button in WPF,
// and WindowChrome reports it as client area, so Windows never offers the flyout.
//
// Attach answers HTMAXBUTTON over the button. That moves the mouse into the non-client area,
// where WPF sees no hover, press or click, so this class also does the button's work there:
// it mirrors hover and press onto the IsHovered and IsPressed attached properties (the
// ChromeButton template triggers on both) and clicks the button on the non-client button-up.
// The button keeps its Click handler, keyboard focus and UI Automation name unchanged.
//
// ---- Wiring it up -------------------------------------------------------------------------
//   1. Copy this file unchanged. The namespace is shared on purpose, like AccessibleControls.
//   2. Call SnapLayouts.Attach(this, MaximizeBtn) from MainWindow_SourceInitialized. The hook
//      must be added after WindowChrome's own hook so it sees WM_NCHITTEST first, which is
//      true from SourceInitialized.
//   3. Add the two snap: triggers to the window's ChromeButton style, beside IsMouseOver and
//      IsPressed, so the button still shows hover and press.
//   4. A window with its own WM_NCHITTEST handler calls HitTest from that handler instead of
//      relying on hook order.
// ============================================================
namespace KillerFamily.Windowing
{
    public static class SnapLayouts
    {
        /// <summary>True while the pointer is over the maximize button in the non-client area.</summary>
        public static readonly DependencyProperty IsHoveredProperty = DependencyProperty.RegisterAttached(
            "IsHovered", typeof(bool), typeof(SnapLayouts), new PropertyMetadata(false));

        /// <summary>True while the left button is held on the maximize button in the non-client area.</summary>
        public static readonly DependencyProperty IsPressedProperty = DependencyProperty.RegisterAttached(
            "IsPressed", typeof(bool), typeof(SnapLayouts), new PropertyMetadata(false));

        // Keeps the hook alive for as long as the window that owns it.
        private static readonly DependencyProperty HookProperty = DependencyProperty.RegisterAttached(
            "Hook", typeof(Hook), typeof(SnapLayouts), new PropertyMetadata(null));

        public static bool GetIsHovered(DependencyObject element) => (bool)element.GetValue(IsHoveredProperty);
        public static void SetIsHovered(DependencyObject element, bool value) => element.SetValue(IsHoveredProperty, value);
        public static bool GetIsPressed(DependencyObject element) => (bool)element.GetValue(IsPressedProperty);
        public static void SetIsPressed(DependencyObject element, bool value) => element.SetValue(IsPressedProperty, value);

        public const int HTMAXBUTTON = 9;

        private const int WM_NCHITTEST = 0x0084;
        private const int WM_NCMOUSEMOVE = 0x00A0;
        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int WM_NCLBUTTONUP = 0x00A2;
        private const int WM_NCLBUTTONDBLCLK = 0x00A3;
        private const int WM_NCMOUSELEAVE = 0x02A2;
        private const uint TME_LEAVE = 0x00000002;
        private const uint TME_NONCLIENT = 0x00000010;

        /// <summary>Hooks the window so Windows treats the button as its maximize button.
        /// Call once the window has a handle, from SourceInitialized.</summary>
        public static void Attach(Window window, Button maximizeButton)
        {
            if (window == null) throw new ArgumentNullException(nameof(window));
            if (maximizeButton == null) throw new ArgumentNullException(nameof(maximizeButton));
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            HwndSource? source = HwndSource.FromHwnd(hwnd);
            if (source == null || window.GetValue(HookProperty) != null) return;
            var hook = new Hook(hwnd, maximizeButton);
            window.SetValue(HookProperty, hook);
            source.AddHook(hook.WndProc);
        }

        /// <summary>True when a WM_NCHITTEST point (screen pixels packed in lParam) falls on the
        /// visible, enabled button. For a window that handles WM_NCHITTEST itself.</summary>
        public static bool HitTest(Button maximizeButton, IntPtr lParam)
        {
            if (maximizeButton == null || !maximizeButton.IsVisible || !maximizeButton.IsEnabled) return false;
            // The source can outlive its composition target during native window teardown.
            if (PresentationSource.FromVisual(maximizeButton)?.CompositionTarget == null) return false;
            long packed = lParam.ToInt64();
            var screen = new Point((short)(packed & 0xFFFF), (short)((packed >> 16) & 0xFFFF));
            Point local;
            try { local = maximizeButton.PointFromScreen(screen); }
            catch (InvalidOperationException) { return false; }
            return local.X >= 0 && local.Y >= 0
                && local.X < maximizeButton.ActualWidth && local.Y < maximizeButton.ActualHeight;
        }

        private sealed class Hook
        {
            private readonly IntPtr _hwnd;
            private readonly Button _button;

            internal Hook(IntPtr hwnd, Button button)
            {
                _hwnd = hwnd;
                _button = button;
            }

            internal IntPtr WndProc(IntPtr _, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
            {
                bool onButton = wParam == new IntPtr(HTMAXBUTTON);
                switch (msg)
                {
                    case WM_NCHITTEST:
                        if (HitTest(_button, lParam))
                        {
                            handled = true;
                            return new IntPtr(HTMAXBUTTON);
                        }
                        break;
                    case WM_NCMOUSEMOVE:
                        if (onButton)
                        {
                            SetIsHovered(_button, true);
                            TrackLeave();
                        }
                        else Clear();
                        break;
                    case WM_NCMOUSELEAVE:
                        Clear();
                        break;
                    case WM_NCLBUTTONDOWN:
                    case WM_NCLBUTTONDBLCLK:
                        // Claim it, or Windows runs its own caption-button loop and paints a
                        // stock button over ours.
                        if (onButton)
                        {
                            SetIsPressed(_button, true);
                            handled = true;
                        }
                        break;
                    case WM_NCLBUTTONUP:
                        if (onButton)
                        {
                            bool pressed = GetIsPressed(_button);
                            SetIsPressed(_button, false);
                            handled = true;
                            if (pressed) Click();
                        }
                        break;
                }
                return IntPtr.Zero;
            }

            private void Clear()
            {
                SetIsHovered(_button, false);
                SetIsPressed(_button, false);
            }

            // The same path UI Automation uses, so Click handlers and commands both run.
            private void Click()
            {
                if (!_button.IsEnabled) return;
                if (UIElementAutomationPeer.CreatePeerForElement(_button)?.GetPattern(PatternInterface.Invoke)
                    is IInvokeProvider invoker)
                    invoker.Invoke();
            }

            private void TrackLeave()
            {
                var track = new TRACKMOUSEEVENT
                {
                    cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                    dwFlags = TME_LEAVE | TME_NONCLIENT,
                    hwndTrack = _hwnd,
                };
                TrackMouseEvent(ref track);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct TRACKMOUSEEVENT
        {
            public uint cbSize;
            public uint dwFlags;
            public IntPtr hwndTrack;
            public uint dwHoverTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT eventTrack);
    }
}
