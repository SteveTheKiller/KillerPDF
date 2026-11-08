using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using Xunit.Abstractions;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubFontKeywordTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InheritUsesActualParentSizeAndWeightAtBothPublicSeams(bool convert)
    {
        Check("<p style='font-size:20px;font-weight:bold;margin:0'><span style='font-size:inherit;font-weight:inherit'>A</span></p>",
            null, convert, 20, 24, 18, "KillerBold");
    }

    [Theory]
    [InlineData("unset", 20, 24, 18, "KillerBold", 46.5, 2)]
    [InlineData("initial", 10, 6, 4.5, "KillerTest", 46.5, 10)]
    [InlineData("InHeRiT", 20, 24, 18, "KillerBold", 46.5, 2)]
    [InlineData("UnSeT", 20, 24, 18, "KillerBold", 46.5, 2)]
    [InlineData("InItIaL", 10, 6, 4.5, "KillerTest", 46.5, 10)]
    public void KeywordSizeAndWeightUseTheirLiteralParentOrReaderInitialValues(string keyword,
        double height, double width, double pdfWidth, string name, double baseline, double top)
    {
        foreach (bool convert in new[] { false, true })
            Check($"<p style='font-size:20px;font-weight:bold;margin:0'><span style='font-size:{keyword};font-weight:{keyword}'>A</span></p>",
                null, convert, height, width, pdfWidth, name, baseline, top);
    }

    [Theory]
    [InlineData("font-size:initial;font-weight:inherit", 10, 12, 9, "KillerBold", 46.5, 10)]
    [InlineData("font-size:inherit;font-weight:initial", 20, 12, 9, "KillerTest", 46.5, 2)]
    public void InitialAndInheritedPropertiesRemainIndependent(string declarations,
        double height, double width, double pdfWidth, string name, double baseline, double top)
    {
        foreach (bool convert in new[] { false, true })
            Check($"<p style='font-size:20px;font-weight:bold;margin:0'><span style='{declarations}'>A</span></p>",
                null, convert, height, width, pdfWidth, name, baseline, top);
    }

    [Theory]
    [InlineData(".pick{font-size:inherit;font-weight:inherit} span{font-size:8px;font-weight:normal}", "")]
    [InlineData(".pick{font-size:8px;font-weight:normal}.pick{font-size:unset;font-weight:unset}", "")]
    [InlineData("#pick{font-size:10px;font-weight:normal}", " style='font-size:inherit;font-weight:inherit'")]
    [InlineData(".pick{font-size:initial;font-weight:initial}", " style='font-size:unset;font-weight:unset'")]
    [InlineData("", " style='font-size:8px;font-weight:normal;font-size:inherit;font-weight:inherit'")]
    public void PresentInheritanceDeclarationsWinSpecificityOrderAndInlineOverride(string css, string attributes)
    {
        foreach (bool convert in new[] { false, true })
            Check("<p style='font-size:20px;font-weight:bold;margin:0'><span id='pick' class='pick'" + attributes + ">A</span></p>",
                css.Length == 0 ? null : css, convert, 20, 24, 18, "KillerBold");
    }

    [Fact]
    public void InitialInlineDeclarationsOverrideEarlierInheritedStylesheetValues()
    {
        foreach (bool convert in new[] { false, true })
            Check("<p style='font-size:20px;font-weight:bold;margin:0'><span class='pick' style='font-size:initial;font-weight:initial'>A</span></p>",
                ".pick{font-size:inherit;font-weight:inherit}", convert, 10, 6, 4.5, "KillerTest", 46.5, 10);
    }

    [Fact]
    public void InheritanceUsesImmediateComputedParentInsteadOfOuterAncestor()
    {
        foreach (bool convert in new[] { false, true })
            Check("<div style='font-size:20px;font-weight:bold'><p style='font-size:15px;font-weight:normal;margin:0'><span style='font-size:inherit;font-weight:unset'>A</span></p></div>",
                null, convert, 15, 9, 6.75, "KillerTest", 49.5);
    }

    [Theory]
    [InlineData("font-weight:0")]
    [InlineData("font-weight:bolder")]
    [InlineData("font-weight:lighter")]
    [InlineData("font-size:revert")]
    [InlineData("line-height:inherit")]
    // Unsupported keyword values are ignored declarations, so the inherited values stay.
    public void UnsupportedKeywordValuesAreIgnored(string declaration)
    {
        var font = Face(false);
        var options = new EpubLayoutOptions(100, 80, font) { FontSize = 10, Margin = 2 };
        var book = EpubDocument.Open(Book($"<p style='{declaration}'>A</p>", null));
        EpubCompatibilityTests.AssertSameLayout(EpubDocument.Open(Book("<p>A</p>", null)).CreateLayout(options), book.CreateLayout(options));
        Assert.NotEmpty(book.ConvertToPdf(new EpubPdfConversionOptions(font)));
    }

    private void Check(string body, string? css, bool convert, double height, double width,
        double pdfWidth, string fontName, double pdfBaseline = 46.5, double nativeTop = 2)
    {
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        byte[] source = Book(body, css), original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var regular = Face(false);
        var bold = Face(true);
        Assert.Equal(800, regular.Ascender);
        Assert.Equal(800, bold.Ascender);
        Assert.Equal(1000, regular.UnitsPerEm);
        Assert.Equal(1000, bold.UnitsPerEm);
        EpubFontFaceResult? Resolve(EpubFontFaceRequest request) => request.Weight switch
        {
            400 => new(regular, 400, EpubFontMatch.Exact),
            700 => new(bold, 700, EpubFontMatch.Exact),
            _ => throw new InvalidOperationException("Unexpected fixture weight.")
        };
        Assert.Equal("A", book.ReadText(0));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(regular)
            {
                PageWidthPoints = 75, PageHeightPoints = 60, MarginPoints = 1.5,
                FontSizePoints = 7.5, FontFaceResolver = Resolve
            }));
            var letter = Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters);
            Assert.Equal("A", letter.Value);
            Assert.Equal(1.5, letter.StartBaseLine.X);
            Assert.Equal(pdfWidth, letter.EndBaseLine.X - letter.StartBaseLine.X);
            Assert.Equal(pdfBaseline, letter.StartBaseLine.Y);
            Assert.EndsWith(fontName, letter.FontName);
        }
        else
        {
            var layout = book.CreateLayout(new EpubLayoutOptions(100, 80, regular)
            { FontSize = 10, Margin = 2, FontFaceResolver = Resolve });
            var placement = Assert.Single(Assert.Single(layout.Pages).TextPlacements);
            Assert.Equal(0, placement.Start);
            Assert.Equal(2, placement.Left);
            Assert.Equal(nativeTop, placement.Top);
            Assert.Equal(height, placement.Height);
            Assert.Equal(width, placement.Width);
        }
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
        }
        return TrueTypeFont.Load(bytes);
    }

    private static byte[] Book(string body, string? css)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>font-keywords</dc:identifier><dc:title>Font keywords</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + "</manifest><spine><itemref idref='doc'/></spine></package>");
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
