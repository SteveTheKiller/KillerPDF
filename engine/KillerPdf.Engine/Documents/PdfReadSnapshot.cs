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
    // DjVu pages each have their own geometry; every EPUB page has the one geometry, however many pages there are yet.
    private readonly PdfReadPageGeometry[] _geometries;

    private PdfReadSnapshot(EpubLayoutDocument layout, PdfReadPageGeometry geometry, string sourceSha256,
        Func<IReadOnlyList<EpubNavigationEntry>> navigation, CancellationToken cancellationToken)
    {
        _layout = layout;
        _geometries = [geometry];
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
        CancellationToken cancellationToken = default) => OpenEpub(bytes, options, inBackground: false, cancellationToken);

    /// <summary>
    /// Captures source bytes and lays out the EPUB's first chapter in reading order, then returns while the rest of
    /// the book is paginated in the background, in reading order. <see cref="PageCount"/> grows a chapter at a time
    /// until <see cref="Pagination"/> completes; pages already counted never change. The finished snapshot holds the
    /// pages <see cref="OpenEpub(ReadOnlyMemory{byte}, EpubLayoutOptions, CancellationToken)"/> lays out. The token
    /// cancels the first chapter; <see cref="ReleaseLayout"/> stops the background pagination.
    /// </summary>
    public static PdfReadSnapshot OpenEpubProgressive(ReadOnlyMemory<byte> bytes, EpubLayoutOptions options,
        CancellationToken cancellationToken = default) => OpenEpub(bytes, options, inBackground: true, cancellationToken);

    private static PdfReadSnapshot OpenEpub(ReadOnlyMemory<byte> bytes, EpubLayoutOptions options, bool inBackground,
        CancellationToken cancellationToken)
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
        var layout = inBackground ? publication.CreateLayoutInBackground(capturedOptions, cancellationToken)
            : publication.CreateLayout(capturedOptions, cancellationToken);
        try
        {
            return new PdfReadSnapshot(layout,
                new PdfReadPageGeometry(capturedOptions.PageWidth, capturedOptions.PageHeight,
                    PdfReadPageUnit.CssPixel, 96, 96, 0), sourceSha256,
                () => publication.ReadNavigation(cancellationToken), cancellationToken);
        }
        catch
        {
            // Nobody will close a snapshot that was never returned, so its background pagination stops here.
            layout.ChapterPages?.Close();
            throw;
        }
    }

    /// <summary>Gets whether EPUB pages are still being added at the end of the book; false for other formats.</summary>
    public bool IsPaginating => _layout?.IsPaginating ?? false;

    /// <summary>Gets how many EPUB spine items are paginated so far and how many there are; (0, 0) for other formats.</summary>
    public (int Paginated, int Total) PaginationProgress => _layout?.PaginationProgress ?? (0, 0);

    /// <summary>
    /// Completes when every page is known: at once for DjVu and for a blocking EPUB open. It fails with the error that
    /// stopped pagination and is canceled when the layout is released first; pages already counted stay valid.
    /// </summary>
    public Task Pagination => _layout?.Pagination ?? Task.CompletedTask;

    /// <summary>Waits until the page exists; false when pagination ended without reaching it.</summary>
    public Task<bool> WaitForPageAsync(int pageIndex, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(pageIndex);
        return _layout?.WaitForPageAsync(pageIndex, cancellationToken) ?? Task.FromResult(pageIndex < PageCount);
    }

    /// <summary>
    /// Waits until <see cref="ResolveNavigationTarget"/> can resolve the target as in the finished book: for EPUB,
    /// until pagination has reached the target's chapter or ended. Other formats return at once.
    /// </summary>
    public Task WaitForNavigationTargetAsync(string target, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(target);
        return _layout?.WaitForNavigationTargetAsync(target, cancellationToken) ?? Task.CompletedTask;
    }
    /// <summary>Gets the original source format.</summary>
    public PdfDocumentFormat Format { get; }
    /// <summary>Gets the SHA-256 identity of the captured source bytes.</summary>
    public string SourceSha256 { get; }
    /// <summary>Gets the identity of this immutable layout.</summary>
    public Guid LayoutIdentity { get; } = Guid.NewGuid();
    /// <summary>Gets the native page count.</summary>
    public int PageCount => _layout?.Pages.Count ?? _geometries.Length;

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
        return _geometries[_layout is null ? pageIndex : 0];
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

    /// <summary>
    /// Renders the pixel rectangle at <paramref name="left"/>, <paramref name="top"/> of a native page drawn at the
    /// options' full size: exactly that crop of <see cref="RenderPage(int, PdfRenderOptions, CancellationToken)"/>, with
    /// storage limits applied to the region, so a viewer can draw the visible part of a page at any zoom.
    /// </summary>
    public PdfRenderedFrame RenderPageRegion(int pageIndex, PdfRenderOptions options,
        int left, int top, int width, int height, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= PageCount)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (_layout is not null)
            return _layout.RenderPageRegion(pageIndex, options, left, top, width, height, cancellationToken);
        if (left < 0 || top < 0 || width <= 0 || height <= 0
            || (long)left + width > options.Width || (long)top + height > options.Height)
            throw new ArgumentOutOfRangeException(nameof(width), "The region must fit inside the page raster.");
        if (options.SeparationPlate is not null)
            throw new NotSupportedException("Native DjVu rendering does not support color separations.");
        // Smmr and IW44 data decode only as a whole page, so the natural frame (at most 128 MiB) is decoded once and
        // only the region is resampled from it; the requested page size itself is never stored.
        var geometry = _geometries[pageIndex];
        int naturalWidth = (int)Math.Ceiling(geometry.Width);
        int naturalHeight = (int)Math.Ceiling(geometry.Height);
        if (geometry.IntrinsicRotation is 90 or 270)
            (naturalWidth, naturalHeight) = (naturalHeight, naturalWidth);
        long pixels = checked((long)naturalWidth * naturalHeight + (long)width * height);
        if (pixels > options.PixelByteLimit / (2 * 4 * sizeof(float)))
            throw new ArgumentException("The native render exceeds the caller's combined pixel storage limit.", nameof(options));
        PdfNativeFrameResampler.CheckWork(naturalWidth, naturalHeight, options.Width, options.Height, width, height);
        PdfNativeFrameProof.Preflight(options, (long)naturalWidth * naturalHeight, (long)width * height);
        var frame = _djvu!.RenderPage(pageIndex, cancellationToken);
        var output = PdfNativeFrameResampler.Resize(frame, options.Width, options.Height,
            left, top, width, height, cancellationToken);
        return PdfNativeFrameProof.Apply(output, options, cancellationToken);
    }
}
