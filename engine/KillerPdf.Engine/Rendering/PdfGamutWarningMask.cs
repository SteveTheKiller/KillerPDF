using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

/// <summary>An owned printer-gamut classification plane, separate from frame color pixels.</summary>
/// <remarks>Coordinates use the returned frame's top-left origin. Storage is copied and never pooled.</remarks>
public sealed class PdfGamutWarningMask
{
    private readonly byte[] _classifications;

    internal PdfGamutWarningMask(int width, int height, ReadOnlySpan<byte> classifications,
        string printerProfileSha256)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (classifications.Length != (long)width * height)
            throw new ArgumentException("The gamut classifications do not match the frame dimensions.", nameof(classifications));
        ArgumentNullException.ThrowIfNull(printerProfileSha256);
        int warnings = 0, boundary = 0;
        foreach (byte classification in classifications)
        {
            if (classification > (byte)PdfGamutClassification.NotEvaluated)
                throw new ArgumentException("The gamut classification is invalid.", nameof(classifications));
            if (classification == (byte)PdfGamutClassification.Outside) warnings++;
            if (classification == (byte)PdfGamutClassification.Boundary) boundary++;
        }
        _classifications = classifications.ToArray();
        Width = width;
        Height = height;
        PrinterProfileSha256 = printerProfileSha256;
        WarningPixelCount = warnings;
        BoundaryPixelCount = boundary;
    }

    /// <summary>Gets the mask's pixel width.</summary>
    public int Width { get; }
    /// <summary>Gets the mask's pixel height.</summary>
    public int Height { get; }
    /// <summary>Gets the content identity of the printer profile used for classification.</summary>
    public string PrinterProfileSha256 { get; }
    /// <summary>Gets the number of pixels beyond the warning boundary band.</summary>
    public int WarningPixelCount { get; }
    /// <summary>Gets the number of pixels within the numerical boundary band.</summary>
    public int BoundaryPixelCount { get; }

    internal ReadOnlySpan<byte> Classifications => _classifications;

    /// <summary>Gets the classification at a frame-local pixel.</summary>
    public PdfGamutClassification GetClassification(int x, int y)
    {
        if ((uint)x >= (uint)Width) throw new ArgumentOutOfRangeException(nameof(x));
        if ((uint)y >= (uint)Height) throw new ArgumentOutOfRangeException(nameof(y));
        return (PdfGamutClassification)_classifications[y * Width + x];
    }
}
