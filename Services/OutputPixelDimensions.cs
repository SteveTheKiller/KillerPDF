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

    internal static string ScaleLabel(int outputWidth, int outputHeight, int sourceWidth, int sourceHeight)
    {
        if (outputWidth <= 0 || outputHeight <= 0 || sourceWidth <= 0 || sourceHeight <= 0) return string.Empty;
        double scale = Math.Sqrt((double)outputWidth * outputHeight / ((double)sourceWidth * sourceHeight));
        return "x" + scale.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
    }
}
