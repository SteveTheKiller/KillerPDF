using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubImageDegradeTests
{
    [Theory]
    [InlineData("<img/>")]
    [InlineData("<img src=''/>")]
    [InlineData("<img src='   '/>")]
    public void ImageWithoutUsableSourceIsOmittedAndItsParagraphSurvives(string image)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A" + image + "A</p>") } }.Build());
        Assert.Equal("AA", book.ReadText(0));

        var layout = book.CreateLayout(Options());

        EpubImageCompatibilityTests.AssertImageOmitted(layout, "source");
        Assert.Equal(EpubOmissionReason.MissingResource, Assert.Single(layout.ContentDiagnostics).Reason);
        Assert.Equal("AA", Assert.Single(layout.Pages).Text.Trim());
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { DiagnosticReported = diagnostics.Add })));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("AA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(EpubOmissionReason.MissingResource, Assert.Single(diagnostics).Reason);
    }

    // A picture over the 8 MiB entry limit is reported once, as too large, and never again as an unsupported format.
    [Fact]
    public void AnOversizedPictureIsReportedOnceAsTooLarge()
    {
        var source = new Book
        {
            Chapters = { Chapter("<p>A<img src='../images/big.png'/>A</p>") },
            ExtraManifest = "<item id='big' href='images/big.png' media-type='image/png'/>"
        };
        source.Resources.Add(("images/big.png", new byte[9 * 1024 * 1024]));
        var book = EpubDocument.Open(source.Build());

        var layout = book.CreateLayout(Options());

        var content = Assert.Single(layout.ContentDiagnostics);
        Assert.Equal(EpubOmissionReason.ImageTooLarge, content.Reason);
        var reported = new List<EpubChapterDiagnostic>();
        book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont) { DiagnosticReported = reported.Add });
        var diagnostic = Assert.Single(reported, item => item.Message.Contains("big.png"));
        Assert.Equal(EpubOmissionReason.ImageTooLarge, diagnostic.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PageHeightPictureInADefaultFigureDoesNotCreateAnEmptyLeadingPage(bool afterAnotherChapter)
    {
        var source = new Book
        {
            Chapters = { Chapter("<figure><img src='../images/c.png'/></figure>") },
            ExtraManifest = "<item id='image' href='images/c.png' media-type='image/png'/>"
        };
        source.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 56, 2)));
        if (afterAnotherChapter) source.Chapters.Insert(0, Chapter("<p>A</p>"));
        var book = EpubDocument.Open(source.Build());

        var layout = book.CreateLayout(Options(200, 60));

        Assert.Empty(layout.ChapterDiagnostics);
        var image = Assert.Single(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
        Assert.InRange(image.Top, 2, 58);
        Assert.InRange(image.Height, 1, 56);
        Assert.True(image.Top + image.Height <= 58);
        Assert.Equal(afterAnotherChapter ? 2 : 1, layout.Pages.Count);
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { PageWidthPoints = 150, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 })));
        Assert.Equal(afterAnotherChapter ? 2 : 1, reader.PageCount);
        Assert.Single(reader.Read(reader.PageCount - 1).Images);
    }

    [Fact]
    public void InlineSvgCoverIsOmittedWithoutDiscardingItsSurroundingText()
    {
        var source = new Book
        {
            Chapters = { Chapter("<p>A<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 20'>"
                + "<image width='10' height='20' href='../images/c.png'/></svg>A</p>") },
            ExtraManifest = "<item id='image' href='images/c.png' media-type='image/png'/>"
        };
        source.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 20, 2)));
        var book = EpubDocument.Open(source.Build());

        var layout = book.CreateLayout(Options());

        EpubImageCompatibilityTests.AssertImageOmitted(layout, "SVG");
        Assert.Equal("AA", Assert.Single(layout.Pages).Text.Trim());
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = true, DiagnosticReported = diagnostics.Add })));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("AA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Contains("SVG", Assert.Single(diagnostics).Message);
    }
}
