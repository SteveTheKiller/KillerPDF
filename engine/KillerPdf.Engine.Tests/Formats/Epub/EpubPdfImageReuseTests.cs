using System.Text;
using System.Text.RegularExpressions;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// A picture shown on several pages is stored once per decoded size, not once per page.
public sealed class EpubPdfImageReuseTests
{
    private static int ImageObjects(byte[] pdf) =>
        Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Subtype\s*/Image").Count;

    [Fact]
    public void ThePictureOnEveryPageIsOneImageObjectInTheConvertedPdf()
    {
        const string body = "<p><img src='../images/logo.png' alt=''/></p>";
        var source = new Book
        {
            Chapters = { Chapter(body), Chapter(body), Chapter(body) },
            ExtraManifest = "<item id=\"logo\" href=\"images/logo.png\" media-type=\"image/png\"/>"
        };
        source.Resources.Add(("images/logo.png", EpubImageCompatibilityTests.Png(20, 20, 2)));
        var book = EpubDocument.Open(source.Build());

        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { PageWidthPoints = 150, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 });

        var pdf = PdfDocument.Open(converted);
        Assert.Equal(3, new PdfPageContentReader(pdf).PageCount);
        Assert.Equal(1, ImageObjects(converted));
        for (int page = 0; page < 3; page++) Assert.Single(new PdfPageContentReader(pdf).Read(page).Images);
    }

    [Fact]
    public void TwoDifferentPicturesStayTwoImageObjects()
    {
        var source = new Book
        {
            Chapters = { Chapter("<p><img src='../images/a.png' alt=''/></p>"), Chapter("<p><img src='../images/b.png' alt=''/></p>") },
            ExtraManifest = "<item id=\"a\" href=\"images/a.png\" media-type=\"image/png\"/><item id=\"b\" href=\"images/b.png\" media-type=\"image/png\"/>"
        };
        source.Resources.Add(("images/a.png", EpubImageCompatibilityTests.Png(20, 20, 2)));
        source.Resources.Add(("images/b.png", EpubImageCompatibilityTests.Png(30, 20, 2)));

        byte[] converted = EpubDocument.Open(source.Build()).ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { PageWidthPoints = 150, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 });

        Assert.Equal(2, ImageObjects(converted));
    }
}
