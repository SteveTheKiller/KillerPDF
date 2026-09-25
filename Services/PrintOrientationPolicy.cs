using System;

namespace KillerPDF.Services;

internal static class PrintOrientationPolicy
{
    public static bool IsLandscape(double[] pageWidths, double[] pageHeights, int currentPageIndex)
    {
        ArgumentNullException.ThrowIfNull(pageWidths);
        ArgumentNullException.ThrowIfNull(pageHeights);

        int pageCount = Math.Min(pageWidths.Length, pageHeights.Length);
        if (currentPageIndex >= 0 && currentPageIndex < pageCount
            && HasValidDimensions(pageWidths[currentPageIndex], pageHeights[currentPageIndex]))
        {
            return pageWidths[currentPageIndex] > pageHeights[currentPageIndex];
        }

        for (int i = 0; i < pageCount; i++)
        {
            if (HasValidDimensions(pageWidths[i], pageHeights[i]))
                return pageWidths[i] > pageHeights[i];
        }

        return false;
    }

    private static bool HasValidDimensions(double width, double height)
        => double.IsFinite(width) && double.IsFinite(height) && width > 0 && height > 0;
}
