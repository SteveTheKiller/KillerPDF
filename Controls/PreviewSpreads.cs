using System;
using System.Collections.Generic;
using System.Windows;

namespace KillerPDF.Controls
{
    /// <summary>The 1 / 2 / Book choice of a page window's preview.</summary>
    internal enum PreviewLayout { Single, TwoPage, Book }

    /// <summary>
    /// How a page preview pairs pages into spreads and how big a page or spread is drawn in the
    /// preview frame. The pairing is the main viewer's (PdfViewer.Viewport.cs, SpreadStart), so
    /// the preview and the reading view never disagree on which pages face each other.
    /// </summary>
    internal static class PreviewSpreads
    {
        /// <summary>Space around the page or spread inside the frame.</summary>
        public const double Margin = 16;
        /// <summary>Space between the two pages of a spread.</summary>
        public const double Gap = 12;
        /// <summary>The narrowest page a spread may draw; below this the preview shows one page.</summary>
        public const double MinSpreadPage = 162;

        /// <summary>The layout a preview starts in, from the owning viewer: two-page views open
        /// as spreads (Book when the viewer's book mode is on), everything else as one page.</summary>
        public static PreviewLayout FromViewer(bool twoPage, bool bookMode)
            => !twoPage ? PreviewLayout.Single : bookMode ? PreviewLayout.Book : PreviewLayout.TwoPage;

        /// <summary>The first item of the spread holding <paramref name="index"/>: the viewer's
        /// formula, book mode keeping item 0 alone.</summary>
        public static int SpreadStart(int index, bool book)
            => book ? (index == 0 ? 0 : index - ((index + 1) % 2)) : index - index % 2;

        /// <summary>Every spread of <paramref name="count"/> items, in order: the first item and
        /// the second (or -1 when the spread holds one item).</summary>
        public static IReadOnlyList<(int First, int Second)> Spreads(int count, PreviewLayout layout)
        {
            var list = new List<(int, int)>();
            bool book = layout == PreviewLayout.Book;
            for (int i = 0; i < count;)
            {
                bool alone = layout == PreviewLayout.Single || (book && i == 0) || i + 1 >= count;
                list.Add((i, alone ? -1 : i + 1));
                i += alone ? 1 : 2;
            }
            return list;
        }

        /// <summary>The position in <see cref="Spreads"/> of the spread holding item <paramref name="index"/>.</summary>
        public static int SpreadOf(int index, int count, PreviewLayout layout)
        {
            var spreads = Spreads(count, layout);
            for (int s = 0; s < spreads.Count; s++)
                if (spreads[s].First == index || spreads[s].Second == index) return s;
            return spreads.Count == 0 ? 0 : index < 0 ? 0 : spreads.Count - 1;
        }

        /// <summary>The pages worth drawing ahead of a step away from item <paramref name="index"/>: the first item
        /// of the next spread, then the first item of the spread before, each only when it exists.</summary>
        public static int[] NeighborStarts(int index, int count, PreviewLayout layout)
        {
            if (index < 0 || index >= count) return [];
            bool single = layout == PreviewLayout.Single, book = layout == PreviewLayout.Book;
            int start = single ? index : SpreadStart(index, book);
            // The same pairing as Spreads: a single layout, the book cover and a last odd item stand alone.
            bool alone = single || (book && start == 0) || start + 1 >= count;
            int forward = start + (alone ? 1 : 2);
            int backward = start == 0 ? -1 : single ? start - 1 : SpreadStart(start - 1, book);
            var targets = new List<int>(2);
            if (forward < count) targets.Add(forward);
            if (backward >= 0) targets.Add(backward);
            return [.. targets];
        }

        /// <summary>True when a frame this wide can draw two pages of at least the minimum width.</summary>
        public static bool FitsTwo(double frameWidth) => frameWidth >= 2 * MinSpreadPage + Gap + 2 * Margin;

        /// <summary>The drawn size of each page: the largest that fits the frame less the margin,
        /// with <paramref name="pages"/> (1 or 2) side by side, limited by width or height.
        /// <paramref name="aspect"/> is page height over width.</summary>
        public static Size PageSize(Size frame, double aspect, int pages)
        {
            int n = Math.Max(1, pages);
            if (!(aspect > 0)) aspect = 1;
            double byWidth = (frame.Width - 2 * Margin - Gap * (n - 1)) / n;
            double byHeight = (frame.Height - 2 * Margin) / aspect;
            double w = Math.Max(0, Math.Min(byWidth, byHeight));
            return new Size(w, w * aspect);
        }

        /// <summary>The frame width that lets <paramref name="pages"/> pages fill a frame this tall.</summary>
        public static double FrameWidthFor(double frameHeight, double aspect, int pages)
        {
            int n = Math.Max(1, pages);
            if (!(aspect > 0)) aspect = 1;
            return n * Math.Max(0, frameHeight - 2 * Margin) / aspect + Gap * (n - 1) + 2 * Margin;
        }
    }
}
