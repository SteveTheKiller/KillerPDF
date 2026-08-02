using PdfPigDoc = UglyToad.PdfPig.PdfDocument;

namespace KillerPDF.Services
{
    // ============================================================
    // Image placement extraction for the display dark mode (#135
    // follow-up: pictures keep their real colors while the page
    // inverts). Pure functions over an OPEN PdfPig document - the
    // caller owns the open/dispose, because a held handle on the
    // temp file would block the save-time file swap.
    // ============================================================
    internal static class PdfImages
    {
        /// <summary>
        /// The page's image bounding boxes as fractions of the unrotated page, top-left origin
        /// (PdfPig reports PDF points, bottom-left origin - the same y-flip the annotation
        /// pipeline uses). Fractional so one cached set serves every render resolution, and
        /// computed against the UNROTATED page because the render sites apply the display
        /// inversion before the pixel-buffer rotation. pageIndex is 0-based (PdfPig is 1-based).
        /// </summary>
        internal static BitmapHelpers.FracRect[] GetFracRects(PdfPigDoc doc, int pageIndex)
        {
            var page = doc.GetPage(pageIndex + 1);
            double pw = page.Width, ph = page.Height;
            if (pw <= 0 || ph <= 0) return [];

            var list = new List<BitmapHelpers.FracRect>();
            foreach (var img in page.GetImages())
            {
                var b = img.BoundingBox;   // Bounds is obsolete in current PdfPig
                double l = b.Left / pw, r = b.Right / pw;
                double t = (ph - b.Top) / ph, bo = (ph - b.Bottom) / ph;
                if (r < l) { var tmp = l; l = r; r = tmp; }
                if (bo < t) { var tmp = t; t = bo; bo = tmp; }
                l = Clamp01(l); r = Clamp01(r);
                t = Clamp01(t); bo = Clamp01(bo);
                if (r - l <= 0 || bo - t <= 0) continue;   // degenerate or fully off-page
                list.Add(new BitmapHelpers.FracRect(l, t, r, bo));
            }
            return list.ToArray();
        }

        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);
    }
}
