using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The sidebar's thumbnail renderer lets go of its session each time its queue drains and opens a new one on
// the next scroll. Opening reads the whole page tree, which on a very large document costs far more than
// drawing a few rows, so the page information is read once per parsed document and shared.
public sealed class ThumbnailRenderSessionReuseTests
{
    [Fact]
    public void ReopeningARenderSessionForTheSameDocumentDoesNotReadItsPagesAgain()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".pdf");
        try
        {
            File.WriteAllBytes(path, Document());
            // The tab keeps the parse alive for as long as it is open.
            var document = PdfPageRenderSession.OpenDocument(path, null);
            using (var first = PdfPageRenderSession.OpenEngineFirst(path, 128, 256, null))
                Assert.Equal(2, first.PageCount);
            using var second = PdfPageRenderSession.OpenEngineFirst(path, 128, 256, null);

            Assert.Equal(2, second.PageCount);
            Assert.Same(PdfPageRenderSession.SizedPages(document), PdfPageRenderSession.SizedPages(document));
            Assert.Equal(1, PdfPageRenderSession.PageInformationReads(document));
        }
        finally { PdfPageRenderSession.ForgetDocument(path); File.Delete(path); }
    }

    private static byte[] Document() => new PdfDocumentBuilder()
        .AddPage(200, 300, new PdfContentStreamBuilder().SetFillRgb(0.2, 0.7, 0.3).Rectangle(10, 10, 100, 150).Fill())
        .AddPage(300, 200, new PdfContentStreamBuilder().SetFillRgb(0.7, 0.2, 0.3).Rectangle(10, 10, 150, 100).Fill()).Build();
}