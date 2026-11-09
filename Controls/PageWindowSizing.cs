using System;
using System.Windows;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Size rules shared by the page windows (Print, Transform, Color correction, Stamp): the
    /// smallest size that still shows every open option, and a size and position remembered per
    /// window type. DialogFrame.EnablePageSizing applies them; they are kept free of the window
    /// tree so the rules can be checked without showing anything.
    /// </summary>
    internal static class PageWindowSizing
    {
        /// <summary>The narrowest preview a page window keeps: 18 of frame margins and border
        /// around a 264 wide single page.</summary>
        public const double PreviewFloor = 18 + 264;

        /// <summary>The window's minimum from its framed content measured with the preview
        /// collapsed (so the options, footer, title bar and frame, at the dialog's scale already),
        /// plus the preview floor at that scale.</summary>
        public static Size Minimum(Size optionsOnly, double previewFloor, double scale)
            => new(Math.Ceiling(optionsOnly.Width + previewFloor * (scale > 0 ? scale : 1)),
                   Math.Ceiling(optionsOnly.Height));

        /// <summary>The smallest size a resizable dialog may be dragged to: the size it opened at,
        /// so its layout never shrinks below what it was designed for, never below its declared
        /// minimum, and never beyond the work area. An unknown opening side keeps the declared one.</summary>
        public static Size OpeningMinimum(Size opening, Size declared, Size workArea)
        {
            static double Side(double open, double floor, double area)
            {
                double side = double.IsFinite(open) && open > 0 ? Math.Max(open, floor) : floor;
                return double.IsFinite(area) && area > 0 ? Math.Min(side, area) : side;
            }
            return new Size(Side(opening.Width, declared.Width, workArea.Width),
                            Side(opening.Height, declared.Height, workArea.Height));
        }

        /// <summary>The settings key a page window type stores one placement value under, for
        /// example "PageWin.Print.WindowPlacement".</summary>
        public static string SettingKey(string windowType, string key) => "PageWin." + windowType + "." + key;

        /// <summary>Restores the saved placement of this window type, raised to the window's
        /// current minimum. True when a saved placement was applied.</summary>
        public static bool Restore(Window window, string windowType, Func<string, string?> getSetting)
        {
            var before = window.WindowStartupLocation;
            window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowPlacement.Restore(window, k => getSetting(SettingKey(windowType, k)));
            bool restored = window.WindowStartupLocation == WindowStartupLocation.Manual;
            if (!restored) window.WindowStartupLocation = before;
            return restored;
        }

        /// <summary>Saves this window type's size, position and maximized state.</summary>
        public static void Save(Window window, string windowType, Action<string, string> setSetting)
            => WindowPlacement.Save(window, (k, v) => setSetting(SettingKey(windowType, k), v));
    }
}
