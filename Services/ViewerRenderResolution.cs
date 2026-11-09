namespace KillerPDF.Services;

internal static class ViewerRenderResolution
{
    internal static int Primary(double dpiScaleX, double dpiScaleY, double zoomLevel) =>
        (int)Math.Min(6144,
            2048 * Math.Max(dpiScaleX, dpiScaleY) * Math.Max(1.0, zoomLevel));

    /// <summary>The longest side, in device pixels, of a Grid tile drawn at the size it shows: its longest side in DIPs
    /// times the view zoom times the monitor scale, rounded up, between 64 and 4096.</summary>
    internal static int GridTile(double longestDip, double dpiScaleX, double dpiScaleY, double zoomLevel) =>
        (int)Math.Clamp(Math.Ceiling(longestDip * zoomLevel * Math.Max(dpiScaleX, dpiScaleY) - 1e-9), 64, 4096);

    /// <summary>Continuous view's page raster width in pixels. From full size up it is unchanged: twice the page's width
    /// in DIPs, between 800 and 2048. Below full size it is twice the page's width on screen, never under 256 and never
    /// more than at full size: a page shown at 46% drew 1632 px for about 376 px on screen.</summary>
    internal static int ContinuousWidth(double pageWidthDip, double dpiScaleX, double dpiScaleY, double zoomLevel)
    {
        int full = Math.Max(800, Math.Min(2048, (int)(pageWidthDip * 2)));
        if (zoomLevel >= 1.0) return full;
        double onScreen = 2 * pageWidthDip * zoomLevel * Math.Max(dpiScaleX, dpiScaleY);
        return Math.Min(full, Math.Max(256, (int)Math.Ceiling(onScreen - 1e-9)));
    }

    internal static int Secondary(bool twoPage, double dpiScaleX, double dpiScaleY, double zoomLevel) =>
        twoPage
            ? Primary(dpiScaleX, dpiScaleY, zoomLevel)
            : (int)Math.Min(3072, 1536 * Math.Max(1.0, Math.Max(dpiScaleX, dpiScaleY)));
}
