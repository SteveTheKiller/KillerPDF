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
    }

    internal static NativeWorkingDocument OpenDjvu(ReadOnlyMemory<byte> source,
        CancellationToken cancellationToken = default)
    {
        byte[] captured = CaptureSource(source, cancellationToken);
        return new NativeWorkingDocument(captured, PdfReadSnapshot.OpenDjvu(captured, cancellationToken));
    }

    internal static NativeWorkingDocument OpenEpub(ReadOnlyMemory<byte> source, EpubLayoutOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(options);
        // EPUB uses the engine's single package limit (128 MiB) for large illustrated editions.
        byte[] captured = CaptureSource(source, cancellationToken, EpubDocument.MaxPackageBytes);
        return new NativeWorkingDocument(captured, PdfReadSnapshot.OpenEpub(captured, options, cancellationToken));
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
    internal int UnavailableChapterCount => _snapshot.ChapterDiagnostics.Count;
    internal IReadOnlyList<EpubChapterDiagnostic> ChapterDiagnostics => _snapshot.ChapterDiagnostics;
    internal IReadOnlyList<EpubChapterDiagnostic> ContentDiagnostics => _snapshot.ContentDiagnostics;
    internal IReadOnlyList<EpubIgnoredDeclarations> IgnoredDeclarations => _snapshot.IgnoredDeclarations;
    internal IReadOnlyList<int> ReadingOrder => _snapshot.ReadingOrder;
    /// <summary>True when a chapter or an item was left out; ignored style rules alone are not omissions.</summary>
    internal bool HasOmissions => ChapterDiagnostics.Count > 0 || ContentDiagnostics.Count > 0;

    internal byte[] ConvertDjvuToPdf(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] source;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            if (Format != PdfDocumentFormat.Djvu)
                throw new NotSupportedException("DjVu conversion requires a DjVu document.");
            source = _source;
        }
        return DjvuDocument.Open(source, cancellationToken).ConvertToPdf(cancellationToken);
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
            return _revision = new PdfCapturedReadRevision(_snapshot);
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
    }

    public void Dispose() => Close();
}
