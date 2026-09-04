namespace KillerPdf.Engine.Documents;

/// <summary>Page orientation derived from physical page dimensions.</summary>
public enum PdfPaperOrientation
{
    /// <summary>The page is taller than it is wide.</summary>
    Portrait,
    /// <summary>The page is wider than it is tall.</summary>
    Landscape,
    /// <summary>The page has equal width and height.</summary>
    Square
}

/// <summary>Display-ready physical dimensions and common paper identity.</summary>
public sealed record PdfPaperSizeDescription(
    double WidthPoints, double HeightPoints,
    double WidthInches, double HeightInches,
    double WidthMillimeters, double HeightMillimeters,
    PdfPaperOrientation Orientation, string? CommonName);

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

    /// <summary>Returns physical dimensions, orientation, and a common paper name.</summary>
    public static PdfPaperSizeDescription Describe(
        double width, double height, double tolerance = 1)
    {
        string? name = Identify(width, height, tolerance);
        return new PdfPaperSizeDescription(
            width, height,
            width / 72d, height / 72d,
            width * 25.4d / 72d, height * 25.4d / 72d,
            width == height ? PdfPaperOrientation.Square
                : width > height ? PdfPaperOrientation.Landscape
                : PdfPaperOrientation.Portrait,
            name);
    }

    private static bool Near(double value, double expected, double tolerance) =>
        Math.Abs(value - expected) <= tolerance;
}
