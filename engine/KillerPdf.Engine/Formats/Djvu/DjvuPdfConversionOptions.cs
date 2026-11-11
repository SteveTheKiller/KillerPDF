using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Formats.Djvu;

/// <summary>Optional platform image encoding for DjVu PDF conversion.</summary>
public sealed record DjvuPdfConversionOptions
{
    /// <summary>The JPEG encoder for color photo pages. Without one, those pages convert losslessly.</summary>
    public PdfJpegEncoder? JpegEncoder { get; init; }

    /// <summary>The JPEG quality from 1 through 100 when an encoder is supplied.</summary>
    public int JpegQuality { get; init; } = 98;

    internal void Validate()
    {
        if (JpegQuality is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(JpegQuality), "JPEG quality must be between 1 and 100.");
    }
}
