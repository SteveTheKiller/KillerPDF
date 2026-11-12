using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Services;

/// <summary>Shared geometry and page-range helpers for markup preview and engine burn-in.</summary>
internal static class PdfBurn
{
    /// <summary>
    /// A highlighter mark: multiplied with what lies under it on screen and in saved copies, so the text stays dark.
    /// Covers, filled Shapes rectangles, strikethrough and underline paint normally. Screen and burn share this rule.
    /// </summary>
    internal static bool IsHighlighterMark(HighlightAnnotation highlight) =>
        highlight is not CoverAnnotation && !highlight.IsShape && highlight.Style == HighlightStyle.Fill;

    internal static Geometry? HighlightEraseGeometry(HighlightAnnotation highlight)
    {
        if (highlight.Erases is not { Count: > 0 } erases) return null;
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (HighlightErase erase in erases)
        {
            if (erase.Points.Count == 0) continue;
            if (erase.Points.Count == 1)
            {
                holes.Children.Add(new EllipseGeometry(erase.Points[0], erase.Radius, erase.Radius));
                continue;
            }
            var figure = new PathFigure
                { StartPoint = erase.Points[0], IsClosed = false, IsFilled = false };
            for (int index = 1; index < erase.Points.Count; index++)
                figure.Segments.Add(new LineSegment(erase.Points[index], true));
            var path = new PathGeometry();
            path.Figures.Add(figure);
            var pen = new Pen(Brushes.Black, Math.Max(0.5, erase.Radius * 2))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round
            };
            holes.Children.Add(path.GetWidenedPathGeometry(pen));
        }
        return holes.Children.Count == 0 ? null : new CombinedGeometry(
            GeometryCombineMode.Exclude, new RectangleGeometry(highlight.DrawRect()), holes);
    }

    internal static IEnumerable<int> StampPageRange(string range, int pageCount)
    {
        var pages = new SortedSet<int>();
        if (string.IsNullOrWhiteSpace(range))
        {
            for (int index = 0; index < pageCount; index++) pages.Add(index);
            return pages;
        }
        foreach (string part in range.Split(','))
        {
            string value = part.Trim();
            if (value.Length == 0) continue;
            int dash = value.IndexOf('-');
            if (dash > 0)
            {
                if (!int.TryParse(value[..dash].Trim(), out int first)
                    || !int.TryParse(value[(dash + 1)..].Trim(), out int last)) continue;
                int low = Math.Max(1, Math.Min(first, last));
                int high = Math.Min(pageCount, Math.Max(first, last));
                for (int page = low; page <= high; page++) pages.Add(page - 1);
            }
            else if (int.TryParse(value, out int page) && page >= 1 && page <= pageCount)
                pages.Add(page - 1);
        }
        return pages;
    }
}
