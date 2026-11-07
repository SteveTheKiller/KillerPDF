using System.IO.Compression;
using System.Buffers.Binary;
using System.Text;
using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubDisplayFormattingTests
{
    [Theory]
    [InlineData("scripted")]
    [InlineData("cover-image scripted")]
    public void CoverImageMetadataDoesNotAdmitOtherManifestProperties(string properties)
    {
        byte[] source = Book(false, "<img src='image.png' alt='Cover'/>", "img{display:block}", RedImage(), properties), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void CoverImageManifestPropertyDoesNotPreventNativePaintingOrConversion()
    {
        byte[] source = Book(false, "<img src='image.png' alt='Cover' title='Cover'/>", "img{display:block}", RedImage(), "cover-image");
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Single(new PdfPageContentReader(pdf).Read(0).Images);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DisplayNoneCoverImageIsNotDecodedOrPainted()
    {
        byte[] image = [1, 2, 3];
        byte[] source = Book(false, "<img src='image.png' alt='Cover' title='Cover'/>", "img{display:none}", image), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        Assert.Empty(Assert.Single(layout.Pages).TextPlacements);
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Empty(content.Images);
        Assert.Empty(content.Letters);
        Assert.Equal(image, book.ReadResource("image.png"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("https://example.com/cover.png")]
    [InlineData("missing.png")]
    public void DirectBodyImageStillRejectsExternalOrMissingResources(string reference)
    {
        byte[] source = Book(false, "<img src='" + reference + "' alt='Cover' title='Cover'/>", ""), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<img src='image.png' alt='Cover' title='Cover image'/>")]
    [InlineData("<div><img src='image.png' alt='Cover' title='Cover image'/></div>")]
    public void UnstyledCoverImageUsesInlineBaselineInBodyOrContainer(string body)
    {
        byte[] source = Book(false, body, "", RedImage()), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        Assert.Empty(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((8 * 40 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Empty(content.Letters);
        var image = Assert.Single(content.Images);
        Assert.Equal(1.5, image.BoundingBox.Left, 6);
        Assert.Equal(21, image.BoundingBox.Top, 6);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<img src='image.png' alt='Cover'/>")]
    [InlineData("<div><img src='image.png' alt='Cover'/></div>")]
    [InlineData("<img src='image.png' alt='Cover' title='Cover image'/>")]
    public void CoverImageCanBePlacedDirectlyInBodyOrContainer(string body)
    {
        byte[] source = Book(false, body, "img{display:block}", RedImage()), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        Assert.Empty(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Empty(content.Letters);
        var image = Assert.Single(content.Images);
        Assert.Equal(1.5, image.BoundingBox.Left, 6);
        Assert.Equal(25.5, image.BoundingBox.Top, 6);
        Assert.Equal(preserved, source);
    }

    public EpubDisplayFormattingTests(ITestOutputHelper output) => output.WriteLine("Loaded engine SHA256: " +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));

    [Fact]
    public void VisibilityHiddenBlockImageRetainsHeightWithoutPaintingOrPdfImageContent()
    {
        byte[] image = RedImage();
        byte[] source = Book(false, "<p><img src='image.png' alt=''/>A</p>", "img{display:block;visibility:hidden}", image);
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        Assert.Equal(4, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Empty(content.Images);
        Assert.Equal("A", Assert.Single(content.Letters).Value);
        Assert.Equal(image, book.ReadResource("image.png"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayNoneImageAddsNoSpacePaintOrConvertedImage(bool malformed)
    {
        byte[] image = malformed ? [1, 2, 3] : RedImage();
        byte[] source = Book(false, "<p>A<img src='image.png' alt=''/>A</p>", "img{display:none}", image);
        byte[] preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", page.Text);
        Assert.Equal(2, page.TextPlacements.Count);
        Assert.Equal(8, page.TextPlacements[1].Left, 6);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var content = new PdfPageContentReader(pdf).Read(0);
        Assert.Empty(content.Images);
        Assert.Equal("AA", string.Concat(content.Letters.Select(letter => letter.Value)));
        Assert.Equal(6, content.Letters[1].StartBaseLine.X, 6);
        Assert.Equal(image, book.ReadResource("image.png"));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void BlockImageUsesItsIntrinsicHeightWithoutAnInlineFontStrut()
    {
        byte[] image = RedImage();
        byte[] source = Book(false, "<p><img src='image.png' alt=''/>A</p>", "img{display:block}", image);
        byte[] preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("A", book.ReadText(0, deadline.Token));
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 }, deadline.Token);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 4, 6, 10), Assert.Single(Assert.Single(layout.Pages).TextPlacements));
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((3 * 40 + 3) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(1.5, letter.StartBaseLine.X); Assert.Equal(18, letter.StartBaseLine.Y);
        var placed = Assert.Single(reader.Read(0).Images);
        Assert.Equal(2, placed.PixelWidth); Assert.Equal(2, placed.PixelHeight);
        Assert.Equal(1.5, placed.BoundingBox.Left); Assert.Equal(3, placed.BoundingBox.Right);
        Assert.Equal(24, placed.BoundingBox.Bottom); Assert.Equal(25.5, placed.BoundingBox.Top);
        var converted = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 36), deadline.Token);
        Assert.Empty(converted.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, converted.Pixels.Slice((2 * 40 + 2) * 4, 4).ToArray());
        Assert.Equal(image, book.ReadResource("image.png"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<p>A </p><p>A</p>")]
    [InlineData("<p>A</p><p> A</p>")]
    public void InlineParagraphSourceWhitespaceSurvivesWithoutInventingExtractionCharacters(string body)
    {
        byte[] source = Book(false, body, "p{display:inline}");
        byte[] preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("A\n\nA", book.ReadText(0, deadline.Token));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(40, 36, Font())
        { FontSize = 10, Margin = 2 }, deadline.Token).Pages);
        Assert.Equal("A\n\nA", page.Text);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 13 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(new double[] { 1.5, 9.75 }, letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(new double[] { 19.5, 19.5 }, letters.Select(item => item.StartBaseLine.Y));
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 36), deadline.Token).Diagnostics);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void InlineParagraphWrappingMeasuresTheWholeVisualWordAcrossStructuralSeparators()
    {
        byte[] source = Book(false, "<p>A A</p><p>AA</p>", "p{display:inline}");
        byte[] preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("A A\n\nAA", book.ReadText(0, deadline.Token));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(22, 26, Font())
        { FontSize = 10, Margin = 2 }, deadline.Token).Pages);
        Assert.Equal("A A\n\nAA", page.Text);
        Assert.Equal(new[] { 0, 2, 5, 6 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 2, 8, 14 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 12, 12, 12 }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 16.5, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(new double[] { 1.5, 1.5, 6, 10.5 }, letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(new double[] { 12, 4.5, 4.5, 4.5 }, letters.Select(item => item.StartBaseLine.Y));
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(22, 26), deadline.Token).Diagnostics);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("p{display:inline}", true)]
    [InlineData("p{display:inline}p{display:block}", false)]
    [InlineData("p.note{display:inline}p{display:block}", true)]
    [InlineData("p.note{display:block}p{display:inline}", false)]
    public void ParagraphDisplayKeepsStructuralTextAndFollowsTheCascade(string css, bool inline)
    {
        byte[] source = Book(false, "<p class='note'>A</p><p class='note'>A</p>", css);
        byte[] preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("A\n\nA", book.ReadText(0, deadline.Token));
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(40, 36, Font())
        { FontSize = 10, Margin = 2 }, deadline.Token).Pages);
        Assert.Equal("A\n\nA", page.Text);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(inline ? new double[] { 2, 8 } : [2, 2], page.TextPlacements.Select(item => item.Left));
        Assert.Equal(inline ? new double[] { 2, 2 } : [2, 22], page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(inline ? new double[] { 1.5, 6 } : [1.5, 1.5], letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(inline ? new double[] { 19.5, 19.5 } : [19.5, 4.5], letters.Select(item => item.StartBaseLine.Y));
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 36), deadline.Token).Diagnostics);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void CollapsedSpacesAtBlockBoundariesRetainExtractionWithoutPaintingEmptyAdvances()
    {
        byte[] source = Book(true, "<p>A <span class='block'> A </span> A</p>");
        byte[] preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("A A A", book.ReadText(0, deadline.Token));
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A A A", page.Text);
        Assert.Equal(new[] { 0, 2, 4 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 2, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 12, 22 }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(new double[] { 1.5, 1.5, 1.5 }, letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(new double[] { 19.5, 12, 4.5 }, letters.Select(item => item.StartBaseLine.Y));
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 36), deadline.Token).Diagnostics);
        Assert.Equal(preserved, source);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedBlockRetainsSourceIndicesAndPaintsActualBoxFragments(bool block)
    {
        byte[] source = Book(block), preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("AAAAA", book.ReadText(0, deadline.Token));
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 36, Font()) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AAAAA", page.Text);
        Assert.Equal(new[] { 0, 1, 2, 3, 4 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(block ? new double[] { 2, 8, 2, 2, 8 } : [2, 8, 14, 20, 26], page.TextPlacements.Select(item => item.Left));
        Assert.Equal(block ? new double[] { 2, 2, 12, 22, 22 } : [2, 2, 2, 2, 2], page.TextPlacements.Select(item => item.Top));
        Assert.All(page.TextPlacements, item => { Assert.Equal(6, item.Width); Assert.Equal(10, item.Height); });
        var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
        Pixel(0, 0, [1, 1, 1, 1]);
        if (block)
        {
            Pixel(9, 11, [0, 1, 0, 1]);
            Pixel(30, 11, [1, 1, 1, 1]);
            Pixel(30, 21, [1, 0, 0, 1]);
            Pixel(3, 31, [0, 1, 0, 1]);
            Pixel(30, 31, [1, 1, 1, 1]);
        }
        else
        {
            Pixel(9, 11, [0, 1, 0, 1]);
            Pixel(15, 11, [1, 0, 0, 1]);
            Pixel(30, 21, [1, 1, 1, 1]);
        }
        Assert.Equal(preserved, source);
        void Pixel(int x, int y, float[] expected) => Assert.Equal(expected, frame.Pixels.Slice((y * 40 + x) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedBlockConversionReopensWithSourceTextBoxGeometryAndFragmentColors(bool block)
    {
        byte[] source = Book(block), preserved = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("AAAAA", book.ReadText(0, deadline.Token));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Font())
        { PageWidthPoints = 30, PageHeightPoints = 27, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A", "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(block ? new double[] { 1.5, 6, 1.5, 1.5, 6 } : [1.5, 6, 10.5, 15, 19.5], letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(block ? new double[] { 19.5, 19.5, 12, 4.5, 4.5 } : [19.5, 19.5, 19.5, 19.5, 19.5], letters.Select(item => item.StartBaseLine.Y));
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 36), deadline.Token);
        Assert.Empty(frame.Diagnostics);
        Pixel(0, 0, [255, 255, 255, 255]);
        if (block)
        {
            Pixel(9, 11, [0, 255, 0, 255]);
            Pixel(30, 11, [255, 255, 255, 255]);
            Pixel(30, 21, [0, 0, 255, 255]);
            Pixel(3, 31, [0, 255, 0, 255]);
            Pixel(30, 31, [255, 255, 255, 255]);
        }
        else
        {
            Pixel(9, 11, [0, 255, 0, 255]);
            Pixel(15, 11, [0, 0, 255, 255]);
            Pixel(30, 21, [255, 255, 255, 255]);
        }
        Assert.Equal(preserved, source);
        void Pixel(int x, int y, byte[] expected) => Assert.Equal(expected, frame.Pixels.Slice((y * 40 + x) * 4, 4).ToArray());
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));

    private static byte[] Book(bool block, string? body = null, string? css = null, byte[]? image = null, string imageProperties = "")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>formatting</dc:identifier><dc:title>Formatting</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='text.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("text.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head><link rel='stylesheet' href='style.css'/></head><body>" +
                (body ?? "<p>A<span class='outer'>A<span class='block'>A</span>A</span>A</p>") + "</body></html>");
            Add("style.css", css ?? "span.outer{background-color:rgb(0,255,0)}span.block{background-color:rgb(255,0,0);" + (block ? "display:block;" : "") + "}");
            if (image is not null)
            {
                using var stream = zip.CreateEntry("image.png", CompressionLevel.NoCompression).Open();
                stream.Write(image);
            }
            void Add(string name, string text)
            {
                if (image is not null && name == "package.opf")
                    text = text.Replace("</manifest>", "<item id='image' href='image.png' media-type='image/png' properties='" + imageProperties + "'/></manifest>");
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return output.ToArray();
    }

    private static byte[] RedImage()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(new byte[] { 0, 255, 0, 0, 255, 0, 0, 0, 255, 0, 0, 255, 0, 0 });
        Chunk("IDAT", compressed.ToArray()); Chunk("IEND", []);
        return output.ToArray();
        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length); output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type); output.Write(name); output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc); output.Write(integer);
        }
    }
}
