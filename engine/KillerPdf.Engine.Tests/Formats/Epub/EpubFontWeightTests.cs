using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubFontWeightTests
{
    public EpubFontWeightTests(ITestOutputHelper output) => output.WriteLine("Loaded engine SHA256: " +
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WeightSelectsRealFaceMetricsAndInkWithInheritedAndNormalSpans(bool weighted)
    {
        byte[] source = Book(weighted), original = source.ToArray();
        var regular = Face(false); var bold = Face(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        Assert.Equal("AAA", book.ReadText(0, deadline.Token));
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2, FontFamilyResolver = _ => regular,
            FontFaceResolver = request => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact)
        };
        var layout = book.CreateLayout(options, deadline.Token);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AAA", page.Text);
        Assert.Equal(new[] { 0, 1, 2 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(weighted ? new double[] { 12, 6, 12 } : [6, 6, 6], page.TextPlacements.Select(item => item.Width));
        Assert.Equal(weighted ? new double[] { 2, 14, 20 } : [2, 8, 14], page.TextPlacements.Select(item => item.Left));
        Assert.All(page.TextPlacements, item => { Assert.Equal(2, item.Top); Assert.Equal(10, item.Height); });
        var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
        Assert.Equal(weighted ? new float[] { 0, 0, 0, 1 } : [1, 1, 1, 1], frame.Pixels.Slice((9 * 40 + 32) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConversionEmbedsTheSelectedWeightWithIndependentTextGeometryAndInk(bool weighted)
    {
        byte[] source = Book(weighted), original = source.ToArray();
        var regular = Face(false); var bold = Face(true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var book = EpubDocument.Open(source, deadline.Token);
        var options = new EpubPdfConversionOptions(regular)
        {
            PageWidthPoints = 30, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => regular,
            FontFaceResolver = request => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact)
        };
        var pdf = PdfDocument.Open(book.ConvertToPdf(options, deadline.Token));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(1, reader.PageCount);
        var letters = reader.Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A" }, letters.Select(item => item.Value));
        Assert.Equal(weighted ? new double[] { 1.5, 10.5, 15 } : [1.5, 6, 10.5], letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(weighted ? new double[] { 9, 4.5, 9 } : [4.5, 4.5, 4.5], letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
        Assert.All(letters, item => Assert.Equal(12, item.StartBaseLine.Y));
        Assert.EndsWith(weighted ? "KillerBold" : "KillerTest", letters[0].FontName);
        Assert.EndsWith("KillerTest", letters[1].FontName);
        Assert.EndsWith(weighted ? "KillerBold" : "KillerTest", letters[2].FontName);
        var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 26), deadline.Token);
        Assert.Empty(frame.Diagnostics);
        Assert.Equal(weighted ? new byte[] { 0, 0, 0, 255 } : [255, 255, 255, 255], frame.Pixels.Slice((9 * 40 + 32) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void StylesheetSpecificityAndInlineResetPreserveNumericWeightMetrics()
    {
        var regular = Face(false); var bold = Face(true);
        byte[] source = Book(false,
            "<p class='heavy'>A<span style='font-weight:normal'>A</span><span style='font-weight:625.5'>A</span></p>",
            "p.heavy{font-weight:700} p{font-weight:400}");
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = request => request.Weight switch
            {
                400 => new(regular, 400, EpubFontMatch.Exact),
                700 => new(bold, 700, EpubFontMatch.Exact),
                625.5 => new(bold, 700, EpubFontMatch.Nearest),
                _ => throw new InvalidOperationException("Unexpected computed weight.")
            }
        });
        Assert.Equal(new double[] { 12, 6, 12 }, Assert.Single(layout.Pages).TextPlacements.Select(item => item.Width));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    // An unresolved bold face falls back to the default face, as a user agent does for a family
    // without that weight, instead of refusing the chapter.
    public void UnresolvedBoldFallsBackToTheDefaultFace(bool typedResolver)
    {
        var regular = Face(false);
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2, FontFamilyResolver = _ => regular,
            FontFaceResolver = typedResolver ? _ => null : null
        };
        var layout = EpubDocument.Open(Book(true)).CreateLayout(options);
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.NotEmpty(layout.Pages.SelectMany(page => page.TextPlacements));
    }

    [Theory]
    [InlineData(0, EpubFontMatch.Nearest)]
    [InlineData(1001, EpubFontMatch.Nearest)]
    [InlineData(400, EpubFontMatch.Exact)]
    [InlineData(700, EpubFontMatch.Nearest)]
    [InlineData(700, (EpubFontMatch)99)]
    public void InconsistentHostFaceMetadataIsRejected(double actualWeight, EpubFontMatch match)
    {
        var regular = Face(false);
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = _ => new(regular, actualWeight, match)
        };
        Assert.Throws<InvalidDataException>(() => EpubDocument.Open(Book(true)).CreateLayout(options));
    }

    [Fact]
    public void ExplicitDefaultVariantAndRealNearestFaceRenderWithoutADeclaredFamily()
    {
        var regular = Face(false); var bold = Face(true);
        var layout = EpubDocument.Open(Book(false, "<p style='font-weight:650'>AAA</p>"))
            .CreateLayout(new EpubLayoutOptions(40, 26, regular)
            {
                FontSize = 10, Margin = 2,
                FontFaceResolver = request => request.Family is null && ReferenceEquals(request.DefaultFont, regular)
                    ? new(bold, 700, EpubFontMatch.Nearest) : null
            });
        Assert.Equal(new double[] { 12, 12, 12 }, Assert.Single(layout.Pages).TextPlacements.Select(item => item.Width));
    }

    [Fact]
    public void CancellationFromAHostResolverStopsLayoutBeforePublication()
    {
        var regular = Face(false);
        using var cancellation = new CancellationTokenSource();
        var options = new EpubLayoutOptions(40, 26, regular)
        {
            FontSize = 10, Margin = 2,
            FontFaceResolver = _ =>
            {
                cancellation.Cancel();
                return new(regular, 700, EpubFontMatch.Exact);
            }
        };
        Assert.ThrowsAny<OperationCanceledException>(() => EpubDocument.Open(Book(true)).CreateLayout(options, cancellation.Token));
    }

    [Theory]
    [InlineData("strong", false)]
    [InlineData("strong", true)]
    [InlineData("b", false)]
    [InlineData("b", true)]
    public void StrongSelectsRealBoldFaceAtBothPublicSeams(string tag, bool convert)
    {
        byte[] source = Book(false, "<p><" + tag + ">A</" + tag + "></p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        var requests = new List<double>();
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request)
        {
            requests.Add(request.Weight);
            return new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 30, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5, FontFaceResolver = Resolve }, deadline.Token));
            var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
            Assert.Equal("A", letter.Value);
            Assert.EndsWith("KillerBold", letter.FontName);
            Assert.Equal(1.5, letter.StartBaseLine.X);
            Assert.Equal(12, letter.StartBaseLine.Y);
            Assert.Equal(9, letter.EndBaseLine.X - letter.StartBaseLine.X);
            var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 26), deadline.Token);
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((9 * 40 + 14) * 4, 4).ToArray());
        }
        else
        {
            var layout = book.CreateLayout(new EpubLayoutOptions(40, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }, deadline.Token);
            var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
            Assert.Equal(2, placement.Left);
            Assert.Equal(2, placement.Top);
            Assert.Equal(10, placement.Height);
            Assert.Equal(12, placement.Width);
            var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
            Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((9 * 40 + 14) * 4, 4).ToArray());
        }
        Assert.Contains(700, requests);
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Theory]
    [InlineData("<p><strong style='font-weight:normal'>A</strong></p>", null, 400)]
    [InlineData("<p><b style='font-weight:400'>A</b></p>", null, 400)]
    [InlineData("<p><strong class='pick'>A</strong></p>", ".pick{font-weight:400}strong{font-weight:700}", 400)]
    [InlineData("<p><strong class='pick' style='font-weight:700'>A</strong></p>", ".pick{font-weight:400}", 700)]
    [InlineData("<p><span style='font-weight:650'><strong style='all:initial'>A</strong></span></p>", null, 400)]
    [InlineData("<p><span style='font-weight:650'><strong style='all:inherit'>A</strong></span></p>", null, 650)]
    [InlineData("<p><strong><b>A</b></strong></p>", null, 900)]
    [InlineData("<p><b><strong>A</strong></b></p>", null, 900)]
    [InlineData("<p><strong><span>A</span></strong></p>", null, 700)]
    [InlineData("<strong>A</strong>", null, 700)]
    [InlineData("<div><b>A</b></div>", null, 700)]
    [InlineData("<p><a href='#target'><strong id='target'>A</strong></a></p>", null, 700)]
    public void StrongAndBoldAuthorOverridesAndInlineContextsUseRealRequestedFaces(string body, string? css, double expectedWeight)
        => CheckInlineFace(body, css, expectedWeight);

    [Theory]
    [InlineData("349.5", 400)]
    [InlineData("350", 700)]
    [InlineData("549.5", 700)]
    [InlineData("550", 900)]
    [InlineData("899", 900)]
    [InlineData("900", 900)]
    [InlineData("950", 950)]
    public void StrongRelativeWeightUsesLiteralParentThresholds(string parentWeight, double expectedWeight)
        => CheckInlineFace("<p style='font-weight:" + parentWeight + "'><strong>A</strong></p>", null, expectedWeight);

    [Fact]
    public void StrongSemanticAttributesNavigationAndBreakChildrenPreservePublicText()
    {
        var regular = Face(false); var bold = Face(true);
        byte[] source = Book(false, "<p><strong id='target' class='pick' xmlns:epub='http://www.idpf.org/2007/ops' epub:type='label' style='font-weight:normal'><span>A</span><br/><a href='#target'>A</a></strong></p>");
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 26, regular)
        { FontSize = 10, Margin = 2, FontFaceResolver = request => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact) });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(p => p.Top));
        Assert.Equal(new double[] { 6, 6 }, page.TextPlacements.Select(p => p.Width));
        Assert.Equal("doc.xhtml#target", page.TextPlacements[1].LinkTarget);
        Assert.Equal(0, layout.ResolveNavigationTarget("doc.xhtml#target").TextOffset);
    }

    [Theory]
    [InlineData("<p><strong onclick='bad'>A</strong></p>")]
    public void StrongAdmissionKeepsScriptingRefused(string body)
    {
        var book = EpubDocument.Open(Book(false, body));
        var regular = Face(false); var bold = Face(true);
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request) => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(40, 26, regular)
        { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(regular) { FontFaceResolver = Resolve }));
    }

    // A missing image inside a bold link is omitted on its own; the chapter lays out.
    [Fact]
    public void AMissingImageInsideStrongIsOmitted()
    {
        var book = EpubDocument.Open(Book(false, "<p><a href='#x'><strong><img src='missing.png'/></strong></a></p>"));
        var regular = Face(false); var bold = Face(true);
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request) => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact);
        EpubImageCompatibilityTests.AssertImageOmitted(book.CreateLayout(new EpubLayoutOptions(40, 26, regular)
        { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }));
    }

    // Headings nested in other content, unknown h7, foreign-namespace and advisory attributes all lay out:
    // HTML admits the nesting, an unknown element flows inline and inert attributes do not change rendering.
    [Theory]
    [InlineData("<p><h1>A</h1></p>")]
    [InlineData("<h1><h3>A</h3></h1>")]
    [InlineData("<p><span><h4>A</h4></span></p>")]
    [InlineData("<h7>A</h7>")]
    [InlineData("<h1 xmlns='urn:other'>A</h1>")]
    [InlineData("<h1 align='center'>A</h1>")]
    [InlineData("<h1 headingoffset='1'>A</h1>")]
    [InlineData("<section><span><h1>A</h1></span></section>")]
    [InlineData("<head><strong>A</strong></head><p>A</p>")]
    [InlineData("<p><a href='#x'><strong><a href='#x'>A</a></strong></a></p>")]
    [InlineData("<p><strong xmlns='urn:other'>A</strong></p>")]
    public void HeadingAndStrongInOtherContextsLayOut(string body)
    {
        var regular = Face(false); var bold = Face(true);
        EpubCompatibilityTests.AssertLaysOut(EpubDocument.Open(Book(false, body)), HeadingNativeOptions(regular, bold));
    }

    // CSS Fonts 4: bolder of 400 is 700 and lighter of 700 is 400, resolved against the inherited weight.
    [Theory]
    [InlineData("<p><strong style='font-weight:bolder'>A</strong></p>", "<p><strong>A</strong></p>")]
    [InlineData("<p style='font-weight:bold'><b style='font-weight:lighter'>A</b></p>", "<p style='font-weight:bold'><span style='font-weight:normal'>A</span></p>")]
    public void RelativeWeightKeywordsResolveAgainstTheInheritedWeight(string body, string equivalent)
    {
        var regular = Face(false); var bold = Face(true);
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request) => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact);
        var options = new EpubLayoutOptions(40, 26, regular) { FontSize = 10, Margin = 2, FontFaceResolver = Resolve };
        EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book(false, equivalent)).CreateLayout(options),
            EpubDocument.Open(Book(false, body)).CreateLayout(options));
    }

    private static void CheckInlineFace(string body, string? css, double expectedWeight)
    {
        byte[] source = Book(false, body, css), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        var requests = new List<double>();
        bool useBold = expectedWeight > 400;
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request)
        {
            requests.Add(request.Weight);
            double actual = request.Weight > 400 ? 700 : 400;
            return new(request.Weight > 400 ? bold : regular, actual,
                request.Weight == actual ? EpubFontMatch.Exact : EpubFontMatch.Nearest);
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var layout = book.CreateLayout(new EpubLayoutOptions(40, 26, regular)
        { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }, deadline.Token);
        var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
        Assert.Equal(useBold ? 12 : 6, placement.Width);
        Assert.Equal(2, placement.Left);
        Assert.Equal(2, placement.Top);
        Assert.Equal(10, placement.Height);
        Assert.Contains(expectedWeight, requests);
        requests.Clear();
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
        { PageWidthPoints = 30, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5, FontFaceResolver = Resolve }, deadline.Token));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal("A", letter.Value);
        Assert.Equal(useBold ? 9 : 4.5, letter.EndBaseLine.X - letter.StartBaseLine.X);
        Assert.EndsWith(useBold ? "KillerBold" : "KillerTest", letter.FontName);
        Assert.Contains(expectedWeight, requests);
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void H1DefaultSizeAndRealBoldFaceReachBothPublicSeams(bool convert)
    {
        byte[] source = Book(false, "<h1 style='margin:0'>A</h1>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request) => new(request.Weight == 700 ? bold : regular, request.Weight, EpubFontMatch.Exact);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5, FontFaceResolver = Resolve }, deadline.Token));
            var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
            Assert.Equal("A", letter.Value);
            Assert.EndsWith("KillerBold", letter.FontName);
            Assert.Equal(1.5, letter.StartBaseLine.X);
            Assert.Equal(61.5, letter.StartBaseLine.Y);
            Assert.Equal(18, letter.EndBaseLine.X - letter.StartBaseLine.X);
            var frame = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(100, 100), deadline.Token);
            Assert.Empty(frame.Diagnostics);
            Assert.Equal(new byte[] { 0, 0, 0, 255 }, frame.Pixels.Slice((16 * 100 + 26) * 4, 4).ToArray());
        }
        else
        {
            var layout = book.CreateLayout(new EpubLayoutOptions(100, 100, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }, deadline.Token);
            var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
            Assert.Equal(2, placement.Left);
            Assert.Equal(2, placement.Top);
            Assert.Equal(20, placement.Height);
            Assert.Equal(24, placement.Width);
            var frame = layout.RenderPage(0, cancellationToken: deadline.Token);
            Assert.Equal(new float[] { 0, 0, 0, 1 }, frame.Pixels.Slice((16 * 100 + 26) * 4, 4).ToArray());
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Theory]
    [InlineData("h1", 20, 24, 15.4, 48.8, 18, 51.45, 32.4)]
    [InlineData("h2", 15, 18, 14.45, 41.9, 13.5, 55.1625, 37.575)]
    [InlineData("h3", 11.7, 14.04, 13.7, 37.1, 10.53, 57.705, 41.175)]
    [InlineData("h4", 10, 12, 15.3, 38.6, 9, 57.525, 40.05)]
    [InlineData("h5", 8.3, 9.96, 15.861, 38.022, 7.47, 58.12425, 40.4835)]
    [InlineData("h6", 6.7, 8.04, 17.611, 39.922, 6.03, 57.77175, 39.0585)]
    public void AllHeadingLevelsKeepLiteralDefaultMetricsAndFollowingParagraphPositions(string tag,
        double size, double width, double top, double nextTop, double pdfWidth, double baseline, double nextBaseline)
    {
        byte[] source = Book(false, "<" + tag + ">A</" + tag + "><p style='font-size:10px;font-weight:normal;margin:0'>A</p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        var page = Assert.Single(book.CreateLayout(HeadingNativeOptions(regular, bold)).Pages);
        Assert.Equal("A\n\nA", page.Text);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(top, page.TextPlacements[0].Top, 8);
        Assert.Equal(nextTop, page.TextPlacements[1].Top, 8);
        Assert.Equal(size, page.TextPlacements[0].Height, 8);
        Assert.Equal(width, page.TextPlacements[0].Width, 8);
        Assert.Equal(10, page.TextPlacements[1].Height, 8);
        Assert.Equal(6, page.TextPlacements[1].Width, 8);
        Assert.All(page.TextPlacements, p => Assert.Equal(2, p.Left, 8));
        var pdf = PdfDocument.Open(book.ConvertToPdf(HeadingPdfOptions(regular, bold)));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A" }, letters.Select(p => p.Value));
        Assert.Equal(baseline, letters[0].StartBaseLine.Y, 8);
        Assert.Equal(nextBaseline, letters[1].StartBaseLine.Y, 8);
        Assert.Equal(pdfWidth, letters[0].EndBaseLine.X - letters[0].StartBaseLine.X, 8);
        Assert.EndsWith("KillerBold", letters[0].FontName);
        Assert.EndsWith("KillerTest", letters[1].FontName);
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Theory]
    [InlineData("h1", 40, 48, 36, 49.5)]
    [InlineData("h3", 23.4, 28.08, 21.06, 59.46)]
    [InlineData("h5", 16.6, 19.92, 14.94, 63.54)]
    public void HeadingEmSizeUsesActualParentAndFixedBoldDefault(string tag, double size, double width, double pdfWidth, double baseline)
        => CheckHeading("<div style='font-size:20px;font-weight:950'><" + tag + " style='margin:0'>A</" + tag + "></div>", null,
            size, width, pdfWidth, baseline, "KillerBold");

    [Theory]
    [InlineData("h1")]
    [InlineData("h2")]
    [InlineData("h3")]
    [InlineData("h4")]
    [InlineData("h5")]
    [InlineData("h6")]
    public void HeadingTypeClassAndInlineOverridesWinOverEveryUaDefault(string tag)
        => CheckHeading("<" + tag + " class='pick' style='font-size:10px'>A</" + tag + ">",
            tag + "{font-size:30px;font-weight:700;margin:9px}.pick{font-size:12px;font-weight:normal;margin:0}",
            10, 6, 4.5, 67.5, "KillerTest");

    [Theory]
    [InlineData("initial", 10, 6, 4.5, 61.5, 10)]
    [InlineData("inherit", 20, 12, 9, 61.5, 2)]
    public void HeadingAllMarkersRetainActualParentAndResetSemantics(string keyword,
        double size, double width, double pdfWidth, double baseline, double top)
        => CheckHeading("<div style='font-size:20px;font-weight:normal;margin:0'><h1 style='all:" + keyword + "'>A</h1></div>", null,
            size, width, pdfWidth, baseline, "KillerTest", top);

    [Theory]
    [InlineData("h1", 20, 24, 18, 61.5, 2)]
    [InlineData("h2", 15, 18, 13.5, 64.5, 2)]
    [InlineData("h3", 11.7, 14.04, 10.53, 66.48, 2)]
    [InlineData("h4", 10, 12, 9, 67.5, 2)]
    [InlineData("h5", 8.3, 9.96, 7.47, 67.5, 3.36)]
    [InlineData("h6", 6.7, 8.04, 6.03, 67.5, 4.64)]
    public void BareInlineHeadingOmitsOnlyUaMargins(string tag, double size, double width, double pdfWidth, double baseline, double top)
        => CheckHeading("<" + tag + " style='display:inline'>A</" + tag + ">", null,
            size, width, pdfWidth, baseline, "KillerBold", top);

    [Fact]
    public void HeadingLocalAnchorAndSourceOffsetsSurviveConversion()
    {
        byte[] source = Book(false, "<h3 id='target' class='heading' style='font-size:10px;font-weight:normal;margin:0'><span>A</span></h3><p style='font-size:10px;margin:0'><a href='#target'>A</a></p>"), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        var layout = book.CreateLayout(HeadingNativeOptions(regular, bold));
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(p => p.Start));
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(p => p.Top));
        Assert.Equal("doc.xhtml#target", page.TextPlacements[1].LinkTarget);
        Assert.Equal(0, layout.ResolveNavigationTarget("doc.xhtml#target").TextOffset);
        var pdf = PdfDocument.Open(book.ConvertToPdf(HeadingPdfOptions(regular, bold)));
        Assert.Equal(new[] { "A", "A" }, new PdfPageContentReader(pdf).Read(0).Letters.Select(p => p.Value));
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    // Inline margins are not laid out yet, so they are dropped instead of refusing the chapter.
    [Theory]
    [InlineData("h1", "margin-left:1px")]
    [InlineData("h3", "margin-top:1px")]
    [InlineData("h6", "margin-bottom:1px")]
    public void InlineHeadingMarginsAreIgnored(string heading, string margin)
    {
        var regular = Face(false); var bold = Face(true);
        EpubCompatibilityTests.AssertSameLayout(
            EpubDocument.Open(Book(false, $"<{heading} style='display:inline'>A</{heading}>")).CreateLayout(HeadingNativeOptions(regular, bold)),
            EpubDocument.Open(Book(false, $"<{heading} style='display:inline;{margin}'>A</{heading}>")).CreateLayout(HeadingNativeOptions(regular, bold)));
    }

    [Theory]
    [InlineData("<h5 onclick='bad'>A</h5>")]
    public void HeadingAdmissionKeepsInlineMarginParentNamespaceAndAttributeGuards(string body)
    {
        var book = EpubDocument.Open(Book(false, body));
        var regular = Face(false); var bold = Face(true);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(HeadingNativeOptions(regular, bold)));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(HeadingPdfOptions(regular, bold)));
    }

    [Theory]
    [InlineData("<h2 style='display:inline;font-size:8.3px'>A</h2>", 8.3, 9.96, 7.47, 67.5, "KillerBold", 3.36)]
    [InlineData("<p><span style='font-size:6.7px;font-weight:700'>A</span></p>", 6.7, 8.04, 6.03, 67.5, "KillerBold", 4.64)]
    [InlineData("<div style='font-size:20px'><h2 style='all:initial'>A</h2></div>", 10, 6, 4.5, 61.5, "KillerTest", 10)]
    public void ExistingInlineStrutControlsKeepParentBaselineAndChildMetrics(string body, double size,
        double width, double pdfWidth, double baseline, string face, double top)
        => CheckHeading(body, null, size, width, pdfWidth, baseline, face, top);

    private static EpubFontFaceResult? HeadingFace(EpubFontFaceRequest request, TrueTypeFont regular, TrueTypeFont bold) => request.Weight switch
    {
        400 => new(regular, 400, EpubFontMatch.Exact),
        700 => new(bold, 700, EpubFontMatch.Exact),
        _ => throw new InvalidOperationException("Unexpected heading weight.")
    };
    private static EpubLayoutOptions HeadingNativeOptions(TrueTypeFont regular, TrueTypeFont bold) => new(100, 100, regular)
    { FontSize = 10, Margin = 2, FontFaceResolver = request => HeadingFace(request, regular, bold) };
    private static EpubPdfConversionOptions HeadingPdfOptions(TrueTypeFont regular, TrueTypeFont bold) => new(regular)
    { PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5, FontFaceResolver = request => HeadingFace(request, regular, bold) };
    private static void CheckHeading(string body, string? css, double size, double width, double pdfWidth, double baseline, string face, double top = 2)
    {
        byte[] source = Book(false, body, css), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        var placement = Assert.Single(Assert.Single(book.CreateLayout(HeadingNativeOptions(regular, bold)).Pages).TextPlacements);
        Assert.Equal(2, placement.Left, 8);
        Assert.Equal(top, placement.Top, 8);
        Assert.Equal(size, placement.Height, 8);
        Assert.Equal(width, placement.Width, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(HeadingPdfOptions(regular, bold)));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal(baseline, letter.StartBaseLine.Y, 8);
        Assert.Equal(pdfWidth, letter.EndBaseLine.X - letter.StartBaseLine.X, 8);
        Assert.EndsWith(face, letter.FontName);
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    private static TrueTypeFont Face(bool bold)
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        if (!bold) return TrueTypeFont.Load(bytes);
        TrueTypeFontTests.RenameFace(bytes, "KillerBold");
        int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        for (int entry = 12; entry < 12 + tables * 16; entry += 16)
        {
            string tag = Encoding.ASCII.GetString(bytes, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 8, 4));
            if (tag == "hmtx") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4, 2), 1200);
            if (tag == "head") BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 40, 2), 1500);
            if (tag == "glyf")
            {
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 6, 2), 1500);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 17, 2), 1500);
                BinaryPrimitives.WriteInt16BigEndian(bytes.AsSpan(offset + 19, 2), -750);
            }
        }
        return TrueTypeFont.Load(bytes);
    }

    private static byte[] Book(bool weighted, string? body = null, string? css = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>weight</dc:identifier><dc:title>Weight</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + "</manifest><spine><itemref idref='doc'/></spine></package>");
            if (css is not null) Add("style.css", css);
            string head = css is null ? "" : "<head><link rel='stylesheet' href='style.css'/></head>";
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'>" + head + "<body>" + (body ?? (weighted
                ? "<p style='font-family:Fixture;font-weight:bold'>A<span style='font-weight:normal'>A</span><span style='font-weight:700'>A</span></p>"
                : "<p style='font-family:Fixture'>AAA</p>")) + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
