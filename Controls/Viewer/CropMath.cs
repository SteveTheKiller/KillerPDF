using System.Windows;
using KillerPDF.Services;

namespace KillerPDF.Controls
{
    /// <summary>
    /// Pure helpers behind the Crop bar: page-range parsing and the canvas-to-page mapping.
    /// They live apart from PdfViewer so they can be tested without a window.
    /// </summary>
    internal static class CropMath
    {
        /// <summary>
        /// Parse a page-range string like "1-3,5,7-9" (1-based) into a zero-based index array.
        /// Returns <c>null</c> on parse error or if no valid pages are produced.
        /// </summary>
        internal static int[]? ParsePageRange(string input, int pageCount)
        {
            if (string.IsNullOrWhiteSpace(input)) return null;
            var result = new System.Collections.Generic.HashSet<int>();
            foreach (var part in input.Split([','], StringSplitOptions.RemoveEmptyEntries))
            {
                var seg = part.Trim();
                if (seg.Contains('-'))
                {
                    var halves = seg.Split('-');
                    if (halves.Length == 2 &&
                        int.TryParse(halves[0].Trim(), out int lo) &&
                        int.TryParse(halves[1].Trim(), out int hi))
                    {
                        // Clamp before looping: a hi of int.MaxValue would never end, p++ wraps unchecked.
                        lo = Math.Max(1, lo);
                        hi = Math.Min(pageCount, hi);
                        for (int p = lo; p <= hi; p++)
                            if (p >= 1 && p <= pageCount) result.Add(p - 1);
                    }
                    else return null;
                }
                else if (int.TryParse(seg, out int pg))
                {
                    if (pg >= 1 && pg <= pageCount) result.Add(pg - 1);
                }
                else return null;
            }
            return result.Count == 0 ? null : [.. result.OrderBy(x => x)];
        }

        /// <summary>
        /// Maps a rectangle drawn on the reference page's canvas to a crop box on one target page, in
        /// absolute user-space points (x, y, width, height from the bottom-left). The mapping through the
        /// reference page's effective box is relative to that box, so the target page's own origin
        /// (<paramref name="pageLeft"/>, <paramref name="pageBottom"/>) is added afterwards, as the redaction
        /// burn does, and the result is clamped to that page's box and kept at least one point across.
        /// </summary>
        internal static (double X, double Y, double Width, double Height) ToCropBox(
            Rect canvasRect, double refPageWidth, double refPageHeight, double canvasWidth, double canvasHeight,
            int rotation, double pageLeft, double pageBottom, double pageWidth, double pageHeight)
        {
            var (rx1, ry1, rx2, ry2) = RedactionGeometry.CanvasToPage(
                canvasRect, refPageWidth, refPageHeight, canvasWidth, canvasHeight, rotation);

            // Scale proportionally when "All Pages" spans pages of different sizes.
            double x1 = rx1 * pageWidth / refPageWidth;
            double y1 = ry1 * pageHeight / refPageHeight;
            double x2 = rx2 * pageWidth / refPageWidth;
            double y2 = ry2 * pageHeight / refPageHeight;

            // Clamp to the page's box (relative), keep at least 1 pt, then move into user space.
            x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
            x2 = Math.Min(pageWidth, x2); y2 = Math.Min(pageHeight, y2);
            if (x2 - x1 < 1) x2 = x1 + 1;
            if (y2 - y1 < 1) y2 = y1 + 1;
            return (x1 + pageLeft, y1 + pageBottom, x2 - x1, y2 - y1);
        }
    }
}
