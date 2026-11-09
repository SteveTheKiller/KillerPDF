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

    internal static int Secondary(bool twoPage, double dpiScaleX, double dpiScaleY, double zoomLevel) =>
        twoPage
            ? Primary(dpiScaleX, dpiScaleY, zoomLevel)
            : (int)Math.Min(3072, 1536 * Math.Max(1.0, Math.Max(dpiScaleX, dpiScaleY)));
}
