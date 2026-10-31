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

                if (r.IsEmpty || r.Width < 1 || r.Height < 1 ||
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

                width = Math.Max(w.MinWidth, width);
                height = Math.Max(w.MinHeight, height);

                if (!IsOnScreen(left, top, width)) return;

                w.WindowStartupLocation = WindowStartupLocation.Manual;
                w.Left = left;
                w.Top = top;
                w.Width = width;
                w.Height = height;
                if (f[4] == "1") w.WindowState = WindowState.Maximized;
            }
            catch { /* best-effort */ }
        }

        /// <summary>Leaves at least a grabbable sliver of title bar reachable on some monitor.</summary>
        private static bool IsOnScreen(double left, double top, double width)
        {
            double vl = SystemParameters.VirtualScreenLeft;
            double vt = SystemParameters.VirtualScreenTop;
            double vr = vl + SystemParameters.VirtualScreenWidth;
            double vb = vt + SystemParameters.VirtualScreenHeight;

            return !(left + width < vl + 40 || left > vr - 40 || top < vt - 8 || top > vb - 40);
        }
    }
}
