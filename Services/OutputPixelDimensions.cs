namespace KillerPDF.Services;

internal static class OutputPixelDimensions
{
    internal static (int Width, int Height) FromPoints(
        double widthPoints, double heightPoints, double dpi)
    {
        if (!double.IsFinite(widthPoints) || !double.IsFinite(heightPoints)
            || !double.IsFinite(dpi) || widthPoints <= 0 || heightPoints <= 0 || dpi <= 0)
            return (0, 0);

        return (
            Math.Max(1, (int)Math.Round(widthPoints / 72.0 * dpi)),
            Math.Max(1, (int)Math.Round(heightPoints / 72.0 * dpi)));
    }

    internal static string ScaleLabel(double outputWidth, double outputHeight, double sourceWidth, double sourceHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return string.Empty;
        double scale = Math.Sqrt(outputWidth * outputHeight / (sourceWidth * sourceHeight));
        return "x" + scale.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>Turns an image's pixel size the way the page it fills is shown.</summary>
    internal static (int Width, int Height) Oriented((int Width, int Height) pixels, double pageWidth, double pageHeight) =>
        pageWidth == pageHeight || pixels.Width == pixels.Height || pageWidth > pageHeight == pixels.Width > pixels.Height
            ? pixels : (pixels.Height, pixels.Width);

    /// <summary>The size "x1" means for a preview: the real image scaled as the preview has been,
    /// or the preview itself when the page has no one image to compare with.</summary>
    internal static (double Width, double Height) ScaleBase(double previewWidth, double previewHeight,
        double sourceWidth, double sourceHeight, (int Width, int Height)? imagePixels) =>
        imagePixels is { } pixels && sourceWidth > 0 && sourceHeight > 0
            ? (pixels.Width * previewWidth / sourceWidth, pixels.Height * previewHeight / sourceHeight)
            : (previewWidth, previewHeight);

    /// <summary>The DPI that keeps a page's image at its own pixels, or the fallback for a page with no
    /// single image. It is a whole number when that renders the page within a pixel of the exact one, so
    /// scans of one size share a resolution (and one render session) without changing the output size.</summary>
    internal static double NativeDpi((int Width, int Height)? imagePixels, double widthPoints,
        double heightPoints, double fallback)
    {
        if (imagePixels is not { } pixels || widthPoints <= 0 || heightPoints <= 0) return fallback;
        double exact = Math.Clamp(Math.Max(pixels.Width / widthPoints, pixels.Height / heightPoints) * 72.0, 24, 1200);
        double whole = Math.Round(exact);
        var (exactWidth, exactHeight) = FromPoints(widthPoints, heightPoints, exact);
        var (wholeWidth, wholeHeight) = FromPoints(widthPoints, heightPoints, whole);
        return Math.Abs(wholeWidth - exactWidth) <= 1 && Math.Abs(wholeHeight - exactHeight) <= 1 ? whole : exact;
    }

    /// <summary>The pixel size an exported page comes out at: its own image's resolution when that is
    /// kept and the page has one image, otherwise the chosen DPI.</summary>
    internal static (int Width, int Height) ExportSize(double widthPoints, double heightPoints, double dpi,
        bool keepResolution, (int Width, int Height)? imagePixels) =>
        FromPoints(widthPoints, heightPoints,
            keepResolution ? NativeDpi(imagePixels, widthPoints, heightPoints, dpi) : dpi);

    internal static bool MatchesDpi(
        int pixelWidth, int pixelHeight, double widthPoints, double heightPoints, double dpi)
    {
        var (Width, Height) = FromPoints(widthPoints, heightPoints, dpi);
        return Math.Abs(pixelWidth - Width) <= 1
            && Math.Abs(pixelHeight - Height) <= 1;
    }
}
