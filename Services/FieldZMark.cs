using System.Collections.Generic;
using System.Windows;

namespace KillerPDF.Services
{
    /// <summary>
    /// The Z that closes off a form field's unused space (#536), the way a paper form gets a Z through the part nobody
    /// wrote in. It is the Killer Sans Z stretched over the space: a closed outline, filled, with a bar along the top
    /// and bottom and a heavier diagonal cut flush where it meets them. The space runs the field's full width from just
    /// under its last line of text to its bottom; an empty field gives the whole box.
    /// </summary>
    internal static class FieldZMark
    {
        // Killer Sans draws its Z 649 units tall and 500 wide, with 68-unit bars and a diagonal 90 units thick.
        internal const double BarPerHeight = 68.0 / 649;
        internal const double BarPerWidth = 68.0 / 500;
        internal const double DiagonalPerBar = 90.0 / 68;

        // Sizes in PDF points, multiplied by the canvas scale (canvas units per point). The outline is filled, so its
        // own stroke is only a hairline that keeps the edges crisp.
        internal const double StrokePoints = 0.25;
        internal const double MinBarPoints = 2;
        internal const double InsetPoints = 4;
        internal const double GapPoints = 2;
        internal const double MinWidthPoints = 24;
        internal const double MinHeightPoints = 12;

        /// <summary>
        /// The Z's closed outline in canvas units, clockwise from its top-left corner with the first point repeated at
        /// the end, or null when the space left is too small to hold one.
        /// </summary>
        /// <param name="field">The field's box on the page canvas.</param>
        /// <param name="usedBottom">How far down from the field's top its text reaches; 0 or less for an empty field.</param>
        /// <param name="scale">Canvas units per PDF point.</param>
        internal static IReadOnlyList<Point>? Points(Rect field, double usedBottom, double scale)
        {
            if (field.IsEmpty || !double.IsFinite(usedBottom) || !(scale > 0)) return null;
            double inset = InsetPoints * scale;
            double top = usedBottom > 0 ? field.Top + usedBottom + GapPoints * scale : field.Top;
            double width = field.Width - 2 * inset, height = field.Bottom - top - 2 * inset;
            // Text that runs to the bottom, or past it, leaves nothing to close off.
            if (width <= 0 || height <= 0 || width < MinWidthPoints * scale - 2 * inset
                || height < MinHeightPoints * scale - 2 * inset) return null;
            double left = field.Left + inset, right = left + width, upper = top + inset, lower = upper + height;

            // Bars in the glyph's proportion to the height, held to its proportion to the width so a tall, narrow
            // space doesn't fill solid, and never thinner than a firm pen line or so thick the diagonal has no room.
            double bar = Math.Min(height * BarPerHeight, width * BarPerWidth);
            bar = Math.Min(Math.Max(bar, MinBarPoints * scale), height / 4);
            double diagonal = bar * DiagonalPerBar, rise = height - 2 * bar;
            // The diagonal's edges are offset sideways by `run`; its thickness across is run times the sine of its
            // slope, and the slope depends on run, so a few rounds of substitution settle it.
            double run = diagonal;
            for (int i = 0; i < 8; i++)
                run = Math.Min(width / 2, diagonal * Math.Sqrt(rise * rise + (width - run) * (width - run)) / rise);

            Point start = new(left, upper);
            return
            [
                start, new(right, upper), new(right, upper + bar),
                new(left + run, lower - bar), new(right, lower - bar), new(right, lower),
                new(left, lower), new(left, lower - bar),
                new(right - run, upper + bar), new(left, upper + bar), start,
            ];
        }
    }
}
