using System.IO;
using KillerPdf.Engine.Documents;

namespace KillerPDF.Services;

/// <summary>Owns an immutable engine document and lazily caches extraction for its pages.</summary>
internal sealed class PdfContentDocument : IDisposable
{
    private readonly PdfPageContentReader _reader;
    private readonly Dictionary<int, PdfPageContent> _pages = [];
    private PdfContentDocument(byte[] source)
    {
        _reader = new PdfPageContentReader(PdfDocument.Open(source, string.Empty));
    }
    internal static PdfContentDocument Open(string path) => new(File.ReadAllBytes(path));
    internal static PdfContentDocument Open(byte[] source) => new(source);
    internal int NumberOfPages => _reader.PageCount;
    internal PdfPageContent GetPage(int number)
    {
        if (number < 1 || number > NumberOfPages) throw new ArgumentOutOfRangeException(nameof(number));
        if (!_pages.TryGetValue(number, out var page)) _pages[number] = page = _reader.Read(number - 1);
        return page;
    }
    public void Dispose() => _pages.Clear();
}
