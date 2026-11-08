using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubFontStyleTests
{
    [Fact]
    public void BoldAndItalicComposeWithoutLosingEitherActualFaceSelection()
    {
        byte[] source = Book("<p><i><strong>A</strong><span style='font-style:normal'>A</span></i><b><em>A</em></b></p>");
        byte[] original = source.ToArray();
        var regular = Face(false); var italic = Face(true);
        var bold = Face(false, true); var both = Face(true, true);
        EpubFontFaceResult Resolve(EpubFontFaceRequest request) => new(
            request.Weight == 700 ? request.Style == EpubFontStyle.Italic ? both : bold
                : request.Style == EpubFontStyle.Italic ? italic : regular, request.Weight, EpubFontMatch.Exact)
            { ActualStyle = request.Style };
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }).Pages);
        Assert.Equal(new double[] { 15, 6, 15 }, page.TextPlacements.Select(item => item.Width));
        Assert.Equal(new double[] { 2, 17, 23 }, page.TextPlacements.Select(item => item.Left));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 39, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
                FontFaceResolver = Resolve }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new double[] { 11.25, 4.5, 11.25 }, letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
        Assert.Equal(new[] { "KillerBoth", "KillerTest", "KillerBoth" },
            letters.Select(item => item.FontName[(item.FontName.LastIndexOf('+') + 1)..]));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("legacy")]
    [InlineData("old-callback")]
    [InlineData("invalid-style")]
    [InlineData("wrong-style-nearest")]
    [InlineData("wrong-match")]
    [InlineData("missing-glyph")]
    public void ItalicCannotSilentlyUseUprightFallbackOrAnInconsistentFaceResult(string mode)
    {
        byte[] source = Book("<p><i>A</i></p>"), original = source.ToArray();
        var regular = Face(false); var italic = Face(true, missingA: mode == "missing-glyph");
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request)
        {
            if (request.Style == EpubFontStyle.Normal) return new(regular, 400, EpubFontMatch.Exact);
            return mode switch
            {
                "null" => null,
                "missing-glyph" => new(italic, 400, EpubFontMatch.Exact) { ActualStyle = EpubFontStyle.Italic },
                "old-callback" => new(regular, 400, EpubFontMatch.Exact),
                "invalid-style" => new(italic, 400, EpubFontMatch.Exact) { ActualStyle = (EpubFontStyle)999 },
                "wrong-style-nearest" => new(regular, 700, EpubFontMatch.Nearest),
                "wrong-match" => new(italic, 400, EpubFontMatch.Nearest) { ActualStyle = EpubFontStyle.Italic },
                _ => throw new InvalidOperationException("Unexpected resolver mode")
            };
        }
        Func<EpubFontFaceRequest, EpubFontFaceResult?>? resolver = mode is "missing" or "legacy" ? null : Resolve;
        Func<EpubFontFamily, TrueTypeFont?>? legacy = mode == "legacy" ? _ => regular : null;
        var book = EpubDocument.Open(source);
        var native = Record.Exception(() => book.CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = resolver, FontFamilyResolver = legacy }));
        var converted = Record.Exception(() => book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 39, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
                FontFaceResolver = resolver, FontFamilyResolver = legacy }));
        // With no usable italic face the text falls back to the upright default face, as a user agent
        // does for a family without that face; an inconsistent resolver answer is still a host fault.
        foreach (var error in new[] { native, converted })
            if (mode is "missing" or "legacy" or "null" or "missing-glyph") Assert.Null(error);
            else Assert.Contains("inconsistent face match", Assert.IsType<InvalidDataException>(error).Message);
        if (mode is "missing" or "legacy" or "null" or "missing-glyph")
        {
            var options = new EpubLayoutOptions(52, 26, regular) { FontSize = 10, Margin = 2, FontFaceResolver = resolver, FontFamilyResolver = legacy };
            EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book("<p><span>A</span></p>")).CreateLayout(options),
                book.CreateLayout(options));
        }
        Assert.Equal(original, source);
    }

    [Fact]
    public void ItalicNearestWeightStillRequiresAndRetainsTheRequestedStyle()
    {
        byte[] source = Book("<p><i>A</i></p>"), original = source.ToArray();
        var regular = Face(false); var italic = Face(true);
        EpubFontFaceResult Resolve(EpubFontFaceRequest request) => request.Style == EpubFontStyle.Italic
            ? new(italic, 700, EpubFontMatch.Nearest) { ActualStyle = EpubFontStyle.Italic }
            : new(regular, 400, EpubFontMatch.Exact);
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve });
        Assert.Equal(9, Assert.Single(Assert.Single(layout.Pages).TextPlacements).Width);
        Assert.Equal(original, source);
    }

    [Fact]
    public void AlternatingDefaultFamilyStylesDoNotShareTheWrongCachedFace()
    {
        byte[] source = Book("<p>A<i>A</i>A</p>", "p{margin:0}"), original = source.ToArray();
        var regular = Face(false); var italic = Face(true);
        EpubFontFaceResult Resolve(EpubFontFaceRequest request) => new(
            request.Style == EpubFontStyle.Italic ? italic : regular, 400, EpubFontMatch.Exact)
            { ActualStyle = request.Style };
        var page = Assert.Single(EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }).Pages);
        Assert.Equal(new double[] { 6, 9, 6 }, page.TextPlacements.Select(item => item.Width));
        Assert.Equal(new double[] { 2, 8, 17 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("font-style", "inherit", 9, 6.75)]
    [InlineData("font-style", "unset", 9, 6.75)]
    [InlineData("font-style", "initial", 6, 4.5)]
    [InlineData("all", "inherit", 9, 6.75)]
    [InlineData("all", "initial", 6, 4.5)]
    public void FontStyleWideKeywordsUseTheActualParentOrUprightInitial(string property, string keyword, double width, double pdfWidth)
    {
        byte[] source = Book("<p><i><span style='" + property + ":" + keyword + "'>A</span></i></p>");
        byte[] original = source.ToArray();
        var regular = Face(false); var italic = Face(true);
        EpubFontFaceResult Resolve(EpubFontFaceRequest request) => new(
            request.Style == EpubFontStyle.Italic ? italic : regular, 400, EpubFontMatch.Exact)
            { ActualStyle = request.Style };
        var book = EpubDocument.Open(source);
        var page = Assert.Single(book.CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve }).Pages);
        Assert.Equal(width, Assert.Single(page.TextPlacements).Width);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 39, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
                FontFaceResolver = Resolve }));
        var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
        Assert.Equal(pdfWidth, letter.EndBaseLine.X - letter.StartBaseLine.X);
        Assert.Equal(original, source);
    }

    [Fact]
    public void ItalicAndUprightSpansKeepTheirRealFaceMetricsAndInkInNativeAndSavedPdf()
    {
        byte[] source = Book("<p>A<i>A<span style='font-style:normal'>A</span></i><em>A</em><span style='font-style:italic'>A</span></p>");
        byte[] original = source.ToArray();
        var regular = Face(false);
        var italic = Face(true);
        EpubFontFaceResult Resolve(EpubFontFaceRequest request) => new(
            request.Style == EpubFontStyle.Italic ? italic : regular, 400, EpubFontMatch.Exact)
            { ActualStyle = request.Style };
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var layout = book.CreateLayout(new EpubLayoutOptions(52, 26, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("AAAAA", page.Text);
        Assert.Equal(new double[] { 6, 9, 6, 9, 9 }, page.TextPlacements.Select(item => item.Width));
        Assert.Equal(new double[] { 2, 8, 17, 23, 32 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new float[] { 0, 0, 0, 1 },
            layout.RenderPage(0).Pixels.Slice((9 * 52 + 44) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            { PageWidthPoints = 39, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5,
                FontFaceResolver = Resolve }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new double[] { 1.5, 6, 12.75, 17.25, 24 }, letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(new double[] { 4.5, 6.75, 4.5, 6.75, 6.75 },
            letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
        Assert.Equal(new[] { "KillerTest", "KillerLean", "KillerTest", "KillerLean", "KillerLean" },
            letters.Select(item => item.FontName[(item.FontName.LastIndexOf('+') + 1)..]));
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, new PdfPageRenderer(pdf)
            .Render(0, new PdfRenderOptions(52, 26)).Pixels.Slice((9 * 52 + 44) * 4, 4).ToArray());
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    private static TrueTypeFont Face(bool italic, bool bold = false, bool missingA = false)
    {
        byte[] bytes = TrueTypeFontTests.BuildTestFont(false, includeOutlines: true);
        if (!italic && !bold) return TrueTypeFont.Load(bytes);
        TrueTypeFontTests.RenameFace(bytes, bold ? italic ? "KillerBoth" : "KillerBold" : "KillerLean");
        int tables = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2));
        for (int entry = 12; entry < 12 + tables * 16; entry += 16)
        {
            string tag = Encoding.ASCII.GetString(bytes, entry, 4);
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(entry + 8, 4));
            if (tag == "cmap" && missingA) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 36, 2), 0xFFBF);
            if (tag == "hmtx") BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset + 4, 2), (ushort)(bold ? italic ? 1500 : 1200 : 900));
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

    private static byte[] Book(string body, string css = "p{margin:0;font-family:Fixture}")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>style</dc:identifier><dc:title>Style</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
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
