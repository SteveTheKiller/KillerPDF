using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubBodyMarginTests
{
    [Theory]
    [InlineData("p{margin-left:10%;padding-left:5%}", 22, 16.5)]
    [InlineData("p{font-size:8px;margin-left:1em;padding-left:.5em}", 22, 16.5)]
    [InlineData("p{margin:0 20px 0 4px;margin-left:12px;padding-left:4px}", 26, 19.5)]
    public void ParagraphInsetsResolveAgainstContainingWidthAndOwnFont(string css, double left, double pdfLeft)
    {
        var actual = Observe("", css);
        Assert.Equal(left, Assert.Single(actual.Placements).Left, 6);
        Assert.Equal(pdfLeft, Assert.Single(actual.Letters).Left, 6);
    }

    [Fact]
    public void ParagraphHorizontalMarginsInsetTextAndBackgroundInNativeAndSavedPdf()
    {
        var actual = Observe("", "p{margin:0 20px 0 12px;background-color:rgb(255,0,0)}");
        Assert.Equal(22, Assert.Single(actual.Placements).Left, 6);
        Assert.Equal(10, actual.Placements[0].Top, 6);
        Assert.Equal(16.5, Assert.Single(actual.Letters).Left, 6);
        foreach (int x in new[] { 21, 70 })
        {
            Assert.Equal(new float[] { 1, 1, 1, 1 }, actual.NativePixels.AsSpan((10 * 100 + x) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, actual.PdfPixels.AsSpan((10 * 100 + x) * 4, 4).ToArray());
        }
        foreach (int x in new[] { 22, 69 })
        {
            Assert.Equal(new float[] { 1, 0, 0, 1 }, actual.NativePixels.AsSpan((10 * 100 + x) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, actual.PdfPixels.AsSpan((10 * 100 + x) * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData("margin:0")]
    [InlineData("margin:0px")]
    [InlineData("margin:0 0px 0em 0%")]
    [InlineData("margin-top:0;margin-right:0;margin-bottom:0;margin-left:0")]
    public void ZeroMarginsPreserveNativeAndIndependentlyReopenedConversion(string style)
    {
        var control = Observe("", text: "ALPHA", portableFont: true);
        var actual = Observe(style, text: "ALPHA", portableFont: true);
        Assert.Equal(control.Placements, actual.Placements);
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.Letters, actual.Letters);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void BodyEmMarginsMoveNativeInkAndConvertedTextWithinConfiguredPageMargins()
    {
        var control = Observe("");
        var actual = Observe("font-size:16px;margin:0.5em 1em 0 0.5em");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 18, 18, 9.6, 16), Assert.Single(actual.Placements));
        Assert.Equal(13.5, Assert.Single(actual.Letters).Left, 5);
        Assert.Equal(Assert.Single(control.Letters).Bottom - 6, Assert.Single(actual.Letters).Bottom, 5);
        for (int y = 8; y < 80; y++)
            for (int x = 8; x < 100; x++)
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.Equal(control.NativePixels[((y - 8) * 100 + x - 8) * 4 + channel],
                        actual.NativePixels[(y * 100 + x) * 4 + channel]);
                    Assert.Equal(control.PdfPixels[((y - 8) * 100 + x - 8) * 4 + channel],
                        actual.PdfPixels[(y * 100 + x) * 4 + channel]);
                }
    }

    [Fact]
    public void EveryPercentageMarginUsesConfiguredContainingBlockWidth()
    {
        var actual = Observe("margin:10% 20% 0 5%");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 14, 18, 9.6, 16), Assert.Single(actual.Placements));
        Assert.Equal(10.5, Assert.Single(actual.Letters).Left, 5);
        var literal = Observe("margin:8px 16px 0 4px");
        Assert.Equal(literal.NativePixels, actual.NativePixels);
        Assert.Equal(literal.Letters, actual.Letters);
        Assert.Equal(literal.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void PackagedMarginCascadeResolvesEachSideBeforeInlineLonghandAndDoesNotInherit()
    {
        const string css = "body.pick{margin:4px 6px 8px 10px;margin-left:12px}"
            + "body{margin:1px}body.pick{margin-top:8px}";
        var actual = Observe("margin-left:4px", css, bodyClass: "pick");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 14, 18, 9.6, 16), Assert.Single(actual.Placements));
        Assert.Equal(10.5, Assert.Single(actual.Letters).Left, 5);
        var literal = Observe("margin:8px 6px 8px 4px");
        Assert.Equal(literal.NativePixels, actual.NativePixels);
        Assert.Equal(literal.Letters, actual.Letters);
        Assert.Equal(literal.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void HorizontalBodyMarginsReduceWholeWordWrappingWidthInNativeAndConversion()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var control = EpubDocument.Open(Book("", "", "A A", ""))
            .CreateLayout(new EpubLayoutOptions(100, 80, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, Assert.Single(control.Pages).TextPlacements.Count);
        Assert.Equal(27.6, control.Pages[0].TextPlacements[2].Left, 5);
        var actual = Observe("margin:0 30px", text: "A A", convertedText: "AA");
        Assert.Equal(new[] { 0, 2 }, actual.Placements.Select(placement => placement.Start));
        Assert.All(actual.Placements, placement =>
        {
            Assert.Equal(0, placement.SpineIndex);
            Assert.Equal(1, placement.Length);
            Assert.Equal(40, placement.Left, 5);
            Assert.Equal(9.6, placement.Width, 5);
            Assert.Equal(16, placement.Height, 5);
        });
        Assert.Equal(10, actual.Placements[0].Top, 5);
        Assert.Equal(26, actual.Placements[1].Top, 5);
        Assert.All(actual.Letters, letter => Assert.Equal(30, letter.Left, 5));
        Assert.Equal(12, actual.Letters[0].Bottom - actual.Letters[1].Bottom, 5);
        foreach (int y in new[] { 14, 30 })
        {
            Assert.Equal(new float[] { 0, 0, 0, 1 }, actual.NativePixels.AsSpan((y * 100 + 48) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, actual.PdfPixels.AsSpan((y * 100 + 48) * 4, 4).ToArray());
        }
        Assert.Equal(new float[] { 1, 1, 1, 1 }, actual.NativePixels.AsSpan((14 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, actual.PdfPixels.AsSpan((14 * 100 + 70) * 4, 4).ToArray());
    }

    [Fact]
    public void BodyTopIsNotRepeatedOnContinuationAndTerminalBottomDoesNotReserveEachPage()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var nativeFrames = new List<float[]>();
        var pdfFrames = new List<byte[]>();
        foreach (string style in new[] { "margin:8px 0 0 4px", "margin:8px 0 30px 4px" })
        {
            byte[] source = Book(style, "", "A<br/>A<br/>A<br/>A", "");
            byte[] original = source.ToArray();
            var book = EpubDocument.Open(source);
            var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
            Assert.Equal(2, layout.Pages.Count);
            Assert.Equal("A\nA\nA\nA", string.Concat(layout.Pages.Select(page => page.Text)));
            Assert.Equal(new[] { 0, 2, 4, 6 }, layout.Pages.SelectMany(page => page.TextPlacements).Select(placement => placement.Start));
            Assert.Equal(new[] { 2, 2 }, layout.Pages.Select(page => page.TextPlacements.Count));
            double[] tops = [18, 34, 10, 26];
            var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
            for (int index = 0; index < 4; index++)
            {
                Assert.Equal(14, placements[index].Left, 5);
                Assert.Equal(tops[index], placements[index].Top, 5);
            }
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
            { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
            var reader = new PdfPageContentReader(pdf);
            Assert.Equal(2, reader.PageCount);
            var letters = Enumerable.Range(0, 2).Select(index => reader.Read(index).Letters).ToArray();
            Assert.All(letters, page => Assert.Equal("AA", string.Concat(page.Select(letter => letter.Value))));
            Assert.All(letters.SelectMany(page => page), letter => Assert.Equal(10.5, letter.BoundingBox.Left, 5));
            Assert.Equal(6, letters[1][0].BoundingBox.Bottom - letters[0][0].BoundingBox.Bottom, 5);
            Assert.All(PdfPageInformation.Read(pdf), page =>
            {
                Assert.Equal(75, page.PhysicalWidth, 5);
                Assert.Equal(45, page.PhysicalHeight, 5);
            });
            for (int page = 0; page < 2; page++)
            {
                var native = layout.RenderPage(page);
                var rendered = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
                Assert.Empty(rendered.Diagnostics);
                int y = page == 0 ? 22 : 14;
                Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((y * 100 + 22) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((y * 100 + 22) * 4, 4).ToArray());
                if (nativeFrames.Count < 2)
                {
                    nativeFrames.Add(native.Pixels.ToArray());
                    pdfFrames.Add(rendered.Pixels.ToArray());
                }
                else
                {
                    Assert.Equal(nativeFrames[page], native.Pixels.ToArray());
                    Assert.Equal(pdfFrames[page], rendered.Pixels.ToArray());
                }
            }
            Assert.Equal(original, source);
        }
    }

    [Theory]
    [InlineData("margin:8px", 18, 18, 13.5)]
    [InlineData("margin:8px 16px", 26, 18, 19.5)]
    [InlineData("margin:8px 16px 0", 26, 18, 19.5)]
    [InlineData("margin:8px 16px 0 4px", 14, 18, 10.5)]
    [InlineData("margin-left:24px;margin:8px 16px 0 4px", 14, 18, 10.5)]
    [InlineData("margin:8px 16px 0 4px;margin-left:12px", 22, 18, 16.5)]
    [InlineData("margin-left:12px;margin-left:4px", 14, 10, 10.5)]
    [InlineData("MARGIN:+.5EM", 18, 18, 13.5)]
    public void ShorthandArityAndDeclarationOrderProduceLiteralOrigins(string style, double left, double top, double pdfLeft)
    {
        var actual = Observe(style);
        Assert.Equal(left, Assert.Single(actual.Placements).Left, 5);
        Assert.Equal(top, Assert.Single(actual.Placements).Top, 5);
        Assert.Equal(pdfLeft, Assert.Single(actual.Letters).Left, 5);
    }

    [Fact]
    public void BodyEmUsesItsOwnComputedFontSizeBeforeSmallerDescendants()
    {
        var actual = Observe("margin:.5em;font-size:32px", "p{font-size:8px}");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 26, 26, 4.8, 8), Assert.Single(actual.Placements));
        Assert.Equal(19.5, Assert.Single(actual.Letters).Left, 5);
    }

    [Fact]
    public void InsetsPreserveCanvasParagraphSpanAndGlyphPaintingOrder()
    {
        var actual = Observe("margin:8px 16px 0 8px;background-color:rgb(0,255,0)",
            "p{background-color:rgb(255,0,0)}span{background-color:rgb(0,0,255);color:rgb(255,255,255)}");
        AssertPixel(5, 33, [0, 1, 0, 1], [0, 255, 0, 255]);
        AssertPixel(50, 33, [1, 0, 0, 1], [0, 0, 255, 255]);
        AssertPixel(19, 33, [0, 0, 1, 1], [255, 0, 0, 255]);
        AssertPixel(26, 22, [1, 1, 1, 1], [255, 255, 255, 255]);
        void AssertPixel(int x, int y, float[] native, byte[] converted)
        {
            Assert.Equal(native, actual.NativePixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
            Assert.Equal(converted, actual.PdfPixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData("margin:auto")]
    [InlineData("margin:-1px")]
    [InlineData("margin:-.5em")]
    [InlineData("margin:-1%")]
    [InlineData("margin:1")]
    [InlineData("margin:1rem")]
    [InlineData("margin:1in")]
    [InlineData("margin:calc(1px)")]
    [InlineData("margin:inherit")]
    [InlineData("margin:NaNpx")]
    [InlineData("margin:Infinity%")]
    [InlineData("margin:1.px")]
    [InlineData("margin:1e2px")]
    [InlineData("margin:0 0 0 0 0")]
    [InlineData("margin-left:0 0")]
    [InlineData("margin:40px")]
    [InlineData("margin-top:60px")]
    [InlineData("margin-bottom:61px")]
    [InlineData("margin-left:101%")]
    [InlineData("margin-left:999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999999px")]
    public void UnsupportedOrOversizedMarginsAreDiagnosedWithoutChangingSource(string style)
    {
        byte[] source = Book(style, "", "A", "");
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(100, 80, font)
        { Margin = 10, FontSize = 16 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("p{margin:1px}")]
    [InlineData("span{margin-left:1px}")]
    public void NonzeroChildMarginsRemainExplicitlyUnsupported(string css)
    {
        byte[] source = Book("margin:0", css, "A", "");
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var native = Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(100, 80, font)
        { Margin = 10, FontSize = 16 }));
        Assert.Equal("Native EPUB nonzero margins are implemented only on the body.", native.Message);
        var converted = Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(native.Message, converted.Message);
        Assert.Equal(original, source);
    }

    private sealed record Result(EpubTextPlacement[] Placements, float[] NativePixels,
        (string Text, double Left, double Bottom)[] Letters, byte[] PdfPixels);

    private static Result Observe(string style, string css = "", string text = "A", string bodyClass = "", string? convertedText = null,
        bool portableFont = false)
    {
        byte[] source = Book(style, css, text, bodyClass);
        byte[] original = source.ToArray();
        using var fontStream = portableFont ? typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")! : new MemoryStream(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        using var fontBytes = new MemoryStream();
        fontStream.CopyTo(fontBytes);
        var font = TrueTypeFont.Load(fontBytes.ToArray());
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 80, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(convertedText ?? text, string.Concat(letters.Select(letter => letter.Value)));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 80));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(original, source);
        Assert.Equal(75, Assert.Single(PdfPageInformation.Read(pdf)).PhysicalWidth, 5);
        return new(page.TextPlacements.ToArray(), native.Pixels.ToArray(),
            letters.Select(letter => (letter.Value, letter.BoundingBox.Left, letter.BoundingBox.Bottom)).ToArray(), rendered.Pixels.ToArray());
    }

    private static byte[] Book(string style, string css, string text, string bodyClass)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:margin</dc:identifier><dc:title>Margin</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"book.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Margin</title><link rel=\"stylesheet\" href=\"book.css\"/></head><body class=\"" + bodyClass + "\" style=\"" + style + "\"><p><span>" + text + "</span></p></body></html>");
            Add("EPUB/book.css", css);
            void Add(string path, string value)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return buffer.ToArray();
    }
}
