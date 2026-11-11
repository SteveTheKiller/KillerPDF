using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubConversionDegradeTests
{
    [Fact]
    public void EmptyNcxLabelOmitsBookmarksAndPreservesEveryPage()
    {
        var source = new Book
        {
            Nav = "<invalid/>", SpineToc = "ncx",
            ExtraManifest = "<item id='ncx' href='toc.ncx' media-type='application/x-dtbncx+xml'/>",
            Chapters = { Chapter("<p>A</p>"), Chapter("<p>A</p>") }
        };
        source.Resources.Add(("toc.ncx", Encoding.UTF8.GetBytes("""
            <ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1"><navMap>
            <navPoint id="p"><navLabel><text></text></navLabel><content src="text/c0.xhtml"/></navPoint>
            </navMap></ncx>
            """)));
        var book = EpubDocument.Open(source.Build());
        var diagnostics = new List<EpubChapterDiagnostic>();

        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { DiagnosticReported = diagnostics.Add }));

        Assert.Empty(PdfBookmarkReader.Read(pdf));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.All(Enumerable.Range(0, 2), index =>
            Assert.Equal("A", string.Concat(reader.Read(index).Letters.Select(letter => letter.Value))));
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(-1, diagnostic.SpineIndex);
        Assert.Equal("OEBPS/toc.ncx", diagnostic.Href);
        Assert.Equal(EpubOmissionReason.InvalidContent, diagnostic.Reason);
        Assert.Contains("Empty EPUB navigation label", diagnostic.Message);
    }
}
