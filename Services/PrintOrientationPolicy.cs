namespace KillerPDF.Services;

internal static class PrintOrientationPolicy
{
    internal static bool UseLandscape(double[] pageWidths, double[] pageHeights)
    {
        int count = System.Math.Min(pageWidths.Length, pageHeights.Length);
        for (int i = 0; i < count; i++)
        {
            if (pageWidths[i] > 0 && pageHeights[i] > 0)
                return pageWidths[i] > pageHeights[i];
        }

        return false;
    }
}
