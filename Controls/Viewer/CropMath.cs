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
    }
}
