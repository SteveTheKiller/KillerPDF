using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace KillerPDF.Controls
{
    /// <summary>
    /// The DWM frame attributes any WindowStyle="None" window in the app needs: rounded corners and
    /// a themed 1px border. Both are no-ops before Windows 11, which is why every call swallows its
    /// failure rather than guarding on a version.
    ///
    /// A window with square corners also gets no system drop shadow, so this is what makes a
    /// borderless window look like a window at all.
    /// </summary>
    internal static class DwmChrome
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;

        private const int DWMWCP_DONOTROUND = 1;
        private const int DWMWCP_ROUND = 2;
        private static readonly DependencyProperty ThemeBorderRefreshProperty = DependencyProperty.RegisterAttached(
            "ThemeBorderRefresh", typeof(Action), typeof(DwmChrome), new PropertyMetadata(null));

        internal static void ObserveThemeBorder(Window w, Action refresh)
        {
            bool observed = w.GetValue(ThemeBorderRefreshProperty) is Action;
            w.SetValue(ThemeBorderRefreshProperty, refresh);
            if (observed) return;

            Action apply = () => (w.GetValue(ThemeBorderRefreshProperty) as Action)?.Invoke();
            var source = HwndSource.FromHwnd(new WindowInteropHelper(w).Handle);
            HwndSourceHook hook = (IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) =>
            {
                if (msg == 0x031A || msg == 0x0015)
                    KillerFamily.Accessibility.HighContrastResources.RefreshSystemColors();
                return IntPtr.Zero;
            };
            source?.AddHook(hook);
            KillerFamily.Accessibility.HighContrastResources.SystemColorsRefreshed += apply;
            w.Closed += (_, _) =>
            {
                KillerFamily.Accessibility.HighContrastResources.SystemColorsRefreshed -= apply;
                source?.RemoveHook(hook);
                w.ClearValue(ThemeBorderRefreshProperty);
            };
        }

        /// <summary>Rounds or squares a window's corners. Square when maximized, so it sits flush
        /// to the screen edges. Returns true when DWM accepted the preference, so a caller can tell
        /// whether the system draws the edge (Windows 11) or the window must draw its own; callers
        /// that do not care ignore the result.</summary>
        internal static bool SetRoundedCorners(Window w, bool rounded)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return false;
                int pref = rounded ? DWMWCP_ROUND : DWMWCP_DONOTROUND;
                return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int)) == 0;
            }
            catch { return false; /* pre-Win11: attribute unsupported */ }
        }

        /// <summary>
        /// Tints the frame border to the theme instead of leaving it system gray. Reads
        /// AppBorderBrush if the theme defines one, else PaneBorderBrush - the override exists for
        /// palettes whose pane borders are deliberately near-invisible. Call at SourceInitialized
        /// and again after every theme change.
        /// </summary>
        internal static void SetThemeBorder(Window w, Action<IntPtr, int>? writeBorder = null)
        {
            try
            {
                var hwnd = new WindowInteropHelper(w).Handle;
                if (hwnd == IntPtr.Zero) return;
                ObserveThemeBorder(w, () => SetThemeBorder(w, writeBorder));
                if ((Application.Current.TryFindResource("AppBorderBrush")
                     ?? Application.Current.TryFindResource("PaneBorderBrush")) is SolidColorBrush b)
                {
                    // COLORREF is 0x00BBGGRR, not RGB.
                    int colorref = b.Color.R | (b.Color.G << 8) | (b.Color.B << 16);
                    if (writeBorder != null) writeBorder(hwnd, colorref);
                    else _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorref, sizeof(int));
                }
            }
            catch { /* pre-Win11: attribute unsupported */ }
        }
    }
}
