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
    [InlineData("width:40px;margin-left:auto;margin-right:10px", 40)]
    [InlineData("width:40px;margin-left:10px;margin-right:auto", 20)]
    [InlineData("width:40px;padding:0 4px;margin:0 auto", 30)]
    [InlineData("width:50%;margin:0 AUTO", 30)]
    [InlineData("width:auto;margin:auto", 10)]
    [InlineData("width:40px;margin:auto", 30)]
    [InlineData("width:40px;margin:0 auto;margin-left:2px", 12)]
    [InlineData("width:100px;margin:0 auto", 10)]
    public void AutomaticMarginsResolveRemainingSpaceWithPaddingAndOverrides(string style, double left)
    {
        var book = EpubDocument.Open(Book("", "", "A", "", paragraphStyle: style));
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 80, font) { Margin = 10, FontSize = 20 });
        Assert.Equal(left, Assert.Single(layout.Pages[0].TextPlacements).Left, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 15 }));
        Assert.Equal(left * .75, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X, 6);
    }

    [Fact]
    public void AutomaticHorizontalMarginsCenterAnExplicitWidthAtBothSeams()
    {
        byte[] source = Book("", "p{width:40px;margin:0 auto}", "A", ""), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 80, font) { Margin = 10, FontSize = 20 });
        Assert.Equal(30, Assert.Single(layout.Pages[0].TextPlacements).Left, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 7.5, FontSizePoints = 15 }));
        Assert.Equal(22.5, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X, 6);
        Assert.Contains(layout.RenderPage(0).Pixels.ToArray(), value => value == 0);
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(100, 1)]
    [InlineData(60, 2)]
    public void DecoratingInlineAncestorContributesItsMetricsToEveryLine(int pageHeight, int pageCount)
    {
        byte[] source = Book("", ".large{font-size:40px;text-decoration:underline;color:red}.small{font-size:10px}",
            "<span class='large'><span class='small'>A<br/>A</span></span>", "");
        byte[] preserved = source.ToArray();
        byte[] post = new byte[32]; post[1] = 3; post[8] = 0xFF; post[9] = 0x9C; post[11] = 50;
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, post: post));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(60, pageHeight, font) { Margin = 2, FontSize = 20 });
        Assert.Equal(pageCount, layout.Pages.Count);
        Assert.Equal(pageCount == 1 ? new double[] { 26, 66 } : new double[] { 26, 26 },
            layout.Pages.SelectMany(page => page.TextPlacements).Select(value => value.Top).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 45, PageHeightPoints = pageHeight * .75, MarginPoints = 1.5, FontSizePoints = 15 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pageCount, reader.PageCount);
        Assert.Equal("AA", string.Concat(Enumerable.Range(0, pageCount).SelectMany(index => reader.Read(index).Letters).Select(value => value.Value)));
        for (int index = 0; index < pageCount; index++)
        {
            var native = layout.RenderPage(index);
            var saved = new PdfPageRenderer(pdf).Render(index, new PdfRenderOptions(60, pageHeight));
            foreach (int y in pageCount == 1 ? new[] { 38, 78 } : new[] { 38 })
            {
                Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((y * 60 + 3) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((y * 60 + 3) * 4, 4).ToArray());
            }
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void HiddenDescendantDoesNotPaintTheVisibleAncestorsUnderline()
    {
        byte[] source = Book("", "p{text-decoration:underline;color:red}span{visibility:hidden}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 32, font) { Margin = 2, FontSize = 20 });
        Assert.Empty(Assert.Single(layout.Pages).TextPlacements);
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), value => Assert.Equal(1f, value));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 24, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.All(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 32)).Pixels.ToArray(),
            value => Assert.Equal((byte)255, value));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void VisibleDescendantShowsTheHiddenAncestorsPropagatedUnderline()
    {
        byte[] source = Book("", "p{text-decoration:underline;color:red;visibility:hidden}span{text-decoration:none;color:blue;visibility:visible}", "A", ""), preserved = source.ToArray();
        byte[] post = new byte[32]; post[1] = 3; post[8] = 0xFF; post[9] = 0x9C; post[11] = 50;
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, post: post));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 32, font) { Margin = 2, FontSize = 20 });
        Assert.Equal(1, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Color!.Value.Blue);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 24, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 32)).Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ContinuousUnderlineHasNoSeamAtAFractionalGlyphBoundary()
    {
        byte[] source = Book("", "span{text-decoration:underline;color:red}", "AA", ""), preserved = source.ToArray();
        byte[] post = new byte[32]; post[1] = 3; post[8] = 0xFF; post[9] = 0x38; post[11] = 125;
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, post: post));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 32, font) { Margin = 2, FontSize = 16 });
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((18 * 40 + 11) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 24, MarginPoints = 1.5, FontSizePoints = 12 }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 32)).Pixels.Slice((18 * 40 + 11) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DescendantNoneKeepsTheAncestorUnderlineAndItsColor()
    {
        byte[] source = Book("", "p{text-decoration:underline;color:red}span{text-decoration:none;color:blue}", "A", ""), preserved = source.ToArray();
        byte[] post = new byte[32]; post[1] = 3; post[8] = 0xFF; post[9] = 0x9C; post[11] = 50;
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, post: post));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 32, font) { Margin = 2, FontSize = 20 });
        Assert.Equal(1, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Color!.Value.Blue);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 24, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 32)).Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void UnderlineUsesTheFontTopAndThicknessInNativeAndConvertedPages()
    {
        byte[] source = Book("", "span{text-decoration:underline;color:red}", "A", ""), preserved = source.ToArray();
        byte[] post = new byte[32];
        post[1] = 3;
        post[8] = 0xFF; post[9] = 0x9C;
        post[11] = 50;
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, post: post));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 32, font) { Margin = 2, FontSize = 20 });
        Assert.Equal("A", Assert.Single(layout.Pages).Text);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((20 * 40 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((21 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((20 * 40 + 14) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 24, MarginPoints = 1.5, FontSizePoints = 15 }));
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 32));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((20 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((21 * 40 + 3) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void VisibilityProjectionPreservesSupplementaryUnicodeRegions()
    {
        const string scalar = "\U0001D400";
        byte[] source = Book("", ".hide{visibility:hidden}", scalar + "<span class='hide'>" + scalar + "</span>" + scalar, "");
        byte[] preserved = source.ToArray();
        byte[] cmap = [0, 0, 0, 1, 0, 3, 0, 10, 0, 0, 0, 12,
            0, 12, 0, 0, 0, 0, 0, 28, 0, 0, 0, 0, 0, 0, 0, 1,
            0, 1, 0xD4, 0, 0, 1, 0xD4, 0, 0, 0, 0, 1];
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(true, includeOutlines: true, cmap: cmap));
        var snapshot = PdfReadSnapshot.OpenEpub(source, new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = snapshot.ReadTextPage(0);
        Assert.Equal(scalar + scalar, page.Text);
        Assert.Collection(page.Regions,
            region => { Assert.Equal(0, region.Start); Assert.Equal(2, region.Length); },
            region => { Assert.Equal(2, region.Start); Assert.Equal(2, region.Length); Assert.Equal(21.9, region.Bounds.Left, 6); });
        var pdf = PdfDocument.Open(EpubDocument.Open(source).ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(scalar + scalar, string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void NavigationToHiddenTextUsesItsRetainedPageAndPosition()
    {
        byte[] source = Book("", "p{margin:0}.hide{visibility:hidden}",
            "A</span></p><p class='hide' id='hidden'><span>A</span></p><p id='shown'><span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 40, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Empty(layout.Pages[1].TextPlacements);
        var hidden = layout.ResolveNavigationTarget("EPUB/text.xhtml#hidden");
        Assert.Equal(1, hidden.PageIndex);
        Assert.Equal(10, hidden.Left, 6);
        Assert.Equal(10, hidden.Top, 6);
        Assert.Equal(2, layout.ResolveNavigationTarget("EPUB/text.xhtml#shown").PageIndex);
        var snapshot = PdfReadSnapshot.OpenEpub(source, new EpubLayoutOptions(100, 40, font) { Margin = 10, FontSize = 16 });
        Assert.DoesNotContain("A", snapshot.ReadTextPage(1).Text);
        Assert.Empty(snapshot.ReadTextPage(1).Regions);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<span class='hide'>A</span>A", "A")]
    [InlineData("A<span class='hide'>A</span>", "A")]
    [InlineData("<span class='hide'>AAA</span>", "")]
    [InlineData("A<span class='hide'>A</span> A", "A A")]
    [InlineData("<span class='hide'>A<span style='visibility:visible'>A</span>A</span>A", "AA")]
    public void SnapshotVisibilityProjectionPreservesVisibleTextAndSourceSeparators(string text, string expected)
    {
        byte[] source = Book("", ".hide{visibility:hidden}", text, ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(source, new EpubLayoutOptions(100, 100, font)
        { Margin = 10, FontSize = 16 });
        var page = snapshot.ReadTextPage(0);
        Assert.Equal(expected, page.Text);
        Assert.Equal(expected, string.Concat(page.Regions.Select(region => page.Text.Substring(region.Start, region.Length))));
        Assert.Equal(expected.Length, page.Regions.Count);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void NativeSnapshotOmitsVisibilityHiddenTextAndRemapsVisibleRegions()
    {
        byte[] source = Book("", ".hide{visibility:hidden}", "A<span class='hide'>A</span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var snapshot = PdfReadSnapshot.OpenEpub(source, new EpubLayoutOptions(100, 100, font)
        { Margin = 10, FontSize = 16 });
        var page = snapshot.ReadTextPage(0);
        Assert.Equal("AA", page.Text);
        Assert.Collection(page.Regions,
            region => { Assert.Equal(0, region.Start); Assert.Equal(1, region.Length); Assert.Equal(7.5, region.Bounds.Left, 6); },
            region => { Assert.Equal(1, region.Start); Assert.Equal(1, region.Length); Assert.Equal(21.9, region.Bounds.Left, 6); });
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("visible")]
    [InlineData("initial")]
    public void VisibleDescendantOverridesInheritedVisibilityWithoutRestoringItsParent(string visibility)
    {
        byte[] source = Book("", ".hide{visibility:hidden}.show{visibility:" + visibility + "}",
            "A<span class='hide'>A<span class='show'>A</span>A</span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(3, placements.Count);
        Assert.Equal(10, placements[0].Left, 6);
        Assert.Equal(29.2, placements[1].Left, 6);
        Assert.Equal(48.4, placements[2].Left, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AAA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(36.3, letters[2].StartBaseLine.X, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void HiddenParagraphRetainsItsBoxWithoutPaintingBackgroundOrBorder()
    {
        byte[] source = Book("", "p{margin:0}.hide{visibility:hidden;padding:4px;border-top:2px solid rgb(255,0,0);background-color:rgb(255,0,0)}",
            "A</span></p><p class='hide'><span>A</span></p><p><span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(52, placements[1].Top, 6);
        var native = layout.RenderPage(0);
        for (int y = 26; y < 52; y++)
            Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        for (int y = 26; y < 52; y++)
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void VisibilityHiddenReservesSpaceWithoutPaintingOrSelectablePdfText()
    {
        byte[] source = Book("", "span.hide{visibility:hidden;background-color:rgb(255,0,0)}",
            "A<span class='hide'>A</span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(10, placements[0].Left, 6);
        Assert.Equal(29.2, placements[1].Left, 6);
        var native = layout.RenderPage(0);
        for (int y = 10; y < 26; y++)
            Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((y * 100 + 27) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(21.9, letters[1].StartBaseLine.X, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        for (int y = 10; y < 26; y++)
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((y * 100 + 27) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DisplayNoneKeepsVisibleNavigationOffsetsInVisibleText()
    {
        byte[] source = Book("", ".hide{display:none}",
            "A<span class='hide' id='hidden'>HIDDEN</span><span id='shown'>A</span>", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(100, 100, font)
        { Margin = 10, FontSize = 16 });
        Assert.Equal("AA", Assert.Single(layout.Pages).Text);
        var destination = layout.ResolveNavigationTarget("EPUB/text.xhtml#shown");
        Assert.Equal(0, destination.PageIndex);
        Assert.Equal(1, destination.TextOffset);
        Assert.Equal(19.6, destination.Left, 6);
        Assert.Throws<ArgumentException>(() => layout.ResolveNavigationTarget("EPUB/text.xhtml#hidden"));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<span class='hide' onclick='alert(1)'>A</span>", "Native EPUB XHTML attributes and styling")]
    [InlineData("<script class='hide'>alert(1)</script>", "Native EPUB XHTML element")]
    [InlineData("<img class='hide' src='https://example.com/image.png'/>", "External or unresolved")]
    [InlineData("<img class='hide' src='missing.png'/>", "External or unresolved")]
    public void DisplayNoneDoesNotBypassScriptOrResourceDiagnostics(string hidden, string diagnosis)
    {
        byte[] source = Book("", ".hide{display:none}", "A" + hidden + "A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var native = Assert.Throws<NotSupportedException>(() => book.CreateLayout(
            new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 }));
        Assert.Contains(diagnosis, native.Message);
        var conversion = Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Contains(diagnosis, conversion.Message);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(".pick{display:none}.pick{display:inline}", "")]
    [InlineData("span.pick{display:inline}.pick{display:none}", "")]
    [InlineData(".pick{display:none}", " style='display:inline'")]
    public void DisplayNoneCanBeOverriddenOnTheSameElement(string css, string attributes)
    {
        byte[] source = Book("", "p{margin:0}" + css,
            "A<span class='pick'" + attributes + ">A</span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AAA", page.Text);
        Assert.Equal(3, page.TextPlacements.Count);
        Assert.Equal(29.2, page.TextPlacements[2].Left, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AAA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(21.9, letters[2].StartBaseLine.X, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DisplayNoneBodyDoesNotApplyItsGeometryOrCanvasBackground()
    {
        byte[] source = Book("display:none;margin:1000px;padding:1000px;width:0;font-size:1000px;background-color:rgb(255,0,0)", "", "HIDDEN", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Equal("HIDDEN", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        Assert.Empty(page.Text);
        Assert.Empty(page.TextPlacements);
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Empty(new PdfPageContentReader(pdf).Read(0).Letters);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.All(saved.Pixels.ToArray(), channel => Assert.Equal((byte)255, channel));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DisplayNoneDoesNotComputeOrPaintIrrelevantGeometry()
    {
        byte[] source = Book("", "p{margin:0}.hide{display:none;font-size:1000px;line-height:2000px;padding-left:10px;border:4px solid rgb(255,0,0);width:0}",
            "A<span class='hide'>HIDDEN</span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", page.Text);
        Assert.Equal(19.6, page.TextPlacements[1].Left, 6);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("<span class='hide'>HIDDEN</span>")]
    [InlineData("<span class='hide'><span class='show'>HIDDEN</span></span>")]
    [InlineData("<span class='hide'/>")]
    public void DisplayNoneDoesNotAddInlineSpaceOrSuppressFollowingText(string hidden)
    {
        byte[] source = Book("", "p{margin:0}.hide{display:none}.show{display:inline}", "A" + hidden + "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AA", page.Text);
        Assert.Collection(page.TextPlacements,
            placement => Assert.Equal(10, placement.Left, 6),
            placement => Assert.Equal(19.6, placement.Left, 6));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(letter => letter.Value)));
        Assert.Equal(14.7, letters[1].StartBaseLine.X, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void DisplayNoneExcludesTheSubtreeFromLayoutPaintingAndConvertedText()
    {
        byte[] source = Book("", "p{margin:0}.hide{display:none;background-color:rgb(255,0,0)}",
            "A</span></p><p class='hide'><span>HIDDEN</span></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Contains("HIDDEN", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var page = Assert.Single(layout.Pages);
        Assert.DoesNotContain("HIDDEN", page.Text);
        Assert.Collection(page.TextPlacements,
            placement => Assert.Equal(10, placement.Top, 6),
            placement => Assert.Equal(26, placement.Top, 6));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((42 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value)));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((42 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ExactWidthLimitRendersClippedToTheRequestedPage()
    {
        byte[] source = Book("", "p{margin:0;width:32768px;background-color:rgb(255,0,0)}", "A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(10, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Left, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(100, native.Width);
        Assert.Equal(100, native.Height);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 99) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal("A", Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).Value);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 99) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("32769px")]
    [InlineData("99999999999999999999999999999999999999999999999999999999999em")]
    [InlineData("0")]
    public void UnsupportedOrExcessiveWidthsDiagnoseWithoutChangingTheBook(string width)
    {
        byte[] source = Book("", "p{margin:0;width:" + width + "}", "A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(preserved, source);
    }

    // Invalid widths are ignored declarations, so the paragraph keeps its automatic width.
    [Theory]
    [InlineData("NaNpx")]
    [InlineData("Infinitypx")]
    [InlineData("-1px")]
    [InlineData("calc(100% - 1px)")]
    public void InvalidWidthsAreIgnored(string width)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 };
        EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book("", "p{margin:0}", "A", "")).CreateLayout(options),
            EpubDocument.Open(Book("", "p{margin:0;width:" + width + "}", "A", "")).CreateLayout(options));
    }

    [Fact]
    public void NestedPercentageMarginsUseTheirActualContainingWidth()
    {
        byte[] source = Book("", "p{margin:0}.pick{width:50%;margin:10% 0 20%}",
            "</span></p><div style='width:50%'><p class='pick'><span>A</span></p></div><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(14, placements[0].Top, 6);
        Assert.Equal(38, placements[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(54.9, letters[0].StartBaseLine.Y, 6);
        Assert.Equal(36.9, letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void NarrowSpecifiedWidthRetainsWrappingAndBackgroundAcrossPages()
    {
        byte[] source = Book("", "p{margin:0;width:25%;text-align:center;background-color:rgb(255,0,0)}", "A A A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(2, layout.Pages[0].TextPlacements.Count);
        Assert.Single(layout.Pages[1].TextPlacements);
        Assert.All(layout.Pages.SelectMany(page => page.TextPlacements), placement => Assert.Equal(15.2, placement.Left, 6));
        Assert.Equal(10, layout.Pages[1].TextPlacements[0].Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal("AA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        Assert.Equal(11.4, Assert.Single(reader.Read(1).Letters).StartBaseLine.X, 6);
        for (int page = 0; page < 2; page++)
        {
            var native = layout.RenderPage(page);
            var saved = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 29) * 4, 4).ToArray());
            Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((10 * 100 + 30) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 29) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((10 * 100 + 30) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void NestedPercentageWidthsUseTheParentContentBoxAndRestoreSiblingBounds()
    {
        byte[] source = Book("", "p{margin:0;text-align:center}.pick{width:50%;background-color:rgb(255,0,0)}",
            "</span></p><div style='width:50%;background-color:rgb(0,0,255)'><p class='pick'><span>A</span></p></div><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(15.2, placements[0].Left, 6);
        Assert.Equal(45.2, placements[1].Left, 6);
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(11.4, letters[0].StartBaseLine.X, 6);
        Assert.Equal(33.9, letters[1].StartBaseLine.X, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        foreach (var point in new[] { (X: 29, Color: 0), (X: 30, Color: 1), (X: 49, Color: 1), (X: 50, Color: 2) })
        {
            float[] expected = point.Color == 0 ? [1, 0, 0, 1] : point.Color == 1 ? [0, 0, 1, 1] : [1, 1, 1, 1];
            byte[] expectedSaved = point.Color == 0 ? [0, 0, 255, 255] : point.Color == 1 ? [255, 0, 0, 255] : [255, 255, 255, 255];
            Assert.Equal(expected, native.Pixels.Slice((10 * 100 + point.X) * 4, 4).ToArray());
            Assert.Equal(expectedSaved, saved.Pixels.Slice((10 * 100 + point.X) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void SpecifiedWidthExcludesPaddingAndUsesTheContainingWidthBeforeMargins()
    {
        byte[] source = Book("", "p{margin:0 12px 0 8px;width:50%;padding:0 6px 0 4px;text-align:center;background-color:rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(37.2, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Left, 6);
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(27.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        foreach (var point in new[] { (X: 17, Red: false), (X: 18, Red: true), (X: 67, Red: true), (X: 68, Red: false) })
        {
            Assert.Equal(point.Red ? new float[] { 1, 0, 0, 1 } : new float[] { 1, 1, 1, 1 },
                native.Pixels.Slice((10 * 100 + point.X) * 4, 4).ToArray());
            Assert.Equal(point.Red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 255, 255, 255 },
                saved.Pixels.Slice((10 * 100 + point.X) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("width:50%;width:auto", "")]
    [InlineData("width:50%", "width:auto")]
    public void AutoWidthClearsAnEarlierSpecifiedWidth(string rule, string inline)
    {
        byte[] source = Book("", "p{margin:0;text-align:center;background-color:rgb(255,0,0)}.pick{" + rule + "}",
            "A", "", paragraphClass: "pick", paragraphStyle: inline), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(45.2, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Left, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 89) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((10 * 100 + 90) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(33.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 89) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((10 * 100 + 90) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("50%")]
    [InlineData("40px")]
    [InlineData("2.5em")]
    public void SpecifiedParagraphWidthControlsAlignmentAndBackgroundExtent(string width)
    {
        byte[] source = Book("", "p{margin:0;width:" + width + ";text-align:center;background-color:rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(25.2, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Left, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 49) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((10 * 100 + 50) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(18.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.X, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 49) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((10 * 100 + 50) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void LeadingEmptyParagraphRetainsItsExplicitBottomMargin()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><p style='margin-bottom:12px'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(22, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(48.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyPaddedParagraphKeepsItsBottomMarginBeforeFollowingText()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><p style='padding-top:4px;margin-bottom:12px'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(26, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(45.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyBorderedParagraphKeepsItsBottomMarginBeforeFollowingText()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><p style='border-top:4px solid rgb(255,0,0);margin-bottom:12px'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(26, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(45.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TopBorderExpansionHonorsTheDocumentPageLimit(bool empty)
    {
        byte[] source = empty
            ? Book("", "p{margin:0}", "</span></p><p style='border-top:10000.5px solid rgb(255,0,0)'></p><p><span>A", "")
            : Book("", "p{margin:0;border-top:10000.5px solid rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var error = Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(100, 21, font) { Margin = 10, FontSize = .5 }));
        Assert.Equal("Native EPUB layout exceeds its 10000-page limit.", error.Message);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyBorderFragmentsFinishBeforeTopAndBottomPadding()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><p style='border-top:95px solid rgb(255,0,0);padding:6px 0 3px;background-color:rgb(0,255,0)'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Equal(34, Assert.Single(layout.Pages[2].TextPlacements).Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(9.9, Assert.Single(new PdfPageContentReader(pdf).Read(2).Letters).StartBaseLine.Y, 6);
        var native = layout.RenderPage(2);
        var saved = new PdfPageRenderer(pdf).Render(2, new PdfRenderOptions(100, 60));
        foreach (var point in new[] { (Y: 10, Red: true), (Y: 24, Red: true), (Y: 25, Red: false), (Y: 33, Red: false) })
        {
            Assert.Equal(point.Red ? new float[] { 1, 0, 0, 1 } : new float[] { 0, 1, 0, 1 },
                native.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(point.Red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 255, 0, 255 },
                saved.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("border-top:4px solid rgb(255,0,0)", "border:0", 10, false)]
    [InlineData("border:0", "border-top:4px solid rgb(255,0,0)", 14, true)]
    public void InlineBorderDeclarationsOverrideTheClassRule(string rule, string inline, double expectedTop, bool red)
    {
        byte[] source = Book("", "p{margin:0}.pick{" + rule + "}", "A", "", paragraphClass: "pick", paragraphStyle: inline);
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(expectedTop, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(red ? new float[] { 1, 0, 0, 1 } : new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ForcedBreakMovesBorderAndPaddingBeforeTheNextParagraph()
    {
        byte[] source = Book("", "p{margin:0}",
            "A</span></p><p style='page-break-before:always;border-top:4px solid rgb(255,0,0);padding-top:6px;background-color:rgb(0,255,0)'><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(20, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((26 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(50.4, Assert.Single(new PdfPageContentReader(pdf).Read(1).Letters).StartBaseLine.Y, 6);
        var native = layout.RenderPage(1);
        var saved = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(100, 100));
        foreach (var point in new[] { (Y: 10, Red: true), (Y: 14, Red: false) })
        {
            Assert.Equal(point.Red ? new float[] { 1, 0, 0, 1 } : new float[] { 0, 1, 0, 1 },
                native.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(point.Red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 255, 0, 255 },
                saved.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void WrappedParagraphContinuationDoesNotRepeatItsTopBorderOrPadding()
    {
        byte[] source = Book("", "p{margin:0;border-top:4px solid rgb(255,0,0);padding-top:6px;background-color:rgb(0,255,0)}", "A A A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(20, Assert.Single(layout.Pages[0].TextPlacements).Top, 6);
        Assert.Collection(layout.Pages[1].TextPlacements,
            placement => Assert.Equal(10, placement.Top, 6),
            placement => Assert.Equal(26, placement.Top, 6));
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 30, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Equal("AA", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        foreach (var point in new[] { (Page: 0, Y: 10, Red: true), (Page: 0, Y: 14, Red: false), (Page: 1, Y: 10, Red: false) })
        {
            var native = layout.RenderPage(point.Page);
            var saved = new PdfPageRenderer(pdf).Render(point.Page, new PdfRenderOptions(40, 60));
            Assert.Equal(point.Red ? new float[] { 1, 0, 0, 1 } : new float[] { 0, 1, 0, 1 },
                native.Pixels.Slice((point.Y * 40 + 25) * 4, 4).ToArray());
            Assert.Equal(point.Red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 0, 255, 0, 255 },
                saved.Pixels.Slice((point.Y * 40 + 25) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void OversizedTopBorderRetainsAllFragmentsBeforeText(bool empty)
    {
        byte[] source = empty
            ? Book("", "p{margin:0}", "</span></p><p style='border-top:95px solid rgb(255,0,0)'></p><p><span>A", "")
            : Book("", "p{margin:0;border-top:95px solid rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Empty(layout.Pages[1].TextPlacements);
        Assert.Equal(25, Assert.Single(layout.Pages[2].TextPlacements).Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(16.65, Assert.Single(new PdfPageContentReader(pdf).Read(2).Letters).StartBaseLine.Y, 6);
        for (int page = 0; page < 3; page++)
        {
            var native = layout.RenderPage(page);
            var saved = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void TransparentTopBorderShowsTheParagraphBackground()
    {
        byte[] source = Book("", "p{margin:0;border-top:4px solid transparent;padding-top:6px;background-color:rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(20, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(50.4, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        foreach (int y in new[] { 10, 13, 14, 19, 20 })
        {
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(".pick{border-top:4px solid rgb(255,0,0)}p{margin:0;border:0}")]
    [InlineData("p{margin:0;border:0}.pick{border-top:4px solid rgb(255,0,0)}")]
    public void MoreSpecificTopBorderSurvivesLessSpecificShorthand(string css)
    {
        byte[] source = Book("", css, "A", "", paragraphClass: "pick"), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(14, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(0).Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(54.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void AutomaticBreakMovesTopBorderWithTheFirstParagraphLine()
    {
        byte[] source = Book("", "p{margin:0}",
            "A<br/>A</span></p><p style='border-top:4px solid rgb(255,0,0)'><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(14, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var previous = layout.RenderPage(0);
        var next = layout.RenderPage(1);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, previous.Pixels.Slice((42 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, next.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(24.9, Assert.Single(new PdfPageContentReader(pdf).Read(1).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(100, 60));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("border-top:4px solid rgb(255,0,0);border:0", 10, false)]
    [InlineData("border:0;border-top:4px solid rgb(255,0,0)", 14, true)]
    [InlineData("border-top:.25em solid rgb(255,0,0)", 14, true)]
    [InlineData("border-top:4px none rgb(255,0,0)", 10, false)]
    [InlineData("border-top:4px solid transparent", 14, false)]
    [InlineData("color:rgb(255,0,0);border-top:4px solid", 14, true)]
    public void TopBorderResetsAndColorsRetainLiteralGeometry(string style, double expectedTop, bool red)
    {
        byte[] source = Book("", "p{margin:0;" + style + "}", "A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(expectedTop, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(red ? new float[] { 1, 0, 0, 1 } : new float[] { 1, 1, 1, 1 },
            native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(red ? new byte[] { 0, 0, 255, 255 } : new byte[] { 255, 255, 255, 255 },
            saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ParagraphMarginStaysOutsideItsTopBorderWithoutRepeating()
    {
        byte[] source = Book("", "p{margin:0;margin-top:8px;border-top:4px solid rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(22, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((17 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((18 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(48.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((18 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ParagraphTopBorderPaintsBeforeItsContentInNativeAndConvertedPages()
    {
        byte[] source = Book("", "p{margin:0;border-top:4px solid rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(14, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(54.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationAfterFontResolutionStartsPreservesTheBook(bool convert)
    {
        byte[] source = Book("", "p{margin:0;padding-bottom:9998px;font-family:Probe}", new string('A', 50000), "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        TrueTypeFont? Resolve(EpubFontFamily family)
        {
            calls++;
            cancellation.Cancel();
            return font;
        }
        var error = Assert.Throws<OperationCanceledException>(() =>
        {
            if (convert)
                book.ConvertToPdf(new EpubPdfConversionOptions(font)
                { PageWidthPoints = 75, PageHeightPoints = 15.75, MarginPoints = 7.5, FontSizePoints = .00000075, FontFamilyResolver = Resolve }, cancellation.Token);
            else
                book.CreateLayout(new EpubLayoutOptions(100, 21, font)
                { Margin = 10, FontSize = .000001, FontFamilyResolver = Resolve }, cancellation.Token);
        });
        Assert.True(calls > 0);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        var recovered = book.CreateLayout(new EpubLayoutOptions(100, 21, font) { Margin = 10, FontSize = .000001 });
        Assert.Equal(9999, recovered.Pages.Count);
        Assert.Equal(new string('A', 50000), string.Concat(recovered.Pages.Select(page => page.Text)));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void LeadingExplicitBreakRetainsOversizedInitialPadding()
    {
        byte[] source = Book("", "p{margin:0;padding-top:60px;background-color:rgb(255,0,0)}", "<br/>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Empty(layout.Pages[1].TextPlacements);
        Assert.Equal(10, Assert.Single(layout.Pages[2].TextPlacements).Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(3, reader.PageCount);
        Assert.Equal(27.9, Assert.Single(reader.Read(2).Letters).StartBaseLine.Y, 6);
        for (int page = 0; page < 3; page++)
        {
            var native = layout.RenderPage(page);
            var saved = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyParagraphTopPaddingRetainsAllPageFragmentsBeforeFollowingText()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><p style='padding-top:95px;background-color:rgb(255,0,0)'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Empty(layout.Pages[1].TextPlacements);
        Assert.Equal(25, Assert.Single(layout.Pages[2].TextPlacements).Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(16.65, Assert.Single(new PdfPageContentReader(pdf).Read(2).Letters).StartBaseLine.Y, 6);
        for (int page = 0; page < 3; page++)
        {
            var native = layout.RenderPage(page);
            var saved = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void PdfConversionPageLimitPreservesTheBookForAnotherLayout()
    {
        byte[] source = Book("", "p{margin:0;padding-bottom:10001px}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var error = Assert.Throws<InvalidDataException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 15.75, MarginPoints = 7.5, FontSizePoints = .375 }));
        Assert.Equal("Native EPUB layout exceeds its 10000-page limit.", error.Message);
        var recovered = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(251, recovered.Pages.Count);
        Assert.Equal("A", string.Concat(recovered.Pages.Select(page => page.Text)));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void PaddingExpansionAcceptsExactlyTenThousandPages()
    {
        byte[] source = Book("", "p{margin:0;padding-bottom:9999.5px}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 21, font) { Margin = 10, FontSize = .5 });
        Assert.Equal(10000, layout.Pages.Count);
        Assert.Single(layout.Pages[0].TextPlacements);
        Assert.All(layout.Pages.Skip(1), page => Assert.Empty(page.TextPlacements));
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void PaddingExpansionHasADocumentWidePageLimit()
    {
        byte[] source = Book("", "p{margin:0;padding-bottom:10001px}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var error = Assert.Throws<InvalidDataException>(() => book.CreateLayout(
            new EpubLayoutOptions(100, 21, font) { Margin = 10, FontSize = .5 }));
        Assert.Equal("Native EPUB layout exceeds its 10000-page limit.", error.Message);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData(60, 16, 30, 12.9)]
    [InlineData(35, 16, 10, 27.9)]
    [InlineData(12, 32, 10, 18.3)]
    public void OversizedInitialPaddingPaginatesBeforeItsFirstLine(int padding, int inlineSize, double expectedTop, double expectedBaseline)
    {
        byte[] source = Book("", $"p{{margin:0;padding-top:{padding}px;background-color:rgb(255,0,0)}}span{{font-size:{inlineSize}px}}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Equal(expectedTop, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Empty(reader.Read(0).Letters);
        Assert.Equal(expectedBaseline, Assert.Single(reader.Read(1).Letters).StartBaseLine.Y, 6);
        foreach (int page in new[] { 0, 1 })
        {
            var native = layout.RenderPage(page);
            var saved = new PdfPageRenderer(pdf).Render(page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void TerminalPaddingRetainsBackgroundAcrossTextlessPageFragments()
    {
        byte[] source = Book("", "p{margin:0;padding-bottom:95px;background-color:rgb(255,0,0)}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Single(layout.Pages[0].TextPlacements);
        Assert.Empty(layout.Pages[1].TextPlacements);
        Assert.Empty(layout.Pages[2].TextPlacements);
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(layout.Pages[0].Text.Length, layout.Pages[1].TextStart);
        Assert.Equal(layout.Pages[1].TextStart, layout.Pages[2].TextStart);
        Assert.Empty(layout.Pages[1].Text);
        Assert.Empty(layout.Pages[2].Text);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(3, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        Assert.Empty(reader.Read(1).Letters);
        Assert.Empty(reader.Read(2).Letters);
        foreach (var point in new[] { (Page: 1, Y: 10), (Page: 1, Y: 49), (Page: 2, Y: 40) })
        {
            var native = layout.RenderPage(point.Page);
            var saved = new PdfPageRenderer(pdf).Render(point.Page, new PdfRenderOptions(100, 60));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
        }
        var final = layout.RenderPage(2);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, final.Pixels.Slice((41 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void OverflowingBottomPaddingContinuesBeforeTheFollowingParagraph()
    {
        byte[] source = Book("", "p{margin:0}",
            "A</span></p><p style='padding-bottom:30px;background-color:rgb(255,0,0)'><span>A</span></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(2, layout.Pages[0].TextPlacements.Count);
        Assert.Equal(32, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var native = layout.RenderPage(1);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((32 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(11.4, Assert.Single(new PdfPageContentReader(pdf).Read(1).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(100, 60));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void AncestorBackgroundCoversTransparentParagraphPadding()
    {
        byte[] source = Book("", "p{margin:0}",
            "</span></p><div style='background-color:rgb(255,0,0)'><p style='padding:4px 0 6px'><span>A</span></p></div><p><span>", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(14, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        foreach (int y in new[] { 10, 13, 30, 35 })
        {
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        }
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyParagraphBottomPaddingSeparatesItsTopAndBottomMargins()
    {
        byte[] source = Book("", "p{margin:0}",
            "A</span></p><p style='margin:8px 0 12px;padding-bottom:6px;background-color:rgb(255,0,0)'></p><p style='margin-top:4px'><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(52, placements[1].Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((27 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((34 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(26.4, new PdfPageContentReader(pdf).Read(0).Letters[1].StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((34 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void AutomaticBreakMovesInitialParagraphPaddingWithItsFirstLine()
    {
        byte[] source = Book("", "p{margin:0}",
            "A</span></p><p style='padding-top:12px;background-color:rgb(255,0,0)'><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(22, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var first = layout.RenderPage(0);
        var second = layout.RenderPage(1);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, first.Pixels.Slice((26 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, second.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(18.9, Assert.Single(new PdfPageContentReader(pdf).Read(1).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(100, 60));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void FollowingParagraphPaddingBackgroundStartsAfterCollapsedSiblingMargins()
    {
        byte[] source = Book("", "p{margin:0;margin-bottom:12px}",
            "A</span></p><p style='margin-top:8px;padding-top:4px;background-color:rgb(255,0,0)'><span>A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(42, layout.Pages[0].TextPlacements[1].Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((27 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((38 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((27 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((38 * 100 + 70) * 4, 4).ToArray());
    }

    [Fact]
    public void ParagraphTopPaddingStartsAfterItsCollapsedBodyMargin()
    {
        byte[] source = Book("margin-top:12px", "p{margin:0;margin-top:8px;padding-top:4px;background-color:rgb(255,0,0)}", "A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(26, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((21 * 100 + 70) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((22 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(45.9, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 6);
    }

    [Fact]
    public void PaddingDecorationsStayOnInitialAndTerminalParagraphFragments()
    {
        byte[] source = Book("", "p{margin:0;padding:4px 0 6px;background-color:rgb(255,0,0)}", "A<br/>A<br/>A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 64, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(2, layout.Pages[0].TextPlacements.Count);
        Assert.Equal(14, layout.Pages[0].TextPlacements[0].Top, 6);
        Assert.Equal(30, layout.Pages[0].TextPlacements[1].Top, 6);
        Assert.Equal(10, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 48, MarginPoints = 7.5, FontSizePoints = 12 }));
        foreach (var point in new[] { (Page: 0, Y: 10), (Page: 1, Y: 31) })
        {
            var native = layout.RenderPage(point.Page);
            var saved = new PdfPageRenderer(pdf).Render(point.Page, new PdfRenderOptions(100, 64));
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((point.Y * 100 + 70) * 4, 4).ToArray());
        }
        var previous = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, previous.Pixels.Slice((48 * 100 + 70) * 4, 4).ToArray());
    }

    [Fact]
    public void ForcedBreakMovesParagraphTopPaddingAndItsBackgroundToTheNextPage()
    {
        byte[] source = Book("", "p{margin:0}",
            "A</span></p><p style='page-break-before:always;padding:4px 0 6px;background-color:rgb(255,0,0)'><span>A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(14, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var native = layout.RenderPage(1);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
        var previous = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, previous.Pixels.Slice((26 * 100 + 70) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        Assert.Equal(54.9, Assert.Single(new PdfPageContentReader(pdf).Read(1).Letters).StartBaseLine.Y, 6);
        var saved = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(100, 100));
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((10 * 100 + 70) * 4, 4).ToArray());
    }

    [Fact]
    public void ParagraphBackgroundIncludesTopAndBottomPaddingStrips()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;padding:4px 0 6px;background-color:rgb(255,0,0)}", "A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var native = layout.RenderPage(0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100));
        Assert.Empty(saved.Diagnostics);
        foreach (int y in new[] { 11, 14, 31, 36 })
        {
            Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        }
        foreach (int y in new[] { 10, 37 })
        {
            Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
            Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((y * 100 + 70) * 4, 4).ToArray());
        }
    }

    [Fact]
    public void VerticalParagraphPaddingOffsetsTextAndTheFollowingParagraph()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;padding:4px 0 6px}",
            "A</span></p><p style='padding:0'><span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var positions = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, positions.Count);
        Assert.Equal(15, positions[0].Top, 6);
        Assert.Equal(37, positions[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(54.15, letters[0].StartBaseLine.Y, 6);
        Assert.Equal(16.5, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void AutomaticParagraphContinuationDoesNotRepeatItsTopMargin()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;margin-top:8px;margin-bottom:12px}",
            "A<br/>A<br/>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 60, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(19, Assert.Single(layout.Pages[0].TextPlacements).Top, 6);
        Assert.Equal(2, layout.Pages[1].TextPlacements.Count);
        Assert.Equal(10, layout.Pages[1].TextPlacements[0].Top, 6);
        Assert.Equal(26, layout.Pages[1].TextPlacements[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 45, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(0).Letters.Select(p => p.Value)));
        var letters = reader.Read(1).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(27.9, letters[0].StartBaseLine.Y, 6);
        Assert.Equal(12, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ForcedPageBreakDiscardsPreviousMarginAndRetainsNextTopMargin()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;margin-top:8px;margin-bottom:20px}",
            "A</span></p><p style='page-break-before:always'><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(19, Assert.Single(layout.Pages[0].TextPlacements).Top, 6);
        Assert.Equal(18, Assert.Single(layout.Pages[1].TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(51.9, Assert.Single(reader.Read(1).Letters).StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void LeadingEmptyParagraphRetainsTheFullCollapsedMarginChain()
    {
        byte[] source = Book("margin-top:12px", "p{margin:0}",
            "</span></p><p style='margin-top:20px'></p><p><span>A", "");
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(30, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(42.9, letter.StartBaseLine.Y, 6);
    }

    [Fact]
    public void FirstParagraphTopMarginCollapsesWithUnseparatedBodyMargin()
    {
        byte[] source = Book("margin-top:12px", "p{margin:0;margin-top:8px}", "A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(22, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(48.9, letter.StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void VerticalPercentageMarginsUseContainingWidthBeforeParagraphSideInsets()
    {
        byte[] source = Book("padding-top:1px", "p{margin:10% 12px 15% 8px;padding:0 4px}",
            "A</span></p><p><span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var positions = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, positions.Count);
        Assert.Equal(19, positions[0].Top, 6);
        Assert.Equal(47, positions[1].Top, 6);
        Assert.All(positions, p => Assert.Equal(22, p.Left, 6));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(21, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.All(letters, p => Assert.Equal(16.5, p.BoundingBox.Left, 6));
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void EmptyParagraphPreservesTheLargestAdjoiningMarginWithoutAddingALine()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;margin-top:8px;margin-bottom:12px}",
            "A</span></p><p style='margin-top:20px;margin-bottom:4px'></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var positions = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, positions.Count);
        Assert.Equal(19, positions[0].Top, 6);
        Assert.Equal(55, positions[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(27, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void AdjacentPositiveParagraphMarginsCollapseToTheLargerGap()
    {
        byte[] source = Book("padding-top:1px", "p{margin:0;margin-top:8px;margin-bottom:12px}",
            "A</span></p><p><span>A", ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var positions = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, positions.Count);
        Assert.Equal(19, positions[0].Top, 6);
        Assert.Equal(47, positions[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(21, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("A</span></p><p></p><p><span>A", 100, 1)]
    [InlineData("A</span></p><p><span>A", 44, 2)]
    public void ZeroMarginsPreserveEmptyAndPaginatedParagraphFlow(string markup, double height, int pageCount)
    {
        byte[] source = Book("", "p{margin:0}", markup, ""), preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, height, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(pageCount, layout.Pages.Count);
        var positions = layout.Pages.SelectMany(p => p.TextPlacements).ToArray();
        Assert.Equal(2, positions.Length);
        Assert.Equal(10, positions[0].Top, 6);
        Assert.Equal(pageCount == 1 ? 26 : 10, positions[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = height * .75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(pageCount, reader.PageCount);
        Assert.Equal("AA", string.Concat(Enumerable.Range(0, pageCount).SelectMany(i => reader.Read(i).Letters).Select(l => l.Value)));
        for (int index = 0; index < pageCount; index++)
            Assert.Empty(new PdfPageRenderer(pdf).Render(index, new PdfRenderOptions(100, (int)height)).Diagnostics);
        Assert.Equal(preserved, source);
    }

    [Theory]
    [InlineData("", "", 42)]
    [InlineData("margin:0", "", 42)]
    [InlineData("", "margin:0", 42)]
    [InlineData("margin-bottom:0", "margin-top:0", 26)]
    public void AdjacentParagraphSidesRetainOnlyTheirUnspecifiedDefaultGap(string first, string second, double top)
    {
        byte[] source = Book("", "", "A</span></p><p style='" + second + "'><span>A", "");
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        using (var original = new ZipArchive(input, ZipArchiveMode.Read))
        using (var revised = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in original.Entries)
            {
                using var read = entry.Open();
                using var bytes = new MemoryStream();
                read.CopyTo(bytes);
                byte[] data = bytes.ToArray();
                if (entry.FullName == "EPUB/text.xhtml")
                    data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("<p><span>A", "<p style='" + first + "'><span>A", StringComparison.Ordinal));
                using var write = revised.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                write.Write(data);
            }
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        byte[] dataBytes = output.ToArray(), preserved = dataBytes.ToArray();
        var book = EpubDocument.Open(dataBytes);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font)
        { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(top, placements[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(top == 26 ? 12 : 24, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Empty(new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100)).Diagnostics);
        Assert.Equal(preserved, dataBytes);
    }

    [Fact]
    public void ExplicitZeroParagraphMarginsSuppressTheDefaultParagraphGap()
    {
        byte[] source = Book("", "p{margin:0}", "A</span></p><p><span>A", "");
        byte[] preserved = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(10, placements[0].Top, 6);
        Assert.Equal(26, placements[1].Top, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(12, letters[0].StartBaseLine.Y - letters[1].StartBaseLine.Y, 6);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void ParagraphInsetsDoNotLeakIntoAnUnstyledSibling()
    {
        byte[] source = Book("", "p.pick{margin:0 12px 0 8px;padding:0 24px 0 16px}", "A", "");
        using var input = new MemoryStream(source);
        using var output = new MemoryStream();
        using (var original = new ZipArchive(input, ZipArchiveMode.Read))
        using (var revised = new ZipArchive(output, ZipArchiveMode.Create, true))
            foreach (var entry in original.Entries)
            {
                using var read = entry.Open();
                using var bytes = new MemoryStream();
                read.CopyTo(bytes);
                byte[] data = bytes.ToArray();
                if (entry.FullName == "EPUB/text.xhtml")
                    data = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("<p><span>A</span></p>",
                        "<p class='pick'><span>A</span></p><p><span>A</span></p>", StringComparison.Ordinal));
                using var write = revised.CreateEntry(entry.FullName, CompressionLevel.NoCompression).Open();
                write.Write(data);
            }
        byte[] bookBytes = output.ToArray(), preserved = bookBytes.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(bookBytes);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, font) { Margin = 10, FontSize = 16 });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(2, placements.Count);
        Assert.Equal(34, placements[0].Left, 6);
        Assert.Equal(10, placements[1].Left, 6);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 7.5, FontSizePoints = 12 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal("AA", string.Concat(letters.Select(p => p.Value)));
        Assert.Equal(25.5, letters[0].BoundingBox.Left, 6);
        Assert.Equal(7.5, letters[1].BoundingBox.Left, 6);
        Assert.Equal(preserved, bookBytes);
    }

    [Fact]
    public void ParagraphHorizontalInsetsRemainOnBothSidesOfAPageBreak()
    {
        byte[] source = Book("", "p{margin:0 12px 0 8px;padding:0 24px 0 16px;background-color:rgb(255,0,0)}", "A A", "");
        byte[] original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(100, 44, font) { Margin = 10, FontSize = 16 });
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { 0, 2 }, layout.Pages.SelectMany(p => p.TextPlacements).Select(p => p.Start));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 75, PageHeightPoints = 33, MarginPoints = 7.5, FontSizePoints = 12 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        for (int index = 0; index < 2; index++)
        {
            var placement = Assert.Single(layout.Pages[index].TextPlacements);
            Assert.Equal(34, placement.Left, 6);
            Assert.Equal(10, placement.Top, 6);
            var letter = Assert.Single(reader.Read(index).Letters);
            Assert.Equal("A", letter.Value);
            Assert.Equal(25.5, letter.BoundingBox.Left, 6);
            var native = layout.RenderPage(index);
            var saved = new PdfPageRenderer(pdf).Render(index, new PdfRenderOptions(100, 44));
            Assert.Empty(saved.Diagnostics);
            foreach (int x in new[] { 17, 78 })
            {
                Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((14 * 100 + x) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, saved.Pixels.Slice((14 * 100 + x) * 4, 4).ToArray());
            }
            foreach (int x in new[] { 20, 76 })
            {
                Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((14 * 100 + x) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((14 * 100 + x) * 4, 4).ToArray());
            }
        }
        Assert.Equal(original, source);
    }

    [Fact]
    public void ParagraphHorizontalPaddingWrapsTextInsideItsPaintedPaddingBox()
    {
        var actual = Observe("", "p{margin:0 12px 0 8px;padding:0 24px 0 16px;background-color:rgb(255,0,0)}",
            text: "A A", convertedText: "AA");
        Assert.Equal(new[] { 0, 2 }, actual.Placements.Select(p => p.Start));
        Assert.Equal(10, actual.Placements[0].Top, 6);
        Assert.Equal(26, actual.Placements[1].Top, 6);
        Assert.All(actual.Placements, p => Assert.Equal(34, p.Left, 6));
        Assert.All(actual.Letters, p => Assert.Equal(25.5, p.Left, 6));
        Assert.Equal(12, actual.Letters[0].Bottom - actual.Letters[1].Bottom, 6);
        foreach (int y in new[] { 14, 30 })
        {
            foreach (int x in new[] { 17, 78 })
            {
                Assert.Equal(new float[] { 1, 1, 1, 1 }, actual.NativePixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 255, 255, 255, 255 }, actual.PdfPixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
            }
            foreach (int x in new[] { 20, 76 })
            {
                Assert.Equal(new float[] { 1, 0, 0, 1 }, actual.NativePixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
                Assert.Equal(new byte[] { 0, 0, 255, 255 }, actual.PdfPixels.AsSpan((y * 100 + x) * 4, 4).ToArray());
            }
        }
    }

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
    [InlineData("padding:40px")]
    [InlineData("padding-left:101%")]
    [InlineData("padding-top:60px")]
    public void OversizedBoxValuesRemainExplicitlyUnsupported(string style)
    {
        AssertUnsupported(style);
    }

    // CSS Syntax ignores an invalid declaration, so the body lays out as if it were absent.
    [Theory]
    [InlineData("padding:-1px")]
    [InlineData("padding:1")]
    [InlineData("padding:auto")]
    [InlineData("padding:inherit")]
    [InlineData("padding:NaNpx")]
    [InlineData("padding:Infinity%")]
    [InlineData("padding:1.px")]
    [InlineData("padding:1e2px")]
    [InlineData("padding:0 0 0 0 0")]
    [InlineData("padding-left:0 0")]
    [InlineData("font-size:0")]
    [InlineData("line-height:0")]
    public void InvalidBoxAndTypographyValuesAreIgnored(string style)
    {
        AssertIgnored(style);
    }

    // CSS Values 4: rem uses the root size (the reading size here) and 12pt is 16px.
    [Theory]
    [InlineData("padding:1rem")]
    [InlineData("padding:12pt")]
    public void RootRelativeAndAbsolutePaddingResolveToPixels(string style)
    {
        AssertIgnored(style, supportedStyle: "padding:16px");
    }

    // Visible borders are not painted yet; they are ignored rather than refusing the chapter.
    [Theory]
    [InlineData("border:2px solid rgb(255,0,0)")]
    [InlineData("border:0;border:2px solid red")]
    [InlineData("border:solid")]
    public void VisibleBordersAreIgnoredUntilTheyArePainted(string style)
    {
        AssertIgnored(style);
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
        AssertIgnored("", "body.pick{border:2px solid red}body{border:0}", "pick");
    }

    [Theory]
    [InlineData("p{display:inline;padding:1px}", "p{display:inline}")]
    [InlineData("span{padding-left:1px}", "")]
    public void PositiveInlinePaddingIsIgnored(string css, string supported)
    {
        AssertIgnored("", css, supportedCss: supported);
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

    private static void AssertIgnored(string style, string css = "", string bodyClass = "", string supportedStyle = "", string supportedCss = "")
    {
        var actual = Observe(style, css, bodyClass: bodyClass);
        var control = Observe(supportedStyle, supportedCss, bodyClass: bodyClass);
        Assert.Equal(control.Placements, actual.Placements);
        Assert.Equal(control.NativePixels, actual.NativePixels);
        Assert.Equal(control.Letters, actual.Letters);
        Assert.Equal(control.PdfPixels, actual.PdfPixels);
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

    private static byte[] Book(string style, string css, string text, string bodyClass, string paragraphClass = "", string paragraphStyle = "")
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\" version=\"1.0\"><rootfiles><rootfile full-path=\"EPUB/book.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles></container>");
            Add("EPUB/book.opf", "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"id\"><metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\"><dc:identifier id=\"id\">urn:test:box</dc:identifier><dc:title>Box</dc:title><dc:language>en</dc:language></metadata><manifest><item id=\"chapter\" href=\"text.xhtml\" media-type=\"application/xhtml+xml\"/><item id=\"css\" href=\"book.css\" media-type=\"text/css\"/></manifest><spine><itemref idref=\"chapter\"/></spine></package>");
            Add("EPUB/text.xhtml", "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Box</title><link rel=\"stylesheet\" href=\"book.css\"/></head><body class=\"" + bodyClass + "\" style=\"" + style + "\"><p" + (paragraphClass.Length == 0 ? "" : " class='" + paragraphClass + "'") + (paragraphStyle.Length == 0 ? "" : " style='" + paragraphStyle + "'") + "><span>" + text + "</span></p></body></html>");
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
