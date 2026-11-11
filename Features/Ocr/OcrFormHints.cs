using System.IO;
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
            // The reader's own failures give no hints; running out of memory is not one of them and stops the run.
            catch (Exception error) when (error is FormatException or InvalidOperationException or NotSupportedException
                or ArgumentException or IOException or InvalidDataException or OverflowException or UnauthorizedAccessException
                or KillerPdf.Engine.Filters.PdfFilterException) { return []; }
        }
    }
}
