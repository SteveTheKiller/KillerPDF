using System.IO.Compression;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubPackageDegradeTests
{
    [Fact]
    public void OversizedChapterKeepsItsOutlineTargetAndResolvesToItsDiagnosisPage()
    {
        var source = new Book { Chapters = { Chapter("<p>" + new string('A', 9 * 1024 * 1024) + "</p>"), Chapter("<p>A</p>") } };
        var book = EpubDocument.Open(source.Build());

        string target = Assert.Single(book.ReadNavigation()).Target;

        Assert.Equal("OEBPS/text/c0.xhtml", target);
        var layout = book.CreateLayout(Options());
        Assert.Equal(0, layout.ResolveNavigationTarget(target).PageIndex);
        Assert.Equal(0, layout.ResolveNavigationTarget(target + "#unavailable-anchor").PageIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OversizedSpineChapterKeepsItsPlaceAndObeysTheIsolationPolicy(bool isolate)
    {
        var source = new Book { Chapters = { Chapter("<p>" + new string('A', 9 * 1024 * 1024) + "</p>"), Chapter("<p>A</p>") } };
        var book = EpubDocument.Open(source.Build());
        var options = new EpubLayoutOptions(400, 600, Options().DefaultFont) { IsolateChapterFailures = isolate };
        var diagnostics = new List<EpubChapterDiagnostic>();
        var conversion = new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = isolate, DiagnosticReported = diagnostics.Add };
        Assert.Equal(2, book.Spine.Count);
        Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.ReadText(0)).Message);
        if (!isolate)
        {
            Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.CreateLayout(options)).Message);
            Assert.Contains("OEBPS/text/c0.xhtml", Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(conversion)).Message);
            return;
        }

        var layout = book.CreateLayout(options);
        Assert.Equal(2, layout.Pages.Count);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", diagnostic.Href);
        Assert.Equal(EpubOmissionReason.LimitExceeded, diagnostic.Reason);
        Assert.Equal("A", layout.Pages[1].Text.Trim());
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(conversion)));
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        var reported = Assert.Single(diagnostics);
        Assert.Equal("OEBPS/text/c0.xhtml", reported.Href);
        Assert.Contains(reported.Href, reported.Message);
        Assert.Equal(EpubOmissionReason.LimitExceeded, reported.Reason);
    }

    [Theory]
    [InlineData("META-INF/container.xml")]
    [InlineData("OEBPS/content.opf")]
    public void OversizedRequiredPackageDocumentsStillRefuseOpening(string path)
    {
        byte[] source = new Book { Chapters = { Chapter("<p>A</p>") } }.Build();
        using var data = new MemoryStream();
        data.Write(source);
        using (var zip = new ZipArchive(data, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(path)!.Delete();
            using var entry = zip.CreateEntry(path).Open();
            entry.Write(new byte[9 * 1024 * 1024]);
        }
        Assert.Contains(path, Assert.Throws<InvalidDataException>(() => EpubDocument.Open(data.ToArray())).Message);
    }

    [Theory]
    [InlineData("remote")]
    [InlineData("name%25.dat")]
    [InlineData("name:odd.dat")]
    public void UnusableOptionalNamesDoNotPreventOpeningOrConversion(string name)
    {
        var source = new Book
        {
            Chapters = { Chapter("<p>A</p>") },
            ExtraManifest = name == "remote" ? "<item id='remote' href='https://example.invalid/font.ttf' media-type='font/ttf'/>" : ""
        };
        if (name != "remote") source.Resources.Add((name, [1, 2, 3]));

        var book = EpubDocument.Open(source.Build());

        Assert.Single(book.Spine);
        Assert.Empty(book.CreateLayout(Options()).ChapterDiagnostics);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void UnusedOversizedResourceDoesNotPreventOpeningOrConversion()
    {
        var source = new Book { Chapters = { Chapter("<p>A</p>") } };
        source.Resources.Add(("unused.dat", new byte[9 * 1024 * 1024]));

        var book = EpubDocument.Open(source.Build());

        Assert.Single(book.Spine);
        Assert.Empty(book.CreateLayout(Options()).ChapterDiagnostics);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }
}
