using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.Documents;

/// <summary>Identifies the original document format.</summary>
public enum PdfDocumentFormat
{
    /// <summary>Portable Document Format.</summary>
    Pdf,
    /// <summary>DjVu.</summary>
    Djvu,
    /// <summary>EPUB.</summary>
    Epub
}

/// <summary>Identifies a native page coordinate unit.</summary>
public enum PdfReadPageUnit
{
    /// <summary>A CSS pixel at 96 pixels per inch.</summary>
    CssPixel
}

/// <summary>Describes immutable natural page geometry.</summary>
public sealed class PdfReadPageGeometry
{
    internal PdfReadPageGeometry(double width, double height)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        Width = width;
        Height = height;
    }
    /// <summary>Gets the width in natural page units.</summary>
    public double Width { get; }
    /// <summary>Gets the height in natural page units.</summary>
    public double Height { get; }
    /// <summary>Gets the horizontal natural units per inch.</summary>
    public double DpiX => 96;
    /// <summary>Gets the vertical natural units per inch.</summary>
    public double DpiY => 96;
    /// <summary>Gets the intrinsic clockwise rotation in degrees.</summary>
    public int IntrinsicRotation => 0;
    /// <summary>Gets the natural coordinate unit.</summary>
    public PdfReadPageUnit Unit => PdfReadPageUnit.CssPixel;
    /// <summary>Gets the width in points.</summary>
    public double WidthPoints => Width * 72 / DpiX;
    /// <summary>Gets the height in points.</summary>
    public double HeightPoints => Height * 72 / DpiY;
}

/// <summary>Owns an immutable native read layout independently of logical document identity.</summary>
public sealed class PdfReadSnapshot
{
    private readonly EpubLayoutDocument _layout;
    private readonly PdfReadPageGeometry _geometry;

    private PdfReadSnapshot(EpubLayoutDocument layout, PdfReadPageGeometry geometry, string sourceSha256)
    {
        _layout = layout;
        _geometry = geometry;
        SourceSha256 = sourceSha256;
    }

    /// <summary>Captures source bytes and creates a native EPUB layout.</summary>
    public static PdfReadSnapshot OpenEpub(ReadOnlyMemory<byte> bytes, EpubLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (bytes.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("EPUB exceeds the input size limit.");
        byte[] source = bytes.ToArray();
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(source));
        var capturedOptions = options with { };
        var layout = EpubDocument.Open(source, cancellationToken).CreateLayout(capturedOptions, cancellationToken);
        return new PdfReadSnapshot(layout,
            new PdfReadPageGeometry(capturedOptions.PageWidth, capturedOptions.PageHeight), sourceSha256);
    }
    /// <summary>Gets the original source format.</summary>
    public PdfDocumentFormat Format => PdfDocumentFormat.Epub;
    /// <summary>Gets the SHA-256 identity of the captured source bytes.</summary>
    public string SourceSha256 { get; }
    /// <summary>Gets the identity of this immutable layout.</summary>
    public Guid LayoutIdentity { get; } = Guid.NewGuid();
    /// <summary>Gets the native page count.</summary>
    public int PageCount => _layout.Pages.Count;
    /// <summary>Gets the natural geometry of a native page.</summary>
    public PdfReadPageGeometry GetPageGeometry(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return _geometry;
    }
    /// <summary>Renders a native page while preserving its frame encoding.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default) =>
        _layout.RenderPage(pageIndex, cancellationToken);
}
