namespace KillerPDF.Services;

internal sealed record ContinuousPageSlot(int Row, int Column, double Left, double Top, double Height);

internal static class ContinuousPageLayout
{
    internal static IReadOnlyList<ContinuousPageSlot> Create(
        IReadOnlyList<double> heights, double pageWidth, bool twoPage, bool book, double gap = 12)
    {
        var slots = new List<ContinuousPageSlot>();
        double top = 0;
        int columns = twoPage ? 2 : 1;
        for (int page = 0, row = 0; page < heights.Count; row++)
        {
            if (twoPage && book && page == 0)
            {
                slots.Add(new(row, 0, (pageWidth + gap) / 2, top, heights[page]));
                top += heights[page++] + gap;
                continue;
            }
            double height = 0;
            for (int column = 0; column < columns && page < heights.Count; column++, page++)
            {
                slots.Add(new(row, column, column * (pageWidth + gap), top, heights[page]));
                height = Math.Max(height, heights[page]);
            }
            top += height + gap;
        }
        return slots;
    }
}
