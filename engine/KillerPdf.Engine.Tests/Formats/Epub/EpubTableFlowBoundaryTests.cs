using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubTableFlowBoundaryTests
{
    [Theory]
    [InlineData("div")]
    [InlineData("table")]
    [InlineData("tr")]
    public void PaddedCellUnderABackgroundPreservesTextInLayoutAndConversion(string background)
    {
        string body = "<div" + Style("div") + "><table style='width:100px;"
            + (background == "table" ? "background-color:red" : "") + "'><tr" + Style("tr")
            + "><td style='width:100px'><p style='padding:2px'>A</p></td></tr></table></div>";
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(body) } }.Build());
        var layout = book.CreateLayout(Options());
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal("A", Assert.Single(layout.Pages).Text.Trim());
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font()))));
        Assert.Equal(1, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));

        string Style(string tag) => background == tag ? " style='background-color:red'" : "";
    }

    [Theory]
    [InlineData("ul")]
    [InlineData("ol")]
    public void TableInsideListItemDrawsOneMarkerBesideItsFirstRow(string list)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<" + list
            + "><li><table style='width:100px'><tr><td style='width:100px'>Cell</td></tr></table></li></"
            + list + ">") } }.Build());
        var font = Font();
        var layout = book.CreateLayout(new EpubLayoutOptions(400, 600, font)
        { FontSize = 16, Margin = 16, IsolateChapterFailures = true });
        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Contains("Cell", page.Text);
        Assert.Equal(1, page.Markers.Count + page.Glyphs.OfType<NativeMarkerPaint>().Count());
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font))));
        Assert.Equal(1, reader.PageCount);
        Assert.Contains("Cell", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
    }

    private static TrueTypeFont Font()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TrueTypeFont.Load(bytes.ToArray());
    }
}
