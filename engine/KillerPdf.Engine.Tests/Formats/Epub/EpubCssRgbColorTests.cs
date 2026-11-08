using System.Globalization;
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

public sealed class EpubCssRgbColorTests
{
    [Theory]
    [InlineData(false, "normal")]
    [InlineData(true, "NoRmAl")]
    public void NormalFontStyleKeepsTheSelectedUprightFontAndLiteralInkAtBothSeams(bool inline, string value)
        => Verify("red", new PdfRgbColor(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255],
            css: inline ? null : "p{font-style:" + value + "}", paragraphStyle: inline ? "font-style:" + value : null);

    [Theory]
    [InlineData("oblique 10deg")]
    [InlineData("oblique")]
    [InlineData("sideways")]
    // Oblique selects the italic face and other values are ignored. With no italic face resolved,
    // text falls back to the upright default face instead of refusing the chapter.
    public void UnsupportedFontStylesFallBackToTheUprightFace(string value)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(30, 16, font) { Margin = 2, FontSize = 10 };
        var book = EpubDocument.Open(Book("<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Style</title></head>"
            + "<body><p style='font-style:" + value + "'>A</p></body></html>"));
        EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book("<html xmlns='http://www.w3.org/1999/xhtml'><head><title>Style</title></head>"
            + "<body><p>A</p></body></html>")).CreateLayout(options), book.CreateLayout(options));
        Assert.NotEmpty(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    [Fact]
    public void InitialTextColorResetsInheritedRedToTheReaderBlackAtBothSeams()
        => Verify("initial", new PdfRgbColor(0, 0, 0), [0, 0, 0, 1], [0, 0, 0, 255], bodyStyle: "color:red");

    [Theory]
    [InlineData("p{color:red}p{color:initial}", true)]
    [InlineData("p{color:initial}p{color:red}", false)]
    [InlineData("body p{color:initial}p{color:red}", true)]
    [InlineData("p{color:INITIAL}", true)]
    public void InitialColorKeepsSourceOrderSpecificityAndInheritance(string css, bool black)
        => Verify(null, black ? new PdfRgbColor(0, 0, 0) : new PdfRgbColor(1, 0, 0),
            black ? [0, 0, 0, 1] : [1, 0, 0, 1], black ? [0, 0, 0, 255] : [0, 0, 255, 255],
            css: css, bodyStyle: "color:red");

    [Theory]
    [InlineData("grey")]
    [InlineData("GrEy")]
    [InlineData("gray")]
    public void GreyKeywordRetainsItsStandardLiteralRgbValueAtBothSeams(string color)
        => Verify(color, new PdfRgbColor(0.5019607843137255, 0.5019607843137255, 0.5019607843137255),
            [0.2158605f, 0.2158605f, 0.2158605f, 1], [128, 128, 128, 255]);

    [Fact]
    public void GreyBackgroundKeepsTextColorIndependentAtBothSeams()
        => Verify("red", new PdfRgbColor(1, 0, 0), [1, 0, 0, 1], [0, 0, 255, 255],
            bodyStyle: "background-color:grey", background: [0.2158605f, 0.2158605f, 0.2158605f, 1],
            pdfBackground: [128, 128, 128, 255]);

    [Fact]
    public void IntegerRgbPreservesLiteralPlacementNativeInkAndConvertedPdf()
    {
        Verify("rgb(180,180,180)", new PdfRgbColor(0.7058823529411765, 0.7058823529411765, 0.7058823529411765),
            [0.45641103f, 0.45641103f, 0.45641103f, 1], [180, 180, 180, 255]);
    }

    [Fact]
    public void PercentageRgbRetainsSubBytePrecisionThroughNativeRendering()
    {
        Verify("rgb(50.01%,50.01%,50.01%)", new PdfRgbColor(0.5001, 0.5001, 0.5001),
            [0.21413372f, 0.21413372f, 0.21413372f, 1], [128, 128, 128, 255]);
    }

    [Theory]
    [InlineData("rgb(0,0,0)", 0, 0f, 0)]
    [InlineData("rgb(255,255,255)", 1, 1f, 255)]
    [InlineData("RgB( +128 , +128 , +128 )", 0.5019607843137255, 0.2158605f, 128)]
    [InlineData("rgb(&#x9;+128&#xA;,&#xD;128,128&#x9;)", 0.5019607843137255, 0.2158605f, 128)]
    [InlineData("rgb(-10,-1,-999)", 0, 0f, 0)]
    [InlineData("rgb(256,999,1000000)", 1, 1f, 255)]
    public void IntegerRangesAndAsciiCaseUseCssChannelScale(string css, double channel, float linear, byte encoded)
    {
        Verify(css, new PdfRgbColor(channel, channel, channel), [linear, linear, linear, 1], [encoded, encoded, encoded, 255]);
    }

    [Theory]
    [InlineData("en-US", "rgb(-20%,+50%,120%)")]
    [InlineData("fr-FR", "RGB( -20.5% , +50.0% , 120.25% )")]
    [InlineData("tr-TR", "rgb(0%,50%,100%)")]
    [InlineData("ar-SA", "rgb(-.5%,+50.00%,100%)")]
    public void PercentageScaleAndClampingAreIndependentOfLocale(string culture, string color)
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            Verify(color, new PdfRgbColor(0, 0.5, 1), [0, 0.21404114f, 1, 1], [255, 128, 0, 255]);
        }
        finally { CultureInfo.CurrentCulture = original; }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void BodyBackgroundAndInheritedPackagedOrInlineColorsKeepTheirCascade(bool packaged, bool overridden)
    {
        const string declarations = "color:rgb(128,0,0);background-color:rgb(0,0,255)";
        Verify(null, overridden ? new PdfRgbColor(1, 1, 1) : new PdfRgbColor(0.5019607843137255, 0, 0),
            overridden ? [1, 1, 1, 1] : [0.2158605f, 0, 0, 1],
            overridden ? [255, 255, 255, 255] : [0, 0, 128, 255],
            packaged ? "body{" + declarations + "}" : null, packaged ? null : declarations,
            overridden ? "color:rgb(100%,100%,100%)" : null,
            [0, 0, 1, 1], [255, 0, 0, 255]);
    }

    [Theory]
    [InlineData("rgb()")]
    [InlineData("rgb(1,2)")]
    [InlineData("rgb(1,,3)")]
    [InlineData("rgb(1,2,3,)")]
    [InlineData("rgb(1%,2,3%)")]
    [InlineData("rgb(1,2%,3)")]
    [InlineData("rgb(1.,2,3)")]
    [InlineData("rgb(1.%,2%,3%)")]
    [InlineData("rgb(.%,2%,3%)")]
    [InlineData("rgb(+,2,3)")]
    [InlineData("rgb(--1,2,3)")]
    [InlineData("rgb(NaN,2,3)")]
    [InlineData("rgb(Infinity,2,3)")]
    [InlineData("rgb(-Infinity%,2%,3%)")]
    [InlineData("rgb(1 0,2,3)")]
    [InlineData("rgb(1 %,2%,3%)")]
    [InlineData("rgb(1px,2,3)")]
    [InlineData("rgb(1,2,3)junk")]
    [InlineData("rgb(1,2,3))")]
    [InlineData("rgb(1,2,3")]
    [InlineData("rgb (1,2,3)")]
    // CSS Color 4 makes these invalid, so the declaration is ignored and the default color stays.
    public void MalformedColorFormsAreIgnored(string color)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        var control = EpubDocument.Open(Book("<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>RGB</title></head><body><p>A</p></body></html>")).CreateLayout(options);
        foreach (string property in new[] { "color", "background-color" })
        {
            byte[] source = Book("<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>RGB</title></head><body style=\""
                + property + ":" + color + "\"><p>A</p></body></html>");
            byte[] original = source.ToArray();
            var book = EpubDocument.Open(source);
            EpubCompatibilityTests.AssertSameLayout(control, book.CreateLayout(options));
            Assert.NotEmpty(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
            Assert.Equal(original, source);
        }
    }

    // CSS Color 4 valid forms. Text has no compositing here, so alpha paints opaque.
    [Theory]
    [InlineData("rgb(1,2,3,4)", 1, 2, 3)]
    [InlineData("rgb(1.0,2,3)", 1, 2, 3)]
    [InlineData("rgb(1e2,2,3)", 100, 2, 3)]
    [InlineData("rgb(1 2 3)", 1, 2, 3)]
    [InlineData("rgb(1 2 3 / 0.5)", 1, 2, 3)]
    [InlineData("rgba(1,2,3,0.5)", 1, 2, 3)]
    [InlineData("hsl(120,100%,50%)", 0, 255, 0)]
    [InlineData("#0f0", 0, 255, 0)]
    [InlineData("#00ff0080", 0, 255, 0)]
    [InlineData("rebeccapurple", 0x66, 0x33, 0x99)]
    public void ModernColorFormsResolve(string color, int red, int green, int blue)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = EpubDocument.Open(Book("<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>RGB</title></head><body style=\"color:"
            + color + "\"><p>A</p></body></html>")).CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(new PdfRgbColor(red / 255d, green / 255d, blue / 255d), Assert.Single(layout.Pages[0].TextPlacements).Color);
    }

    private static void Verify(string? color, PdfRgbColor expected, float[] linear, byte[] encoded,
        string? css = null, string? bodyStyle = null, string? paragraphStyle = null,
        float[]? background = null, byte[]? pdfBackground = null)
    {
        string chapter = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>RGB</title>"
            + (css is null ? "" : "<link rel=\"stylesheet\" href=\"book.css\"/>")
            + "</head><body" + (bodyStyle is null ? "" : " style=\"" + bodyStyle + "\"")
            + "><p" + (paragraphStyle is null ? "" : " style=\"" + paragraphStyle + "\"")
            + "><span" + (color is null ? "" : " style=\"color:" + color + "\"") + ">A</span></p></body></html>";
        byte[] source = Book(chapter, css);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapterBytes = book.ReadResource("EPUB/text.xhtml");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 });
        var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) { Color = expected }, placement);
        var frame = layout.RenderPage(0);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[(5 * 30 + 7) * 4 + channel], linear[channel] - 0.0000003f, linear[channel] + 0.0000003f);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        {
            PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5
        }));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(encoded, rendered.Pixels.Slice((5 * 30 + 7) * 4, 4).ToArray());
        float[] expectedBackground = background ?? [1, 1, 1, 1];
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[15 * 30 * 4 + channel], expectedBackground[channel] - .0000003f,
                expectedBackground[channel] + .0000003f);
        Assert.Equal(pdfBackground ?? [255, 255, 255, 255], rendered.Pixels.Slice(15 * 30 * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(chapterBytes, book.ReadResource("EPUB/text.xhtml"));
        if (css is not null) Assert.Equal(Encoding.UTF8.GetBytes(css), book.ReadResource("EPUB/book.css"));
    }

    private static byte[] Book(string chapter, string? css = null)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:rgb</dc:identifier><dc:title>RGB</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/>"
                + (css is null ? "" : "<item id=\"css\" href=\"book.css\" media-type=\"text/css\"/>")
                + "</manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", chapter);
            if (css is not null) Add("EPUB/book.css", css);
            void Add(string path, string text)
            {
                var entry = zip.CreateEntry(path, CompressionLevel.NoCompression);
                entry.LastWriteTime = new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero);
                using var stream = entry.Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
