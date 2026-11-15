using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Remembers a window's size, position and maximized state as one settings string
    /// ("left,top,width,height,max").
    ///
    /// Two details that are easy to get wrong. Closing while maximized saves RestoreBounds rather
    /// than the current rect, so the pre-maximize size is what comes back. And restore refuses a
    /// saved rect that no longer lands on the virtual desktop, because monitors get unplugged and a
    /// window restored onto a screen that is gone cannot be dragged back.
    /// </summary>
    internal static class WindowPlacement
    {
        private const string Key = "WindowPlacement";

        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        internal static void Save(Window w, Action<string, string> setSetting)
        {
            try
            {
                bool max = IsMaximized(w);
                Rect r = w.WindowState == WindowState.Normal
                    ? new Rect(w.Left, w.Top, w.Width, w.Height)
                    : w.RestoreBounds;
                // A window that started maximized has no restore bounds until it has been normal
                // once; its own left, top and size are what a restore should return it to.
                if (r.IsEmpty && w.WindowState != WindowState.Normal
                    && !double.IsNaN(w.Left) && !double.IsNaN(w.Top) && !double.IsNaN(w.Width) && !double.IsNaN(w.Height)
                    && w.Width >= 1 && w.Height >= 1)
                    r = new Rect(w.Left, w.Top, w.Width, w.Height);

                if (r.IsEmpty || r.Width < 1 || r.Height < 1 || double.IsNaN(r.Width) || double.IsNaN(r.Height) ||
                    double.IsNaN(r.X) || double.IsNaN(r.Y)) return;

                setSetting(Key, string.Join(",",
                    r.X.ToString("0.##", Inv), r.Y.ToString("0.##", Inv),
                    r.Width.ToString("0.##", Inv), r.Height.ToString("0.##", Inv),
                    max ? "1" : "0"));
            }
            catch { /* best-effort: a lost window position is not worth an exception */ }
        }

        // A window minimized from maximized reports Minimized, and by the time it closes the state it
        // will restore to is gone, so the last state it showed is kept as it changes.
        private sealed class LastShown { public WindowState State; }
        private static readonly ConditionalWeakTable<Window, LastShown> Shown = new ConditionalWeakTable<Window, LastShown>();

        private static void Track(Window w)
        {
            if (Shown.TryGetValue(w, out _)) return;
            var last = Shown.GetOrCreateValue(w);
            last.State = w.WindowState == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
            // The property is watched rather than StateChanged, which a window raises only once it has a handle.
            var state = DependencyPropertyDescriptor.FromProperty(Window.WindowStateProperty, typeof(Window));
            EventHandler changed = (_, _) =>
            {
                if (w.WindowState != WindowState.Minimized) last.State = w.WindowState;
            };
            state.AddValueChanged(w, changed);
            w.Closed += (_, _) => state.RemoveValueChanged(w, changed);
        }

        /// <summary>True when the window is maximized, or minimized from a maximized state.</summary>
        internal static bool IsMaximized(Window w)
            => w.WindowState == WindowState.Maximized
                || (w.WindowState == WindowState.Minimized && Shown.TryGetValue(w, out var last) && last.State == WindowState.Maximized);

        internal static void Restore(Window w, Func<string, string?> getSetting)
        {
            Track(w);
            string? s = getSetting(Key);
            if (string.IsNullOrWhiteSpace(s)) return;
            try
            {
                string[] f = s!.Split(',');
                if (f.Length != 5) return;
                if (!double.TryParse(f[0], NumberStyles.Float, Inv, out double left) ||
                    !double.TryParse(f[1], NumberStyles.Float, Inv, out double top) ||
                    !double.TryParse(f[2], NumberStyles.Float, Inv, out double width) ||
                    !double.TryParse(f[3], NumberStyles.Float, Inv, out double height))
                    return;

                (width, height) = ClampSize(width, height, SystemParameters.VirtualScreenWidth,
                    SystemParameters.VirtualScreenHeight, w.MinWidth, w.MinHeight);

                if (!IsReachable(left, top, width, Screens(w))) return;

                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = left;
                w.Top = top;
                w.Width = width;
                w.Height = height;
                if (f[4] == "1") w.WindowState = WindowState.Maximized;
            }
            catch { /* best-effort */ }
        }

        /// <summary>A saved size never exceeds the virtual desktop, and never drops under the window's minimum.</summary>
        internal static (double, double) ClampSize(double width, double height, double desktopWidth, double desktopHeight,
            double minWidth, double minHeight)
        {
            width = Math.Max(minWidth, Math.Min(width, Math.Max(desktopWidth, minWidth)));
            height = Math.Max(minHeight, Math.Min(height, Math.Max(desktopHeight, minHeight)));
            return (width, height);
        }

        /// <summary>
        /// Leaves at least a grabbable sliver of title bar on one real monitor. The virtual desktop's
        /// bounding box is not enough: monitors of different sizes leave dead corners inside it.
        /// </summary>
        internal static bool IsReachable(double left, double top, double width, System.Collections.Generic.IReadOnlyList<Rect> screens)
        {
            foreach (Rect s in screens)
            {
                double overlap = Math.Min(left + width, s.Right) - Math.Max(left, s.Left);
                if (overlap >= 40 && top >= s.Top - 8 && top <= s.Bottom - 40) return true;
            }
            return false;
        }

        // The monitors in device-independent units, from the system's monitor list. When the list
        // cannot be read, the virtual desktop's bounding box is the whole answer, as before.
        private static System.Collections.Generic.IReadOnlyList<Rect> Screens(Window w)
        {
            var screens = new System.Collections.Generic.List<Rect>();
            try
            {
                double scale = System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX;
                if (scale <= 0) scale = 1;
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, ref MonitorRect r, IntPtr data) =>
                {
                    screens.Add(new Rect(r.Left / scale, r.Top / scale, (r.Right - r.Left) / scale, (r.Bottom - r.Top) / scale));
                    return true;
                }, IntPtr.Zero);
            }
            catch { screens.Clear(); }
            if (screens.Count == 0)
                screens.Add(new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight));
            return screens;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct MonitorRect { public int Left, Top, Right, Bottom; }

        private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, ref MonitorRect rect, IntPtr data);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    }
}
