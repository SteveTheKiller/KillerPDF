using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubSemanticContainerTests
{
    [Fact]
    public void NestedBlockquotesCollapseVerticalMarginsAndAccumulateHorizontalInsets()
    {
        byte[] source = Book("<blockquote><blockquote><p>A</p></blockquote></blockquote><p>A</p>", "p{margin:0}"),
            original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(200, 60, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new double[] { 82, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 12, 32 }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 150, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new double[] { 61.5, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new double[] { 30, 15 }, letters.Select(letter => letter.StartBaseLine.Y));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("font-size:20px", 42, 22, 62, 46.5, 22.5)]
    [InlineData("margin:0", 2, 2, 12, 67.5, 60)]
    [InlineData("margin:initial", 2, 2, 12, 67.5, 60)]
    [InlineData("margin:unset", 2, 2, 12, 67.5, 60)]
    [InlineData("margin-left:INITIAL", 2, 12, 32, 60, 45)]
    [InlineData("margin-top:unset", 42, 2, 22, 67.5, 52.5)]
    [InlineData("margin:0;margin-left:3px;margin-top:5px", 5, 7, 17, 63.75, 56.25)]
    public void BlockquoteAuthorStylesOverrideDefaultsInNativeAndSavedPdf(string declarations,
        double firstLeft, double firstTop, double secondTop, double firstBaseline, double secondBaseline)
    {
        byte[] source = Book("<blockquote><p>A</p></blockquote><p>A</p>",
            "p{margin:0}blockquote{" + declarations + "}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(160, 100, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(new double[] { firstLeft, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { firstTop, secondTop }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 120, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { firstLeft * .75, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new double[] { firstBaseline, secondBaseline }, letters.Select(letter => letter.StartBaseLine.Y));
        Assert.Equal(original, source);
    }

    [Fact]
    public void BlockquoteDefaultsUseEmVerticalMarginsAndFortyPixelHorizontalInsets()
    {
        byte[] source = Book("<blockquote><p>A</p></blockquote><p>A</p>", "p{margin:0}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(120, 60, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new double[] { 42, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 12, 32 }, page.TextPlacements.Select(item => item.Top));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((15 * 120 + 47) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((5 * 120 + 47) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 90, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new double[] { 31.5, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new double[] { 30, 15 }, letters.Select(letter => letter.StartBaseLine.Y));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(120, 60));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 120 + 47) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("article")]
    [InlineData("aside")]
    [InlineData("footer")]
    [InlineData("main")]
    [InlineData("nav")]
    [InlineData("section")]
    public void SemanticGroupingElementsKeepNormalBlockFlowAndTheirOwnTypeSelectors(string element)
    {
        byte[] source = Book("<" + element + "><p>A</p></" + element + "><p>A</p>",
            "p{margin:0}" + element + "{color:red}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Equal("A\n\nA", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal(new PdfRgbColor(1, 0, 0), page.TextPlacements[0].Color);
        Assert.Null(page.TextPlacements[1].Color);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new[] { "A", "A" }, new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 26));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HeaderDisplayOverridesPreserveStructuralTextAndOnlyChangeVisibleFlow(bool hidden)
    {
        byte[] source = Book("<header>A</header>A", "header{color:red;display:" + (hidden ? "none" : "inline") + "}"),
            original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Equal("A\n\nA", book.ReadText(0));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(30, 26, font)
        { FontSize = 10, Margin = 2 }).Pages);
        Assert.Equal(hidden ? new double[] { 2 } : [2, 8], page.TextPlacements.Select(item => item.Left));
        Assert.All(page.TextPlacements, item => Assert.Equal(2, item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(hidden ? "A" : "AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<span><header>A</header></span>")]
    [InlineData("<p><header>A</header></p>")]
    [InlineData("<header xmlns='urn:other'><p>A</p></header>")]
    public void UnsupportedHeaderContextsKeepExplicitRefusal(string body)
    {
        byte[] source = Book(body, "p{margin:0}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(30, 26, font)
        { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(original, source);
    }

    [Fact]
    public void HeaderMetadataAndBoxStylesKeepTheirSourceContainerAndDestination()
    {
        byte[] source = Book("<header id='lead' class='chosen'><p>A</p></header><p>A</p>",
            "p{margin:0}header#lead.chosen{width:12px;margin-left:3px;background-color:blue;color:red}");
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new double[] { 5, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new EpubNavigationDestination(0, 0, 0, 5, 2), layout.ResolveNavigationTarget("doc.xhtml#lead"));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((5 * 30 + 16) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((15 * 30 + 16) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(new double[] { 3.75, 1.5 }, new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.X));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 26));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rendered.Pixels.Slice((5 * 30 + 16) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void BodyHeaderKeepsBlockFlowAndItsOwnSelectorIdentityInBothOutputs()
    {
        byte[] source = Book("<header><p>A</p></header><p>A</p>", "p{margin:0}header{color:red}");
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Equal("A\n\nA", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 26, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\n\nA", page.Text);
        Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal(new PdfRgbColor(1, 0, 0), page.TextPlacements[0].Color);
        Assert.Null(page.TextPlacements[1].Color);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 12, 4.5 }, letters.Select(letter => letter.StartBaseLine.Y));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 26));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 30 + 7) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    private static byte[] Book(string body, string css)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>semantic</dc:identifier><dc:title>Containers</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("style.css", css);
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><link rel='stylesheet' href='style.css'/></head><body>" + body + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
