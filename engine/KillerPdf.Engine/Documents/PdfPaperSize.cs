namespace KillerPdf.Engine.Documents;

/// <summary>Identifies common paper sizes from page dimensions measured in PDF points.</summary>
public static class PdfPaperSize
{
    private static readonly (string Name, double Width, double Height)[] Sizes =
    [
        ("Letter", 612, 792),
        ("Legal", 612, 1008),
        ("Tabloid", 792, 1224),
        ("ANSI C", 1224, 1584),
        ("ANSI D", 1584, 2448),
        ("ANSI E", 2448, 3168),
        ("A0", 2383.94, 3370.39),
        ("A1", 1683.78, 2383.94),
        ("A2", 1190.55, 1683.78),
        ("A3", 841.89, 1190.55),
        ("A4", 595.28, 841.89),
        ("A5", 419.53, 595.28)
    ];

    /// <summary>Returns a common size name, or null when no size is within tolerance.</summary>
    public static string? Identify(double width, double height, double tolerance = 1)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height)
            || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width),
                "Paper dimensions must be finite and positive.");
        if (!double.IsFinite(tolerance) || tolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(tolerance),
                "Paper-size tolerance must be finite and nonnegative.");
        foreach ((string name, double standardWidth, double standardHeight) in Sizes)
        {
            bool portrait = Near(width, standardWidth, tolerance)
                && Near(height, standardHeight, tolerance);
            bool landscape = Near(width, standardHeight, tolerance)
                && Near(height, standardWidth, tolerance);
            if (portrait || landscape) return name;
        }
        return null;
    }

    private static bool Near(double value, double expected, double tolerance) =>
        Math.Abs(value - expected) <= tolerance;
}
