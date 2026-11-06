using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubBodyBoxTests
{
    [Theory]
    [InlineData("border:0")]
    [InlineData("border:0px")]
    [InlineData("border:none")]
    [InlineData("border:0 solid rgb(255,0,0)")]
    [InlineData("border:2px solid rgb(255,0,0);border:0")]
    [InlineData("border:2px solid rgb(255,0,0);border:none")]
    [InlineData("border:1px")]
    public void ZeroUsedBordersPreserveNativeAndIndependentlyReopenedPdf(string style)
    {
        var control = Observe("", text: "ALPHA", portableFont: true);
        var actual = Observe(style, text: "ALPHA", portableFont: true);
        Assert.Equal(control.Placements, actual.Placements);
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.Letters, actual.Letters);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
    }

    [Theory]
    [InlineData("padding:0")]
    [InlineData("padding:0px")]
    [InlineData("padding:0 0px 0em 0%")]
    [InlineData("padding-top:0;padding-right:0;padding-bottom:0;padding-left:0")]
    public void ZeroPaddingPreservesNativeAndIndependentlyReopenedPdf(string style)
    {
        var control = Observe("", text: "ALPHA", portableFont: true);
        var actual = Observe(style, text: "ALPHA", portableFont: true);
        Assert.Equal(control.Placements, actual.Placements);
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.Letters, actual.Letters);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void BodyPaddingMovesNativeInkAndIndependentlyConvertedGlyphsWithoutInheritance()
    {
        var control = Observe("");
        var actual = Observe("padding:8px 16px 0 4px");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 14, 18, 9.6, 16), Assert.Single(actual.Placements));
        Assert.Equal(10.5, Assert.Single(actual.Letters).Left, 5);
        Assert.Equal(Assert.Single(control.Letters).Bottom - 6, Assert.Single(actual.Letters).Bottom, 5);
        for (int y = 8; y < 80; y++)
            for (int x = 4; x < 100; x++)
                for (int channel = 0; channel < 4; channel++)
                {
                    Assert.Equal(control.NativePixels[((y - 8) * 100 + x - 4) * 4 + channel],
                        actual.NativePixels[(y * 100 + x) * 4 + channel]);
                    Assert.Equal(control.PdfPixels[((y - 8) * 100 + x - 4) * 4 + channel],
                        actual.PdfPixels[(y * 100 + x) * 4 + channel]);
                }
    }

    [Theory]
    [InlineData("padding:8px", 18, 18, 13.5)]
    [InlineData("padding:8px 16px", 26, 18, 19.5)]
    [InlineData("padding:8px 16px 0", 26, 18, 19.5)]
    [InlineData("padding:8px 16px 0 4px", 14, 18, 10.5)]
    [InlineData("padding:.5em 1em 0 .25em", 14, 18, 10.5)]
    [InlineData("padding:10% 20% 0 5%", 14, 18, 10.5)]
    [InlineData("padding-left:24px;padding:8px 16px 0 4px", 14, 18, 10.5)]
    [InlineData("padding:8px 16px 0 4px;padding-left:12px", 22, 18, 16.5)]
    [InlineData("PADDING:+.5EM", 18, 18, 13.5)]
    public void PaddingLengthsAndShorthandHaveLiteralOrigins(string style, double left, double top, double pdfLeft)
    {
        var actual = Observe(style);
        Assert.Equal(left, Assert.Single(actual.Placements).Left, 5);
        Assert.Equal(top, Assert.Single(actual.Placements).Top, 5);
        Assert.Equal(pdfLeft, Assert.Single(actual.Letters).Left, 5);
    }

    [Fact]
    public void PercentagePaddingUsesCallerContainingWidthBeforeBodyMargins()
    {
        var actual = Observe("margin:0 8px;padding:10% 20% 0 5%");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 22, 18, 9.6, 16), Assert.Single(actual.Placements));
        Assert.Equal(16.5, Assert.Single(actual.Letters).Left, 5);
        var literal = Observe("margin:0 8px;padding:8px 16px 0 4px");
        Assert.Equal(literal.NativePixels, actual.NativePixels);
        Assert.Equal(literal.Letters, actual.Letters);
        Assert.Equal(literal.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void PaddingCascadeResolvesEachSideBeforeInlineLonghandsWithoutInheritance()
    {
        const string css = "body.pick{padding:4px 6px 8px 10px;padding-left:12px}"
            + "body{padding:1px}body.pick{padding-top:8px}";
        var actual = Observe("padding-left:4px", css, bodyClass: "pick");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 14, 18, 9.6, 16), Assert.Single(actual.Placements));
        var literal = Observe("padding:8px 6px 8px 4px");
        Assert.Equal(literal.NativePixels, actual.NativePixels);
        Assert.Equal(literal.Letters, actual.Letters);
        Assert.Equal(literal.PdfPixels, actual.PdfPixels);
        var ownFont = Observe("padding:.5em;font-size:32px", "p{font-size:8px}");
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 26, 26, 4.8, 8), Assert.Single(ownFont.Placements));
        Assert.Equal(19.5, Assert.Single(ownFont.Letters).Left, 5);
    }

    [Fact]
    public void PaddingConsumesWidthInNativeWrappingAndIndependentConversion()
    {
        var actual = Observe("padding:0 30px", text: "A A", convertedText: "AA");
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

    [Theory]
    [InlineData("padding:-1px")]
    [InlineData("padding:1")]
    [InlineData("padding:auto")]
    [InlineData("padding:inherit")]
    [InlineData("padding:1rem")]
    [InlineData("padding:NaNpx")]
    [InlineData("padding:Infinity%")]
    [InlineData("padding:1.px")]
    [InlineData("padding:1e2px")]
    [InlineData("padding:0 0 0 0 0")]
    [InlineData("padding-left:0 0")]
    [InlineData("padding:40px")]
    [InlineData("padding-left:101%")]
    [InlineData("padding-top:60px")]
    [InlineData("font-size:0")]
    [InlineData("line-height:0")]
    public void InvalidOrOversizedBoxAndTypographyValuesRemainExplicitlyUnsupported(string style)
    {
        AssertUnsupported(style);
    }

    [Theory]
    [InlineData("border:2px solid rgb(255,0,0)")]
    [InlineData("border:0;border:2px solid red")]
    [InlineData("border:solid")]
    public void VisibleBordersAreExplicitlyUnsupported(string style)
    {
        AssertUnsupported(style, expected: "Native EPUB visible borders are not implemented.");
    }

    [Fact]
    public void BorderCascadeResetsEarlierVisibleSettingsAtTheWinningDeclaration()
    {
        var actual = Observe("border:0", "body.pick{border:2px solid red}body{border:solid}", bodyClass: "pick");
        var control = Observe("");
        Assert.Equal(control.Placements, actual.Placements);
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.Letters, actual.Letters);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
        AssertUnsupported("", "body.pick{border:2px solid red}body{border:0}", "pick",
            "Native EPUB visible borders are not implemented.");
    }

    [Theory]
    [InlineData("p{padding:1px}")]
    [InlineData("span{padding-left:1px}")]
    public void PositiveChildPaddingIsExplicitlyUnsupported(string css)
    {
        AssertUnsupported("", css, expected: "Native EPUB nonzero padding is implemented only on the body.");
    }

    [Fact]
    public void TerminalPaddingOverflowRemainsAnExplicitUnsupportedBoundary()
    {
        AssertUnsupported("padding-bottom:50px", expected: "Native EPUB terminal body padding overflow is not implemented.");
        var actual = Observe("padding-bottom:44px");
        var control = Observe("");
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
    }

    [Fact]
    public void InitialTopAndFittingTerminalBottomPaddingAreNotClonedAtPageBreaks()
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var nativeFrames = new List<float[]>();
        var pdfFrames = new List<byte[]>();
        foreach (string style in new[] { "padding:8px 0 0 4px", "padding:8px 0 8px 4px" })
        {
            byte[] source = Book(style, "", "A<br/>A<br/>A<br/>A", "");
            byte[] original = source.ToArray();
            var book = EpubDocument.Open(source);
            var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
            Assert.Equal(2, layout.Pages.Count);
            Assert.Equal("A\nA\nA\nA", string.Concat(layout.Pages.Select(page => page.Text)));
            Assert.Equal(new[] { 2, 2 }, layout.Pages.Select(page => page.TextPlacements.Count));
            var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
            Assert.Equal(new[] { 0, 2, 4, 6 }, placements.Select(placement => placement.Start));
            double[] tops = [18, 34, 10, 26];
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

    private static void AssertUnsupported(string style, string css = "", string bodyClass = "", string? expected = null)
    {
        byte[] source = Book(style, css, "A", bodyClass);
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var native = Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(100, 80, font)
        { Margin = 10, FontSize = 16 }));
        var converted = Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(native.Message, converted.Message);
        if (expected is not null) Assert.Equal(expected, native.Message);
        Assert.Equal(original, source);
    }

    private sealed record Result(EpubTextPlacement[] Placements, float[] NativePixels,
        (string Text, double Left, double Bottom)[] Letters, byte[] PdfPixels);

    private static Result Observe(string style, string css = "", string text = "A", string bodyClass = "",
        string? convertedText = null, bool portableFont = false)
    {
        byte[] source = Book(style, css, text, bodyClass);
        byte[] original = source.ToArray();
        using var stream = portableFont ? typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")! : new MemoryStream(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var font = TrueTypeFont.Load(bytes.ToArray());
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 80, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        var native = layout.RenderPage(0);
        Assert.Equal(100, native.Width);
        Assert.Equal(80, native.Height);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(convertedText ?? text, string.Concat(letters.Select(letter => letter.Value)));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 80));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(original, source);
        var information = Assert.Single(PdfPageInformation.Read(pdf));
        Assert.Equal(75, information.PhysicalWidth, 5);
        Assert.Equal(60, information.PhysicalHeight, 5);
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
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:box</dc:identifier><dc:title>Box</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"book.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Box</title><link rel=\"stylesheet\" href=\"book.css\"/></head><body class=\"" + bodyClass + "\" style=\"" + style + "\"><p><span>" + text + "</span></p></body></html>");
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
