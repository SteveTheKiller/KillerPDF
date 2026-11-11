namespace KillerPdf.Engine.Documents;

/// <summary>An immutable detached image resource with source pixel dimensions.</summary>
public sealed class PdfImageAsset
{
    private readonly byte[] _pdf;

    internal PdfImageAsset(int width, int height, byte[] pdf, bool mirrored = false, bool flipped = false)
    {
        Mirrored = mirrored;
        Flipped = flipped;
        Width = width;
        Height = height;
        _pdf = pdf.ToArray();
    }

    /// <summary>Gets the original image width in pixels.</summary>
    public int Width { get; }
    /// <summary>Gets the original image height in pixels.</summary>
    public int Height { get; }

    // The page showed the source picture mirrored left to right or upside down; the encoded bytes are untouched.
    internal bool Mirrored { get; }
    internal bool Flipped { get; }

    internal PdfDocument OpenClosedDocument(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_pdf.Length > 5 * 1024 * 1024)
            throw new NotSupportedException("The detached image asset exceeds its size limit.");
        var document = PdfDocument.Open(_pdf.ToArray());
        cancellationToken.ThrowIfCancellationRequested();
        return document;
    }

    /// <summary>Returns a detached single-image PDF with a pixel-sized canonical page.</summary>
    public byte[] ToPdfBytes(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] result = _pdf.ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }
}
