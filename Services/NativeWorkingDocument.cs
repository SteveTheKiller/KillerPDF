using System.IO;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Djvu;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal sealed class NativeWorkingDocument : IDisposable
{
    private const int MaximumSourceBytes = 64 * 1024 * 1024;
    private const int CopyChunkBytes = 64 * 1024;
    private readonly byte[] _source;
    private readonly PdfReadSnapshot _snapshot;
    private readonly Lock _sync = new();
    private PdfCapturedReadRevision? _revision;
    private bool _closed;

    private NativeWorkingDocument(byte[] source, PdfReadSnapshot snapshot)
    {
        _source = source;
        _snapshot = snapshot;
        _shownPages = snapshot.PageCount;
    }

    internal static NativeWorkingDocument OpenDjvu(ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        byte[] captured = CaptureSource(source, cancellationToken);
        // The captured copy is ours alone and never changes, so the engine reads pages from it without another copy.
        return new NativeWorkingDocument(captured, PdfReadSnapshot.OpenDjvuOwned(captured, cancellationToken));
    }

    internal static NativeWorkingDocument OpenEpub(ReadOnlyMemory<byte> source, EpubLayoutOptions options,
        CancellationToken cancellationToken = default) => OpenEpub(source, options, inBackground: false, cancellationToken);

    /// <summary>
    /// Opens an EPUB. With <paramref name="inBackground"/>, it returns once the first chapter is laid out and the rest
    /// of the book is paginated in the background (<see cref="IsPaginating"/>); closing the book stops that.
    /// </summary>
    internal static NativeWorkingDocument OpenEpub(ReadOnlyMemory<byte> source, EpubLayoutOptions options,
        bool inBackground, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        // EPUB uses the engine's single package limit (128 MiB) for large illustrated editions.
        byte[] captured = CaptureSource(source, cancellationToken, EpubDocument.MaxPackageBytes);
        return new NativeWorkingDocument(captured, inBackground
            ? PdfReadSnapshot.OpenEpubProgressive(captured, options, cancellationToken)
            : PdfReadSnapshot.OpenEpub(captured, options, cancellationToken));
    }

    private static byte[] CaptureSource(ReadOnlyMemory<byte> source, CancellationToken cancellationToken,
        int maximum = MaximumSourceBytes)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (source.Length > maximum)
            throw new InvalidDataException("Native document input exceeds the " + (maximum >> 20) + " MiB limit.");
        return source.ToArray();
    }

    internal PdfDocumentFormat Format => _snapshot.Format;
    internal string ContentIdentity => _snapshot.SourceSha256;
    internal int PageCount => _snapshot.PageCount;
    /// <summary>
    /// The pages the window shows: the count at open, then whatever <see cref="ShowPaginatedPages"/> last took in. The
    /// views, the page list and the page total all size from this one number on the UI thread, so they agree while
    /// pagination adds pages behind them.
    /// </summary>
    internal int ShownPageCount => _shownPages;
    private volatile int _shownPages;

    /// <summary>Takes in the pages paginated since the last call; returns the shown count before and after.</summary>
    internal (int Before, int After) ShowPaginatedPages()
    {
        int before = ShownPageCount;
        int after = Math.Max(before, _snapshot.PageCount);
        _shownPages = after;
        return (before, after);
    }

    /// <summary>True while a book's later pages are still being added; pages already counted never change.</summary>
    internal bool IsPaginating => _snapshot.IsPaginating;
    /// <summary>Completes when every page is known; fails with what stopped pagination, canceled once closed first.</summary>
    internal Task Pagination => _snapshot.Pagination;

    internal bool IsClosed
    {
        get { lock (_sync) return _closed; }
    }

    /// <summary>How many spine items are paginated so far and how many there are, for showing a wait's progress.</summary>
    internal (int Paginated, int Total) PaginationProgress => _snapshot.PaginationProgress;

    /// <summary>Waits until the page exists; false when the book turned out shorter.</summary>
    internal Task<bool> WaitForPageAsync(int pageIndex, CancellationToken cancellationToken = default)
        => _snapshot.WaitForPageAsync(pageIndex, cancellationToken);

    /// <summary>Waits until a navigation target resolves to its final page: its chapter is paginated.</summary>
    internal Task WaitForNavigationTargetAsync(string target, CancellationToken cancellationToken = default)
        => _snapshot.WaitForNavigationTargetAsync(target, cancellationToken);
    internal int UnavailableChapterCount => _snapshot.ChapterDiagnostics.Count;
    internal IReadOnlyList<EpubChapterDiagnostic> ChapterDiagnostics => _snapshot.ChapterDiagnostics;
    internal IReadOnlyList<EpubChapterDiagnostic> ContentDiagnostics => _snapshot.ContentDiagnostics;
    internal IReadOnlyList<EpubIgnoredDeclarations> IgnoredDeclarations => _snapshot.IgnoredDeclarations;
    internal IReadOnlyList<int> ReadingOrder => _snapshot.ReadingOrder;
    /// <summary>True when a chapter or an item was left out; ignored style rules alone are not omissions.</summary>
    internal bool HasOmissions => ChapterDiagnostics.Count > 0 || ContentDiagnostics.Count > 0 || DjvuNotices.Count > 0;

    // Page notices arrive as each page's text is first read, on whatever thread read it.
    private readonly SortedDictionary<(int Page, string Code), DjvuDiagnostic> _pageNotices = [];

    /// <summary>The DjVu notices the reader is told about, document ones first, then page ones in page order.</summary>
    internal IReadOnlyList<DjvuDiagnostic> DjvuNotices
    {
        get
        {
            var document = _snapshot.DjvuDiagnostics.Where(NativeDocumentDiagnostics.RaisesNotice);
            lock (_sync) return [.. document, .. _pageNotices.Values];
        }
    }

    /// <summary>Raised, on the reading thread, when a page's text adds a notice the reader is told about.</summary>
    internal event Action? NoticesChanged;

    // The captured revision reports each DjVu page text it reads; a page notice already known, or one arriving after
    // the document closed, changes nothing.
    private void RecordTextNotices(IReadOnlyList<DjvuDiagnostic> notices)
    {
        bool added = false;
        lock (_sync)
        {
            if (_closed) return;
            foreach (var notice in notices)
                if (notice.PageIndex is int page && NativeDocumentDiagnostics.RaisesNotice(notice))
                    added |= _pageNotices.TryAdd((page, notice.Code), notice);
        }
        if (added) NoticesChanged?.Invoke();
    }

    internal byte[] ConvertDjvuToPdf(CancellationToken cancellationToken = default)
        => ConvertDjvuToPdf(new List<DjvuDiagnostic>(), cancellationToken);

    /// <summary>Converts the DjVu document and adds a notice for each page whose text the PDF kept only in part.</summary>
    internal byte[] ConvertDjvuToPdf(ICollection<DjvuDiagnostic> notices, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(notices);
        cancellationToken.ThrowIfCancellationRequested();
        byte[] source;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (Format != PdfDocumentFormat.Djvu)
                throw new NotSupportedException("DjVu conversion requires a DjVu document.");
            source = _source;
        }
        return DjvuDocument.OpenOwned(source, cancellationToken).ConvertToPdf(new DjvuPdfConversionOptions
        {
            JpegEncoder = (pixels, width, height, components, quality) =>
                PdfImageJpeg.EncodePhoto(pixels, width, height, components, quality, cancellationToken),
            JpegQuality = 100
        }, notices, cancellationToken);
    }

    internal byte[] ConvertEpubToPdf(EpubPdfConversionOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] source;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (Format != PdfDocumentFormat.Epub)
                throw new NotSupportedException("EPUB conversion requires an EPUB document.");
            source = _source;
        }
        return EpubDocument.Open(source, cancellationToken).ConvertToPdf(options, cancellationToken);
    }

    internal PdfReadPageGeometry GetNativePageGeometry(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _snapshot.GetPageGeometry(pageIndex);
        }
    }

    internal IReadOnlyList<PdfReadLinkRegion> ReadLinkRegions(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _snapshot.ReadLinkRegions(pageIndex, cancellationToken);
        }
    }

    internal IReadOnlyList<PdfReadNavigationEntry> ReadNavigation(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _snapshot.ReadNavigation(cancellationToken);
        }
    }

    internal PdfReadNavigationDestination ResolveNavigationTarget(string target, int? currentPageIndex = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            return _snapshot.ResolveNavigationTarget(target, currentPageIndex, cancellationToken);
        }
    }

    internal PdfCapturedReadRevision CaptureReadRevision()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (_revision is { IsClosed: false } revision) return revision;
            return _revision = new PdfCapturedReadRevision(_snapshot, RecordTextNotices);
        }
    }

    internal void WriteCopyTo(Stream destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
            throw new ArgumentException("The destination must be writable.", nameof(destination));
        byte[] source;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            source = _source;
        }
        for (int offset = 0; offset < source.Length; offset += CopyChunkBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            destination.Write(source.AsSpan(offset, Math.Min(CopyChunkBytes, source.Length - offset)));
        }
        cancellationToken.ThrowIfCancellationRequested();
    }

    internal void Close()
    {
        PdfCapturedReadRevision? revision;
        lock (_sync)
        {
            if (_closed) return;
            _closed = true;
            revision = _revision;
            _revision = null;
        }
        revision?.Dispose();
        // With the revision's leases cancelled, the book's queued layout ahead is dropped so the closed book can be
        // collected; a read already laying out finishes, and nothing new is laid out.
        _snapshot.ReleaseLayout();
    }

    public void Dispose() => Close();
}
