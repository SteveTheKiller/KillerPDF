namespace KillerPDF.Shell
{
    /// <summary>The window width floor, as plain arithmetic the app and the tests share.</summary>
    internal static class WindowFloor
    {
        /// <summary>Per-pane minimum in split view. Unchanged, so a split window is no wider than it needs to be.</summary>
        public const double SplitPaneMinimum = 320;

        /// <summary>Width the document preview grid (the find bar's parent) gives up to the pane around it:
        /// the 8 px right inset of the pane host, the card border and the viewer's own padding. None of it is
        /// scaled by the app size, because the document pane is not a scale host.</summary>
        public const double PreviewGridInset = 27;

        /// <summary>The smallest preview grid the find bar needs when one pane fills the window.</summary>
        public const double PreviewGridMinimum = 360;

        /// <summary>Single-pane minimum for the pane host: 360 for the grid plus the 27 px it loses around it.</summary>
        public const double SinglePaneMinimum = PreviewGridMinimum + PreviewGridInset;

        /// <summary>The window width floor. <paramref name="chrome"/> is everything in the window that is not the
        /// pane host, in screen pixels. <paramref name="paneMinimum"/> is one pane's minimum, scaled by
        /// <paramref name="scale"/> only when the pane is laid out inside a scaled host.</summary>
        public static double Compute(double chrome, double paneMinimum, double scale, bool split, double gutter) =>
            split ? chrome + gutter + paneMinimum * scale * 2 : chrome + paneMinimum * scale;
    }
}
