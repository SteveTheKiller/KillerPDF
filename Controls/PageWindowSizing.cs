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

        /// <summary>The least of the options column a page window keeps on screen when dragged
        /// shorter: a section header and about three rows. The rest scrolls.</summary>
        public const double OptionsFloor = 160;

        /// <summary>The shortest a page window may be dragged (Steve 2026-11-26: with its sections
        /// collapsed Transform still would not go shorter). It opens at the full height of its
        /// options, but its minimum is the title bar, frame and footer over <see cref="OptionsFloor"/>
        /// of options (at the dialog's scale), so the settings scroll instead of holding the window
        /// open. Options shorter than the floor keep their own height.</summary>
        public static double MinimumHeight(double framedHeight, double optionsHeight, double scale)
        {
            if (!double.IsFinite(optionsHeight) || optionsHeight <= 0 || optionsHeight > framedHeight)
                return Math.Ceiling(framedHeight);
            double floor = OptionsFloor * (scale > 0 ? scale : 1);
            return Math.Ceiling(framedHeight - optionsHeight + Math.Min(optionsHeight, floor));
        }

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
        /// example "PageWin.Print.L2.WindowPlacement". The layout generation retires sizes saved for an older,
        /// taller layout, so the window opens at its options' height once and is remembered from there. L2 is
        /// 1.9.7's compact page windows (Steve 2026-11-26); before it the key had no generation.</summary>
        public static string SettingKey(string windowType, string key) => "PageWin." + windowType + "." + LayoutGeneration + "." + key;

        /// <summary>Bump when the page windows' options get shorter, so no window reopens at a stale height.</summary>
        internal const string LayoutGeneration = "L2";

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

        /// <summary>The top-left corner that centers a window of <paramref name="size"/> on
        /// <paramref name="area"/>, rounded to whole DIPs. Used for a page window over the app
        /// window and for a small dialog over the document pane it was opened from.</summary>
        public static Point CenteredIn(Rect area, Size size)
            => new(Math.Round(area.Left + (area.Width - size.Width) / 2),
                   Math.Round(area.Top + (area.Height - size.Height) / 2));

        /// <summary>A dialog centers on its document pane only when it fits inside that pane;
        /// one wider or taller than the pane centers on the app window like a page window.</summary>
        public static bool FitsOnPane(Rect pane, Size size)
            => !pane.IsEmpty && pane.Width > 0 && pane.Height > 0
               && double.IsFinite(size.Width) && double.IsFinite(size.Height)
               && size.Width > 0 && size.Height > 0
               && size.Width <= pane.Width && size.Height <= pane.Height;

        /// <summary>Saves this window type's size, position and maximized state.</summary>
        public static void Save(Window window, string windowType, Action<string, string> setSetting)
            => WindowPlacement.Save(window, (k, v) => setSetting(SettingKey(windowType, k), v));
    }
}
