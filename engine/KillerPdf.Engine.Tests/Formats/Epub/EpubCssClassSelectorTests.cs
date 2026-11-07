using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubCssClassSelectorTests
{
    [Fact]
    public void IdSelectorStylesItsMatchingElementInNativeAndPdfPages()
        => Verify("#target{color:red}", "<p id='target'>A</p>", 2, 1.5, true, false);

    [Theory]
    [InlineData("p#target", "<p id='target'>A</p>", true)]
    [InlineData(".note#target", "<p class='note' id='target'>A</p>", true)]
    [InlineData("p#target.note", "<p class='note' id='target'>A</p>", true)]
    [InlineData("#target#target", "<p id='target'>A</p>", true)]
    [InlineData("#target#other", "<p id='target'>A</p>", false)]
    [InlineData("#Target", "<p id='target'>A</p>", false)]
    [InlineData("#target", "<p id='other target'>A</p>", false)]
    [InlineData("#target", "<p>A</p>", false)]
    [InlineData("#root #target", "<div id='root'><p id='target'>A</p></div>", true)]
    [InlineData("#root #target", "<div id='root'/><p id='target'>A</p>", false)]
    [InlineData("p[class~='#target']", "<p id='other' class='#target'>A</p>", true)]
    public void IdPredicatesRetainExactCompoundAndAncestorMeaning(string selector, string body, bool red)
        => Verify(selector + "{color:red}", body, 2, 1.5, red, false);

    [Theory]
    [InlineData("#target{color:red}p.note{color:lime}")]
    [InlineData("p.note{color:lime}#target{color:red}")]
    [InlineData("p#target{color:red}#target{color:lime}")]
    [InlineData("#target{color:lime}#target{color:red}")]
    [InlineData("#root p{color:red}p.note.note.note{color:lime}")]
    [InlineData("#target#target{color:red}p#target{color:lime}")]
    [InlineData("#root #target{color:red}p#target{color:lime}")]
    public void IdSpecificityOutranksClassesAndTypesAndKeepsSourceOrder(string css)
        => Verify(css, "<div id='root'><p class='note' id='target'>A</p></div>", 2, 1.5, true, false);

    [Fact]
    public void IdSpecificityCascadesEachPropertyIndependently()
        => Verify("#target{color:red}p.note{background-color:blue}", "<p class='note' id='target'>A</p>", 2, 1.5, true, true);

    [Theory]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p#target{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';#target{color:red}", true)]
    [InlineData("@namespace 'urn:other';#target{color:red}", false)]
    public void IdCompoundsKeepTheElementNamespaceConstraint(string css, bool red)
        => Verify(css, "<p id='target'>A</p>", 2, 1.5, red, false);

    [Fact]
    public void DescendantAncestorMissesRespectTheSelectorWorkBudget()
    {
        string body = "<p>" + string.Concat(Enumerable.Repeat("<span>", 120)) + "A"
            + string.Concat(Enumerable.Repeat("</span>", 120)) + "</p>";
        Verify("div.missing span{}", body, 2, 1.5, false, false);
        byte[] source = Book(string.Concat(Enumerable.Repeat("div.missing span{}", 4096)), body), preserved = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        const string diagnostic = "Native EPUB CSS selector matching exceeds its work limit.";
        Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
        Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<div class='outer'><div class='middle'><p><span class='caption'>A</span></p></div></div>", true)]
    [InlineData("<div class='middle'><div class='outer'><p><span class='caption'>A</span></p></div></div>", false)]
    [InlineData("<div class='outer middle'><p><span class='caption'>A</span></p></div>", false)]
    [InlineData("<div class='outer'><div class='middle'><div class='middle'><p><span class='caption'>A</span></p></div></div></div>", true)]
    public void DescendantChainsConsumeDistinctAncestorsInOrder(string body, bool styled)
        => Verify(".outer .middle span.caption{color:red}", body, 2, 1.5, styled, false);

    [Theory]
    [InlineData("div span.caption\u00A0")]
    [InlineData("\u00A0div span.caption")]
    [InlineData("div[class\u00A0] span")]
    public void NonCssWhitespaceDoesNotSilentlyChangeADescendantSelector(string selector)
        => UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams(selector);

    [Theory]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|div.figcenter h|span.caption{color:red}", true)]
    [InlineData("@namespace h 'urn:other';h|div.figcenter span.caption{color:red}", false)]
    [InlineData("@namespace h 'urn:other';div.figcenter h|span.caption{color:red}", false)]
    [InlineData("div[class~='figcenter'] span.caption{color:red}", true)]
    [InlineData("div[class~='figcenter other'] span.caption{color:red}", false)]
    public void DescendantCompoundsRetainNamespacesAndQuotedAttributeBoundaries(string css, bool styled)
        => Verify(css, "<div class='figcenter other'><p><span class='caption'>A</span></p></div>", 2, 1.5, styled, false);

    [Theory]
    [InlineData("body div p span{color:lime}.caption{color:red}")]
    [InlineData(".caption{color:red}body div p span{color:lime}")]
    [InlineData(".figcenter span{color:lime}div .caption{color:red}")]
    [InlineData("div.figcenter span.caption{color:red}span.caption{color:lime}")]
    public void DescendantSpecificityCountsClassesBeforeTypesAndBreaksTiesBySourceOrder(string css)
        => Verify(css, "<div class='figcenter'><p><span class='caption'>A</span></p></div>", 2, 1.5, true, false);

    [Theory]
    [InlineData("<div class='figcenter'><p><span class='caption'>A</span></p></div>", true)]
    [InlineData("<div><p><span class='caption'>A</span></p></div>", false)]
    [InlineData("<div class='figcenter'/><p><span class='caption'>A</span></p>", false)]
    [InlineData("<div class='figcenter'><p><span class='other'>A</span></p></div>", false)]
    [InlineData("<div class='caption'><p><span class='figcenter'>A</span></p></div>", false)]
    public void DescendantRulesRequireTheActualAncestorAndTarget(string body, bool styled)
        => Verify("div.figcenter span.caption{color:red}", body, 2, 1.5, styled, false);

    [Theory]
    [InlineData("div.figcenter span.caption")]
    [InlineData(".figcenter .caption")]
    [InlineData("div span")]
    [InlineData("p span")]
    public void DescendantRuleMatchesAcrossAnInterveningElement(string selector)
        => Verify(selector + "{color:red}",
            "<div class='figcenter'><p><span class='caption'>A</span></p></div>", 2, 1.5, true, false);

    [Theory]
    [InlineData("", false)]
    [InlineData("p.note{color:red;text-align:center;background-color:blue}", true)]
    [InlineData(".note{color:red;text-align:center;background-color:blue}", true)]
    public void ClassOnlyRulesPaintAndAlignRealParagraphs(string css, bool styled)
        => Verify(css, "<p class=\"note\">A</p>", styled ? 12 : 2, styled ? 9 : 1.5, styled, styled);

    [Theory]
    [InlineData(".note{color:red;background-color:blue}", "<p><span class=\"note\">A</span></p>", true, true)]
    [InlineData(".a.b{color:red}", "<p class=\"b a\">A</p>", true, false)]
    [InlineData(".a.b{color:red}", "<p class=\"a\">A</p>", false, false)]
    [InlineData(".a.a{color:red}", "<p class=\"a\">A</p>", true, false)]
    [InlineData(".note{color:red}", "<p class=\"notebook Note\">A</p>", false, false)]
    [InlineData(".note{color:red}", "<p class=\"other&#x9;note&#xA;last\">A</p>", true, false)]
    [InlineData(".unused,.note{color:red}", "<p class=\"note\">A</p>", true, false)]
    public void TokensCombinationsGroupsAndSpansUseActualClassMembership(string css, string body, bool red, bool blue)
        => Verify(css, body, 2, 1.5, red, blue);

    [Theory]
    [InlineData("p{color:lime}.note{color:red}")]
    [InlineData(".note{color:red}p{color:lime}")]
    [InlineData("p.note{color:red}.note{color:lime}")]
    [InlineData(".note{color:lime}p.note{color:red}")]
    [InlineData(".note.other{color:red}p.note{color:lime}")]
    [InlineData("p.note{color:lime}.note.other{color:red}")]
    [InlineData(".note{color:lime}.other{color:red}")]
    [InlineData(".other{color:lime}.note{color:red}")]
    [InlineData(".note.note{color:red}p.note{color:lime}")]
    public void ClassSpecificityAndSourceOrderPreserveIndependentCascade(string css)
        => Verify(css, "<p class=\"other note\">A</p>", 2, 1.5, true, false);

    [Fact]
    public void PropertiesCascadeIndependentlyAndInlineDeclarationsOverrideRules()
    {
        Verify("p.note{color:red}.note{text-align:center;background-color:blue}", "<p class=\"note\">A</p>", 12, 9, true, true);
        Verify(".note.other{color:lime;text-align:right;background-color:blue}",
            "<p class=\"other note\" style=\"color:red;text-align:center\">A</p>", 12, 9, true, true);
    }

    [Theory]
    [InlineData(".note{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';.note{color:red}", true)]
    [InlineData("@namespace 'urn:other';.note{color:red}", false)]
    [InlineData("@namespace '';.note{color:red}", false)]
    [InlineData("@namespace h 'urn:other';.note{color:red}", true)]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p.note{color:red}", true)]
    [InlineData("@namespace h 'urn:other';h|p.note{color:red}", false)]
    [InlineData("@namespace 'urn:other';*|p.note{color:red}", true)]
    [InlineData("@namespace 'http://www.w3.org/1999/xhtml';|p.note{color:red}", false)]
    [InlineData("@namespace h 'http://www.w3.org/1999/xhtml';h|p.note{color:red}.note{color:lime}", true)]
    public void ImpliedUniversalRespectsDefaultNamespaceAndQualifiedTypesKeepTheirMeaning(string css, bool red)
        => Verify(css, "<p class=\"note\">A</p>", 2, 1.5, red, false);

    [Theory]
    [InlineData(".")]
    [InlineData(".note.")]
    [InlineData(".note..other")]
    [InlineData(".1note")]
    [InlineData(".n\\6fte")]
    [InlineData(".n\u00f6te")]
    [InlineData(".note>.other")]
    [InlineData(".note+.other")]
    [InlineData(".note~.other")]
    [InlineData(".note[title]")]
    [InlineData(".note:first-child")]
    [InlineData("#")]
    [InlineData("##id")]
    [InlineData("#id#")]
    [InlineData("#1id")]
    [InlineData("#id..note")]
    [InlineData("h#id|p")]
    [InlineData("#id|p")]
    [InlineData("h|.note")]
    public void UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams(string selector)
    {
        VerifyEngine();
        string css = "@namespace h 'http://www.w3.org/1999/xhtml';" + selector + "{color:red}";
        var book = EpubDocument.Open(Book(css, "<p class=\"note\">A</p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        Assert.Equal("Native EPUB CSS selector is not implemented.", Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal("Native EPUB CSS selector is not implemented.", Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
    }

    [Fact]
    public void MatchingClassOnlyMarginsRemainAnExplicitLayoutBoundary()
    {
        VerifyEngine();
        var book = EpubDocument.Open(Book(".subhd{margin-top:0.5em}", "<p><span class=\"subhd\">A</span></p>"));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        const string diagnostic = "Native EPUB nonzero margins are implemented only on the body.";
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 })).Message);
        Assert.Equal(diagnostic, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font))).Message);
    }

    [Fact]
    public void ClassOnlySelectorsKeepCountLengthWorkAndCancellationBounds()
    {
        VerifyEngine();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        void Reject(string css, string body, string diagnostic)
        {
            var book = EpubDocument.Open(Book(css, body));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token)).Message);
            Assert.Equal(diagnostic, Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font), deadline.Token)).Message);
        }
        Reject(string.Concat(Enumerable.Repeat(".note{}", 4097)), "<p>A</p>", "Native EPUB CSS selectors exceed their 4,096-selector limit.");
        Reject("@namespace 'http://www.w3.org/1999/xhtml';" + string.Concat(Enumerable.Repeat(".note{}", 4096)),
            string.Concat(Enumerable.Repeat("<p class=\"note\">A</p>", 16)), "Native EPUB CSS selector matching exceeds its work limit.");
        UnsupportedGrammarKeepsExplicitRefusalAtBothPublicSeams("." + new string('a', 128));
        Verify("." + new string('a', 127) + "{color:red}", "<p class=\"" + new string('a', 127) + "\">A</p>", 2, 1.5, true, false);
        byte[] source = Book(".note{color:red}", "<p class=\"note\">A</p>");
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => EpubDocument.Open(source, canceled));
        var opened = EpubDocument.Open(source);
        var options = new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 };
        Assert.Throws<OperationCanceledException>(() => opened.CreateLayout(options, canceled));
        Assert.Throws<OperationCanceledException>(() => opened.ConvertToPdf(new EpubPdfConversionOptions(font), canceled));
        var layout = opened.CreateLayout(options);
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(0, cancellationToken: canceled));
    }

    [Theory]
    [InlineData("", 1.5, 7, false)]
    [InlineData("p.note{color:red;text-align:center;background-color:blue}", 9, 17, true)]
    [InlineData(".note{color:red;text-align:center;background-color:blue}", 9, 17, true)]
    public void ClassOnlyRulesConvertAndReopenIndependently(string css, double x, int inkX, bool styled)
    {
        VerifyEngine();
        byte[] source = Book(css, "<p class=\"note\">A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(x, letter.StartBaseLine.X);
        Assert.Equal(4.5, letter.StartBaseLine.Y);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(styled ? new byte[] { 0, 0, 255, 255 } : [0, 0, 0, 255], rendered.Pixels.Slice((5 * 30 + inkX) * 4, 4).ToArray());
        Assert.Equal(styled ? new byte[] { 255, 0, 0, 255 } : [255, 255, 255, 255], rendered.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    private static void VerifyEngine()
    {
        string? pinned = Environment.GetEnvironmentVariable("EPUB_EXPECTED_ENGINE_SHA256");
        string loaded = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location)));
        Console.WriteLine("Loaded engine SHA256: " + loaded);
        if (pinned is not null) Assert.Equal(pinned, loaded);
    }

    private static void Verify(string css, string body, double x, double pdfX, bool red, bool blue)
    {
        VerifyEngine();
        byte[] source = Book(css, body), original = source.ToArray();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(30, 16, font) { FontSize = 10, Margin = 2 }, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(new EpubTextPlacement(0, 0, 1, x, 2, 6, 10) { Color = red ? new PdfRgbColor(1, 0, 0) : null }, Assert.Single(page.TextPlacements));
        var native = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(red ? new float[] { 1, 0, 0, 1 } : [0, 0, 0, 1], native.Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
        Assert.Equal(blue ? new float[] { 0, 0, 1, 1 } : [1, 1, 1, 1], native.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 22.5, PageHeightPoints = 12, MarginPoints = 1.5, FontSizePoints = 7.5 }, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letter = Assert.Single(reader.Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(pdfX, letter.StartBaseLine.X);
        Assert.Equal(4.5, letter.StartBaseLine.Y);
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(30, 16), deadline.Token);
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : [0, 0, 0, 255], rendered.Pixels.Slice((5 * 30 + (int)x + 5) * 4, 4).ToArray());
        Assert.Equal(blue ? new byte[] { 255, 0, 0, 255 } : [255, 255, 255, 255], rendered.Pixels.Slice((11 * 30 + 2) * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(Encoding.UTF8.GetBytes(css), book.ReadResource("style.css"));
    }

    private static byte[] Book(string css, string body)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:class</dc:identifier><dc:title>Classes</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"doc\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"style.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"doc\"/></spine></package>");
            Add("text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><link rel=\"stylesheet\" href=\"style.css\"/></head><body>" + body + "</body></html>");
            Add("style.css", css);
            void Add(string path, string text)
            {
                using var stream = zip.CreateEntry(path, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(text));
            }
        }
        return buffer.ToArray();
    }
}
