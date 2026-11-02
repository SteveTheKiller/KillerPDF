using KillerPdf.Engine.Documents;
using System.IO;

namespace KillerPDF.Services;

/// <summary>Immutable engine view of the active serialized working document.</summary>
internal sealed class PdfEngineDocumentSession
{
    private readonly Lazy<PdfLinkReader> _linkReader;
    private readonly Lazy<PdfFormWidgetReader> _formReader;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Lazy<IReadOnlyList<PdfFormWidgetInfo>>> _formWidgets = new();

    private PdfEngineDocumentSession(string path, PdfDocument document,
        IReadOnlyList<PdfPageInformation> pages)
    {
        Path = path;
        Document = document;
        Pages = pages;
        _linkReader = new Lazy<PdfLinkReader>(() => new PdfLinkReader(document));
        _formReader = new Lazy<PdfFormWidgetReader>(() => new PdfFormWidgetReader(document));
    }

    internal string Path { get; }
    internal PdfDocument Document { get; }
    internal IReadOnlyList<PdfPageInformation> Pages { get; }
    internal PdfLinkReader LinkReader => _linkReader.Value;
    internal int PageCount => Pages.Count;
    internal IReadOnlyList<PdfFormWidgetInfo> ReadFormWidgets(int pageIndex, bool forDisplay = false)
    {
        var widgets = _formWidgets.GetOrAdd(pageIndex,
            page => new Lazy<IReadOnlyList<PdfFormWidgetInfo>>(() => _formReader.Value.Read(page))).Value;
        return forDisplay ? [.. widgets.Where(widget => (widget.AnnotationFlags & 34) == 0)] : widgets;
    }

    internal static PdfEngineDocumentSession Open(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Same recovery as the working document, so a file that opened is never rejected here.
        PdfDocument document = PdfEngineIntegration.OpenForReading(File.ReadAllBytes(path));
        return new PdfEngineDocumentSession(path, document,
            PdfPageInformation.Read(document));
    }

    /// <summary>Captures effective visual rotations without consulting the mutable legacy model.</summary>
    internal void CaptureRotations(Dictionary<int, int> rotations)
    {
        ArgumentNullException.ThrowIfNull(rotations);
        bool completeApplicationState = rotations.Count == Pages.Count
            && Enumerable.Range(0, Pages.Count).All(rotations.ContainsKey);
        if (completeApplicationState) return;
        rotations.Clear();
        for (int index = 0; index < Pages.Count; index++)
            rotations[index] = Pages[index].Rotation;
    }

    private readonly Dictionary<int, (int Width, int Height)?> _pageImagePixels = [];
    private PdfPageContentReader? _contentReader;   // made once, under the lock, since it reads the page tree

    /// <summary>The pixels of the one image that fills a page, turned as the page is shown, or null
    /// for any other page. Reading a page costs more than a footer update should, so each is read once.
    /// A job reading many pages off the UI thread and the footer can ask at the same time.</summary>
    internal (int Width, int Height)? PageImagePixels(int pageIndex, IReadOnlyDictionary<int, int>? rotations = null)
    {
        (int Width, int Height)? pixels;
        lock (_pageImagePixels)
        {
            if (!_pageImagePixels.TryGetValue(pageIndex, out pixels))
            {
                try { pixels = PdfPageRasterInformation.ReadPageImageSize(_contentReader ??= new PdfPageContentReader(Document), Pages, pageIndex); }
                catch (Exception error) when (error is not OutOfMemoryException) { pixels = null; }
                _pageImagePixels[pageIndex] = pixels;
            }
        }
        if (pixels is not { } size) return null;
        var (width, height) = VisualPageSize(pageIndex, rotations);
        return OutputPixelDimensions.Oriented(size, width, height);
    }

    /// <summary>The resolution each page's export keeps when it stays at its own image's pixels, or the
    /// fallback for a page with no one image. Reads page content, so a job runs it off the UI thread.</summary>
    internal Dictionary<int, double> NativeDpis(IEnumerable<int> pages, IReadOnlyDictionary<int, int>? rotations, double fallback) =>
        pages.ToDictionary(page => page, page =>
        {
            var (width, height) = PhysicalPageSize(page, rotations);
            return OutputPixelDimensions.NativeDpi(PageImagePixels(page, rotations), width, height, fallback);
        });

    internal (double Width, double Height) PhysicalPageSize(
        int pageIndex, IReadOnlyDictionary<int, int>? rotations = null)
    {
        var (width, height) = VisualPageSize(pageIndex, rotations);
        return (width * Pages[pageIndex].UserUnit, height * Pages[pageIndex].UserUnit);
    }

    internal (double Width, double Height) VisualPageSize(
        int pageIndex, IReadOnlyDictionary<int, int>? rotations = null)
    {
        if (pageIndex < 0 || pageIndex >= Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageInformation page = Pages[pageIndex];
        int rotation = rotations?.TryGetValue(pageIndex, out int stored) == true
            ? ((stored % 360) + 360) % 360 : page.Rotation;
        return rotation is 90 or 270
            ? (page.Height, page.Width) : (page.Width, page.Height);
    }
}
