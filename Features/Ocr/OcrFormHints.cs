using KillerPdf.Engine.Documents;

namespace KillerPDF.Features
{
    /// <summary>The form widgets form-aware OCR uses as hints, read from the file once for the whole run instead of
    /// opening and parsing the file again for every page. A file whose widgets cannot be read gives no hints for any
    /// page, as a failed read of one page did before.</summary>
    internal sealed class OcrFormHints(Func<IReadOnlyList<IReadOnlyList<PdfFormWidgetInfo>>> readAll)
    {
        private IReadOnlyList<IReadOnlyList<PdfFormWidgetInfo>>? _pages;

        internal IReadOnlyList<PdfFormWidgetInfo> ForPage(int pageIndex)
        {
            _pages ??= Read();
            return (uint)pageIndex < (uint)_pages.Count ? _pages[pageIndex] : [];
        }

        private IReadOnlyList<IReadOnlyList<PdfFormWidgetInfo>> Read()
        {
            try { return readAll(); }
            // Any failure of the reader gives no hints, as in 1.9.2. Running out of memory and cancellation are
            // not the reader's failures and still stop the run.
            catch (Exception error) when (error is not (OutOfMemoryException or OperationCanceledException))
            { return []; }
        }
    }
}
