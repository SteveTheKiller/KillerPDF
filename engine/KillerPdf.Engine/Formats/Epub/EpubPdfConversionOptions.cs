using KillerPdf.Engine.Fonts;

namespace KillerPdf.Engine.Formats.Epub;

/// <summary>Controls explicit EPUB conversion paper geometry and fallback typography in points.</summary>
public sealed record EpubPdfConversionOptions(TrueTypeFont DefaultFont)
{
    /// <summary>Gets the paper width in points.</summary>
    public double PageWidthPoints { get; init; } = 595.2755905511812;
    /// <summary>Gets the paper height in points.</summary>
    public double PageHeightPoints { get; init; } = 841.8897637795276;
    /// <summary>Gets the paper margin in points.</summary>
    public double MarginPoints { get; init; } = 42.51968503937008;
    /// <summary>Gets the fallback font size in points.</summary>
    public double FontSizePoints { get; init; } = 12;
}
