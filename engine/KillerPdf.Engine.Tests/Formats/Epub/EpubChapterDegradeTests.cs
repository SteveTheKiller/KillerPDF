using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubChapterDegradeTests
{
    [Fact]
    public void MissingSmallCapsFeatureKeepsOrdinarySelectableTextAndReportsTheFallback()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p style='font-variant:small-caps'>A</p>") } }.Build());
        var layout = book.CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal("A", Assert.Single(layout.Pages).Text.Trim());
        Assert.Contains("small capitals", Assert.Single(layout.ContentDiagnostics).Message);
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = true, DiagnosticReported = diagnostics.Add })));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Contains("small capitals", Assert.Single(diagnostics).Message);
    }

    [Fact]
    public void ActualEncryptedBytesAreReportedAsEncryptionBeforeStylesheetScanning()
    {
        var book = EpubDocument.Open(new Book
        {
            Encryption = """
                <encryption xmlns="urn:oasis:names:tc:opendocument:xmlns:container" xmlns:enc="http://www.w3.org/2001/04/xmlenc#">
                <enc:EncryptedData><enc:EncryptionMethod Algorithm="http://www.w3.org/2001/04/xmlenc#aes128-cbc"/>
                <enc:CipherData><enc:CipherReference URI="OEBPS/text/c0.xhtml"/></enc:CipherData></enc:EncryptedData></encryption>
                """,
            Chapters = { "\0\u0001encrypted bytes", Chapter("<p>A</p>") }
        }.Build());

        var layout = book.CreateLayout(Options());

        Assert.Equal(2, layout.Pages.Count);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(EpubOmissionReason.Encrypted, diagnostic.Reason);
        Assert.Contains("OEBPS/text/c0.xhtml", diagnostic.Message);
        Assert.Equal("A", layout.Pages[1].Text.Trim());
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = true, DiagnosticReported = diagnostics.Add })));
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        Assert.Equal(EpubOmissionReason.Encrypted, Assert.Single(diagnostics).Reason);
    }
}
