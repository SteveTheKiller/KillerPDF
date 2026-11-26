using System.Collections.Generic;
using System.Windows;

namespace KillerPDF.Services
{
    /// <summary>
    /// The Z that closes off a form field's unused space (#536): an open four-point stroke from the top-left of the
    /// space to its top-right, down to its bottom-left and across to its bottom-right, the way a paper form gets a Z
    /// through the part nobody wrote in. The space runs the field's full width from just under its last line of text
    /// to its bottom; an empty field gives the whole box.
    /// </summary>
    internal static class FieldZMark
    {
        // Sizes in PDF points, multiplied by the canvas scale (canvas units per point).
        internal const double StrokePoints = 1.5;
        internal const double InsetPoints = 4;
        internal const double GapPoints = 2;
        internal const double MinWidthPoints = 24;
        internal const double MinHeightPoints = 12;

        /// <summary>The Z's points in canvas units, or null when the space left is too small to hold one.</summary>
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
            return [new Point(left, upper), new Point(right, upper), new Point(left, lower), new Point(right, lower)];
        }
    }
}
