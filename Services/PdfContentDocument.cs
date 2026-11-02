using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Owns an immutable engine document and lazily caches extraction for its pages.</summary>
internal sealed class PdfContentDocument : IDisposable
{
    private readonly PdfPageContentReader _reader;
    private readonly Dictionary<int, PdfPageContent> _pages = [];
    private PdfContentDocument(byte[] source, OpenedProtection? protection)
    {
        // The page renders through compatibility recovery, so its text layer reads the same way, and
        // under the protection of the tab whose file it is.
        _reader = new PdfPageContentReader(PdfEngineIntegration.OpenForReading(source, protection));
    }
    internal static PdfContentDocument Open(string path, OpenedProtection? protection) => new(File.ReadAllBytes(path), protection);
    internal static PdfContentDocument Open(byte[] source, OpenedProtection? protection) => new(source, protection);
    internal int NumberOfPages => _reader.PageCount;
    internal PdfPageContent GetPage(int number)
    {
        if (number < 1 || number > NumberOfPages) throw new ArgumentOutOfRangeException(nameof(number));
        if (!_pages.TryGetValue(number, out var page)) _pages[number] = page = _reader.Read(number - 1);
        return page;
    }
    public void Dispose() => _pages.Clear();
}
