using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubH2DefaultTests(ITestOutputHelper output)
{
    private const string Paragraph = "<p style='font-size:10px;font-weight:normal;margin:0'>A</p>";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultHeadingKeepsLiteralTypographyMarginsAndIndependentPdfGeometry(bool convert)
    {
        var source = Book("<h2>A</h2>" + Paragraph);
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        Assert.Equal(800, regular.Ascender); Assert.Equal(800, bold.Ascender);
        Assert.Equal(1000, regular.UnitsPerEm); Assert.Equal(1000, bold.UnitsPerEm);
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        Assert.Equal("A\n\nA", book.ReadText(0));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
            var letters = new PdfPageContentReader(pdf).Read(0).Letters;
            Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
            Assert.Equal(55.1625, letters[0].StartBaseLine.Y, 8);
            Assert.Equal(37.575, letters[1].StartBaseLine.Y, 8);
            Assert.Equal(13.5, letters[0].EndBaseLine.X - letters[0].StartBaseLine.X, 8);
            Assert.Equal(4.5, letters[1].EndBaseLine.X - letters[1].StartBaseLine.X, 8);
            Assert.All(letters, item => Assert.Equal(1.5, item.StartBaseLine.X, 8));
            Assert.EndsWith("KillerBold", letters[0].FontName);
            Assert.EndsWith("KillerTest", letters[1].FontName);
        }
        else
        {
            var page = Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages);
            Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
            Assert.Equal(14.45, page.TextPlacements[0].Top, 8);
            Assert.Equal(41.9, page.TextPlacements[1].Top, 8);
            Assert.Equal(15, page.TextPlacements[0].Height, 8);
            Assert.Equal(10, page.TextPlacements[1].Height, 8);
            Assert.Equal(18, page.TextPlacements[0].Width, 8);
            Assert.Equal(6, page.TextPlacements[1].Width, 8);
            Assert.All(page.TextPlacements, item => Assert.Equal(2, item.Left, 8));
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Fact]
    public void BareInlineHeadingRetainsDefaultSizeAndWeightWithoutUserAgentBoxMargins()
    {
        var book = EpubDocument.Open(Book("<h2 style='display:inline'>A</h2>"));
        var regular = Face(false); var bold = Face(true);
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        var placement = Assert.Single(Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages).TextPlacements);
        Assert.Equal(2, placement.Left, 8);
        Assert.Equal(2, placement.Top, 8);
        Assert.Equal(15, placement.Height, 8);
        Assert.Equal(18, placement.Width, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal(64.5, letter.StartBaseLine.Y, 8);
        Assert.Equal(13.5, letter.EndBaseLine.X - letter.StartBaseLine.X, 8);
        Assert.EndsWith("KillerBold", letter.FontName);
    }

    [Theory]
    [InlineData("50%", 10, 6, 4.5, 67.5)]
    [InlineData(".5em", 10, 6, 4.5, 67.5)]
    [InlineData("inherit", 20, 12, 9, 61.5)]
    [InlineData("unset", 20, 12, 9, 61.5)]
    [InlineData("initial", 10, 6, 4.5, 67.5)]
    public void AuthorSizeUsesActualParentInsteadOfDefaultHeadingMultiplier(string size,
        double height, double width, double pdfWidth, double baseline)
    {
        CheckHeading($"<div style='font-size:20px'><h2 style='font-size:{size};font-weight:normal;margin:0'>A</h2></div>",
            null, height, width, pdfWidth, baseline, "KillerTest");
    }

    [Theory]
    [InlineData("inherit", "normal")]
    [InlineData("unset", "normal")]
    [InlineData("initial", "bold")]
    public void AuthorWeightKeywordsOverrideTheDefaultBoldFace(string weight, string parentWeight)
    {
        CheckHeading($"<div style='font-weight:{parentWeight}'><h2 style='font-weight:{weight};margin:0'>A</h2></div>",
            null, 15, 9, 6.75, 64.5, "KillerTest");
    }

    [Fact]
    public void AuthorStylesheetAndInlineValuesOverrideDefaultSizeWeightAndMargins()
    {
        CheckHeading("<h2 class='pick' style='font-size:10px'>A</h2>",
            "h2{font-size:20px;font-weight:bold;margin:9px}.pick{font-size:12px;font-weight:normal;margin:0}",
            10, 6, 4.5, 67.5, "KillerTest");
    }

    [Fact]
    public void OnlyAuthoredTopMarginLeavesDefaultBottomMarginIntact()
    {
        var book = EpubDocument.Open(Book("<h2 style='margin-top:0'>A</h2>" + Paragraph));
        var regular = Face(false); var bold = Face(true);
        var placements = Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages).TextPlacements;
        Assert.Equal(2, placements[0].Top, 8);
        Assert.Equal(29.45, placements[1].Top, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(64.5, letters[0].StartBaseLine.Y, 8);
        Assert.Equal(46.9125, letters[1].StartBaseLine.Y, 8);
    }

    [Fact]
    public void AuthoredMarginSidesReplaceEachDefaultSide()
    {
        var book = EpubDocument.Open(Book("<h2 style='font-size:10px;font-weight:normal;margin:3px 4px 5px'>A</h2>" + Paragraph));
        var regular = Face(false); var bold = Face(true);
        var placements = Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages).TextPlacements;
        Assert.Equal(6, placements[0].Left, 8);
        Assert.Equal(5, placements[0].Top, 8);
        Assert.Equal(20, placements[1].Top, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(4.5, letters[0].StartBaseLine.X, 8);
        Assert.Equal(65.25, letters[0].StartBaseLine.Y, 8);
        Assert.Equal(54, letters[1].StartBaseLine.Y, 8);
    }

    [Theory]
    [InlineData("<h2/>", 14.45, 58.1625)]
    [InlineData("<h2></h2>", 14.45, 58.1625)]
    [InlineData("<h2 style='display:none'>A</h2>", 2, 67.5)]
    public void EmptyAndSuppressedDefaultHeadingsDoNotInventTextLines(string heading, double top, double baseline)
    {
        var book = EpubDocument.Open(Book(heading + Paragraph));
        var regular = Face(false); var bold = Face(true);
        var page = Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages);
        Assert.Equal("A", page.Text);
        Assert.Equal(top, Assert.Single(page.TextPlacements).Top, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
        Assert.Equal(baseline, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y, 8);
    }

    [Fact]
    public void InheritedColorFamilyAndLineHeightSurviveHeadingDefaults()
    {
        var book = EpubDocument.Open(Book("<div style='color:red;font-family:Fixture;line-height:30px'><h2 style='margin:0'>A<br/>A</h2></div>"));
        var regular = Face(false); var bold = Face(true);
        EpubFontFaceResult? FamilyFace(EpubFontFaceRequest request) => request.Family?.Name == "Fixture"
            ? Resolve(request, regular, bold) : null;
        var layout = book.CreateLayout(NativeOptions(regular, bold) with { FontFaceResolver = FamilyFace });
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(9.5, placements[0].Top, 8);
        Assert.Equal(39.5, placements[1].Top, 8);
        Assert.All(placements, item => { Assert.Equal(15, item.Height, 8); Assert.Equal(new PdfRgbColor(1, 0, 0), item.Color); });
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold) with { FontFaceResolver = FamilyFace }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(58.875, letters[0].StartBaseLine.Y, 8);
        Assert.Equal(36.375, letters[1].StartBaseLine.Y, 8);
        Assert.All(letters, item => Assert.EndsWith("KillerBold", item.FontName));
    }

    [Fact]
    public void UnresolvedDefaultBoldFallsBackAndExplicitNormalNeedsNoTypedProvider()
    {
        var regular = Face(false); var bold = Face(true);
        var unresolved = EpubDocument.Open(Book("<h2>A</h2>"));
        var native = NativeOptions(regular, bold) with { FontFaceResolver = null };
        var options = PdfOptions(regular, bold) with { FontFaceResolver = null };
        // The default face stands in for the missing bold face instead of refusing the chapter.
        Assert.Empty(unresolved.CreateLayout(native).ChapterDiagnostics);
        Assert.NotEmpty(unresolved.ConvertToPdf(options));
        var supported = EpubDocument.Open(Book("<h2 style='font-weight:normal;margin:0'>A</h2>"));
        Assert.Equal(9, Assert.Single(Assert.Single(supported.CreateLayout(native).Pages).TextPlacements).Width, 8);
        var pdf = PdfDocument.Open(supported.ConvertToPdf(options));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal(6.75, letter.EndBaseLine.X - letter.StartBaseLine.X, 8);
        Assert.EndsWith("KillerTest", letter.FontName);
    }

    [Theory]
    [InlineData("margin-top:1px")]
    [InlineData("margin-left:1px")]
    // Inline margins are not laid out yet, so they are dropped instead of refusing the chapter.
    public void AuthoredNonzeroInlineHeadingMarginsAreIgnored(string margin)
    {
        var regular = Face(false); var bold = Face(true);
        var book = EpubDocument.Open(Book($"<h2 style='display:inline;{margin}'>A</h2>"));
        EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book("<h2 style='display:inline'>A</h2>")).CreateLayout(NativeOptions(regular, bold)),
            book.CreateLayout(NativeOptions(regular, bold)));
        Assert.NotEmpty(book.ConvertToPdf(PdfOptions(regular, bold)));
    }

    private void CheckHeading(string body, string? css, double height, double width,
        double pdfWidth, double baseline, string fontName)
    {
        byte[] source = Book(body, css), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false); var bold = Face(true);
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        var placement = Assert.Single(Assert.Single(book.CreateLayout(NativeOptions(regular, bold)).Pages).TextPlacements);
        Assert.Equal(2, placement.Left, 8); Assert.Equal(2, placement.Top, 8);
        Assert.Equal(height, placement.Height, 8); Assert.Equal(width, placement.Width, 8);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(regular, bold)));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal(baseline, letter.StartBaseLine.Y, 8);
        Assert.Equal(pdfWidth, letter.EndBaseLine.X - letter.StartBaseLine.X, 8);
        Assert.EndsWith(fontName, letter.FontName);
        Assert.Equal(original, source); Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    private static EpubFontFaceResult? Resolve(EpubFontFaceRequest request, TrueTypeFont regular, TrueTypeFont bold) => request.Weight switch
    {
        400 => new(regular, 400, EpubFontMatch.Exact),
        700 => new(bold, 700, EpubFontMatch.Exact),
        _ => throw new InvalidOperationException("Unexpected fixture weight.")
    };
    private static EpubLayoutOptions NativeOptions(TrueTypeFont regular, TrueTypeFont bold) => new(100, 100, regular)
    { FontSize = 10, Margin = 2, FontFaceResolver = request => Resolve(request, regular, bold) };
    private static EpubPdfConversionOptions PdfOptions(TrueTypeFont regular, TrueTypeFont bold) => new(regular)
    {
        PageWidthPoints = 75, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5,
        FontFaceResolver = request => Resolve(request, regular, bold)
    };
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
        }
        return TrueTypeFont.Load(bytes);
    }
    private static byte[] Book(string body, string? css = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>h2-defaults</dc:identifier><dc:title>H2 defaults</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + "</manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'>" + (css is null ? "" : "<head><link rel='stylesheet' href='style.css'/></head>") + "<body>" + body + "</body></html>");
            if (css is not null) Add("style.css", css);
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
