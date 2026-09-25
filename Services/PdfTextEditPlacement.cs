namespace KillerPDF.Services;

internal static class PdfTextEditPlacement
{
    public static double LeftFromOrigin(
        double sourceOrigin, double borderInset, double paddingLeft, double fallbackLeft)
    {
        if (!double.IsFinite(sourceOrigin)
            || !double.IsFinite(borderInset) || borderInset < 0
            || !double.IsFinite(paddingLeft) || paddingLeft < 0)
        {
            return fallbackLeft;
        }

        return sourceOrigin - borderInset - paddingLeft;
    }

    public static double TopFromBaseline(
        double sourceBaseline, double normalizedFontBaseline, double fontSize,
        double borderInset, double fallbackTop)
    {
        if (!double.IsFinite(sourceBaseline)
            || !double.IsFinite(normalizedFontBaseline) || normalizedFontBaseline <= 0
            || !double.IsFinite(fontSize) || fontSize <= 0
            || !double.IsFinite(borderInset) || borderInset < 0)
        {
            return fallbackTop;
        }

        return sourceBaseline - normalizedFontBaseline * fontSize - borderInset;
    }
}
