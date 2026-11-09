using System.Security.Cryptography;
using KillerPdf.Engine.Formats.Djvu;
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
    CssPixel,
    /// <summary>A natural image pixel at the page's recorded resolution.</summary>
    Pixel
}

/// <summary>Describes immutable natural page geometry.</summary>
public sealed class PdfReadPageGeometry
{
    internal PdfReadPageGeometry(double width, double height, PdfReadPageUnit unit,
        double dpiX, double dpiY, int intrinsicRotation)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (unit is not (PdfReadPageUnit.CssPixel or PdfReadPageUnit.Pixel))
            throw new ArgumentOutOfRangeException(nameof(unit));
        if (!double.IsFinite(dpiX) || dpiX <= 0) throw new ArgumentOutOfRangeException(nameof(dpiX));
        if (!double.IsFinite(dpiY) || dpiY <= 0) throw new ArgumentOutOfRangeException(nameof(dpiY));
        if (intrinsicRotation is not (0 or 90 or 180 or 270))
            throw new ArgumentOutOfRangeException(nameof(intrinsicRotation));
        Width = width;
        Height = height;
        Unit = unit;
        DpiX = dpiX;
        DpiY = dpiY;
        IntrinsicRotation = intrinsicRotation;
    }
    /// <summary>Gets the width in natural page units before intrinsic rotation.</summary>
    public double Width { get; }
    /// <summary>Gets the height in natural page units before intrinsic rotation.</summary>
    public double Height { get; }
    /// <summary>Gets the horizontal natural units per inch.</summary>
    public double DpiX { get; }
    /// <summary>Gets the vertical natural units per inch.</summary>
    public double DpiY { get; }
    /// <summary>Gets the intrinsic clockwise rotation in degrees.</summary>
    public int IntrinsicRotation { get; }
    /// <summary>Gets the natural coordinate unit.</summary>
    public PdfReadPageUnit Unit { get; }
    /// <summary>Gets the width in points before intrinsic rotation.</summary>
    public double WidthPoints => Width * 72 / DpiX;
    /// <summary>Gets the height in points before intrinsic rotation.</summary>
    public double HeightPoints => Height * 72 / DpiY;
}

/// <summary>Owns an immutable native read layout independently of logical document identity.</summary>
public sealed partial class PdfReadSnapshot
{
    /// <summary>Captures source bytes and opens a native DjVu document.</summary>
    public static PdfReadSnapshot OpenDjvu(ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length > 64 * 1024 * 1024)
            throw new InvalidDataException("DjVu input exceeds the 64 MiB limit.");
        byte[] source = bytes.ToArray();
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(source));
        var document = DjvuDocument.Open(source, cancellationToken);
        if (document.Pages.Count == 0)
            throw new NotSupportedException("The DjVu container has no local pages available for reading.");
        return new PdfReadSnapshot(document, sourceSha256, cancellationToken);
    }

    private readonly EpubLayoutDocument? _layout;
    private readonly DjvuDocument? _djvu;

    /// <summary>Gets EPUB chapters replaced by diagnosis pages during isolated layout; empty for other formats.</summary>
    public IReadOnlyList<EpubChapterDiagnostic> ChapterDiagnostics => _layout?.ChapterDiagnostics ?? [];

    /// <summary>Gets single EPUB items, such as images, omitted from chapters that still laid out; empty for other formats.</summary>
    public IReadOnlyList<EpubChapterDiagnostic> ContentDiagnostics => _layout?.ContentDiagnostics ?? [];

    /// <summary>Gets, per laid-out EPUB chapter, how many style declarations and rules were ignored; empty for other formats.</summary>
    public IReadOnlyList<EpubIgnoredDeclarations> IgnoredDeclarations => _layout?.IgnoredDeclarations ?? [];

    /// <summary>Gets EPUB spine indexes in the order their pages appear, non-linear items last; empty for other formats.</summary>
    public IReadOnlyList<int> ReadingOrder => _layout?.Pages.Select(page => page.SpineIndex).Distinct().ToArray() ?? [];
    private readonly PdfReadPageGeometry[] _geometries;

    private PdfReadSnapshot(EpubLayoutDocument layout, PdfReadPageGeometry geometry, string sourceSha256,
        Func<IReadOnlyList<EpubNavigationEntry>> navigation, CancellationToken cancellationToken)
    {
        _layout = layout;
        _geometries = Enumerable.Repeat(geometry, layout.Pages.Count).ToArray();
        Format = PdfDocumentFormat.Epub;
        SourceSha256 = sourceSha256;
        var captured = CaptureNavigation(() => navigation().Select(entry => CaptureEntry(entry, cancellationToken)), cancellationToken);
        _navigation = captured.Entries;
        _navigationFailure = captured.Failure;
    }

    private PdfReadSnapshot(DjvuDocument document, string sourceSha256, CancellationToken cancellationToken)
    {
        _djvu = document;
        _geometries = document.Pages.Select(page => new PdfReadPageGeometry(page.Width, page.Height,
            PdfReadPageUnit.Pixel, page.Dpi, page.Dpi, (int)page.Orientation)).ToArray();
        Format = PdfDocumentFormat.Djvu;
        SourceSha256 = sourceSha256;
        var captured = CaptureNavigation(() => document.ReadNavigation(cancellationToken)
            .Select(entry => CaptureEntry(entry, cancellationToken)), cancellationToken);
        _navigation = captured.Entries;
        _navigationFailure = captured.Failure;
    }

    /// <summary>Captures source bytes and creates a native EPUB layout.</summary>
    public static PdfReadSnapshot OpenEpub(ReadOnlyMemory<byte> bytes, EpubLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        // One package limit for EPUB, owned by the engine reader.
        if (bytes.Length > EpubDocument.MaxPackageBytes)
            throw new InvalidDataException("EPUB exceeds the input size limit.");
        byte[] source = bytes.ToArray();
        string sourceSha256 = Convert.ToHexString(SHA256.HashData(source));
        var capturedOptions = options with { };
        var publication = EpubDocument.Open(source, cancellationToken);
        var layout = publication.CreateLayout(capturedOptions, cancellationToken);
        return new PdfReadSnapshot(layout,
            new PdfReadPageGeometry(capturedOptions.PageWidth, capturedOptions.PageHeight,
                PdfReadPageUnit.CssPixel, 96, 96, 0), sourceSha256,
            () => publication.ReadNavigation(cancellationToken), cancellationToken);
    }
    /// <summary>Gets the original source format.</summary>
    public PdfDocumentFormat Format { get; }
    /// <summary>Gets the SHA-256 identity of the captured source bytes.</summary>
    public string SourceSha256 { get; }
    /// <summary>Gets the identity of this immutable layout.</summary>
    public Guid LayoutIdentity { get; } = Guid.NewGuid();
    /// <summary>Gets the native page count.</summary>
    public int PageCount => _geometries.Length;

    /// <summary>
    /// Ends this book's page layout when its owner closes it: work queued to lay pages out ahead of the reader is
    /// cancelled and dropped, and a page that would need laying out again is refused. Page text stays readable, and a
    /// read already in progress finishes. Calling it again does nothing; DjVu documents have no layout to release.
    /// </summary>
    public void ReleaseLayout() => _layout?.ChapterPages?.Close();
    /// <summary>Gets the natural geometry of a native page.</summary>
    public PdfReadPageGeometry GetPageGeometry(int pageIndex)
    {
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        return _geometries[pageIndex];
    }
    /// <summary>Renders a native page with intrinsic rotation already applied, preserving its frame encoding.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, CancellationToken cancellationToken = default) =>
        _djvu is not null ? _djvu.RenderPage(pageIndex, cancellationToken)
            : _layout!.RenderPage(pageIndex, cancellationToken);

    /// <summary>Renders a native page at the requested pixel dimensions.</summary>
    public PdfRenderedFrame RenderPage(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (_layout is not null)
            return _layout.RenderPage(pageIndex, options, cancellationToken);
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("Native DjVu rendering does not support color separations.");
        var geometry = _geometries[pageIndex];
        int width = (int)Math.Ceiling(geometry.Width);
        int height = (int)Math.Ceiling(geometry.Height);
        if (geometry.IntrinsicRotation is 90 or 270)
            (width, height) = (height, width);
        bool resized = options.Width != width || options.Height != height;
        long pixels = checked((long)width * height + (resized ? (long)options.Width * options.Height : 0));
        if (pixels > options.PixelByteLimit / (2 * 4 * sizeof(float)))
            throw new ArgumentException("The native render exceeds the caller's combined pixel storage limit.", nameof(options));
        if (resized)
            PdfNativeFrameResampler.CheckWork(width, height, options.Width, options.Height);
        PdfNativeFrameProof.Preflight(options, resized ? (long)width * height : 0);
        var frame = _djvu!.RenderPage(pageIndex, cancellationToken);
        var output = resized ? PdfNativeFrameResampler.Resize(frame, options.Width, options.Height, cancellationToken) : frame;
        return PdfNativeFrameProof.Apply(output, options, cancellationToken);
    }
}
