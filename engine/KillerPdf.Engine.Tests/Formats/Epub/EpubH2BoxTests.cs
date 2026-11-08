using System.IO.Compression;
using System.Buffers.Binary;
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

public sealed class EpubH2BoxTests(ITestOutputHelper output)
{
    private const string Typography = "font-size:10px;font-weight:normal;margin:0";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExplicitHeadingAndParagraphKeepLiteralGeometryAtBothPublicSeams(bool convert)
    {
        output.WriteLine("Loaded engine SHA256: " + Convert.ToHexString(SHA256.HashData(
            File.ReadAllBytes(typeof(EpubDocument).Assembly.Location))));
        byte[] source = Book($"<h2 style='{Typography}'>A</h2><p style='{Typography}'>A</p>");
        byte[] original = source.ToArray();
        var book = EpubDocument.Open(source);
        byte[] chapter = book.ReadResource("doc.xhtml");
        var font = Font();
        Assert.Equal(800, font.Ascender);
        Assert.Equal(1000, font.UnitsPerEm);
        Assert.Equal("A\n\nA", book.ReadText(0));
        if (convert)
        {
            var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
            var reader = new PdfPageContentReader(pdf);
            Assert.Equal(1, reader.PageCount);
            var letters = reader.Read(0).Letters;
            Assert.Equal(new[] { "A", "A" }, letters.Select(item => item.Value));
            Assert.Equal(new double[] { 1.5, 1.5 }, letters.Select(item => item.StartBaseLine.X));
            Assert.Equal(new double[] { 4.5, 4.5 }, letters.Select(item => item.EndBaseLine.X - item.StartBaseLine.X));
            Assert.Equal(new double[] { 30, 22.5 }, letters.Select(item => item.StartBaseLine.Y));
        }
        else
        {
            var page = Assert.Single(book.CreateLayout(NativeOptions(font)).Pages);
            Assert.Equal("A\n\nA", page.Text);
            Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
            Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(item => item.Left));
            Assert.Equal(new double[] { 10, 10 }, page.TextPlacements.Select(item => item.Height));
            Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        }
        Assert.Equal(original, source);
        Assert.Equal(chapter, book.ReadResource("doc.xhtml"));
    }

    [Fact]
    public void ExplicitBoxWidthAutoMarginsPaddingBorderAndBackgroundKeepLiteralGeometry()
    {
        var book = EpubDocument.Open(Book($"<h2 style='{Typography};width:20px;margin:0 auto;padding:2px;border-top:2px solid red;background-color:blue'>A</h2><p style='{Typography}'>A</p>"));
        var font = Font();
        var layout = book.CreateLayout(NativeOptions(font));
        var placements = Assert.Single(layout.Pages).TextPlacements;
        Assert.Equal(new double[] { 10, 2 }, placements.Select(item => item.Left));
        Assert.Equal(new double[] { 6, 18 }, placements.Select(item => item.Top));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((2 * 40 + 20) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 1, 1 }, native.Pixels.Slice((5 * 40 + 8) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new double[] { 7.5, 1.5 }, letters.Select(item => item.StartBaseLine.X));
        Assert.Equal(new double[] { 27, 18 }, letters.Select(item => item.StartBaseLine.Y));
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 50));
        Assert.Empty(saved.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, saved.Pixels.Slice((2 * 40 + 20) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, saved.Pixels.Slice((5 * 40 + 8) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData("/>")]
    [InlineData("></h2>")]
    [InlineData(">  </h2>")]
    public void EmptyHeadingCollapsesMarginsWithoutInventingALine(string ending)
    {
        var book = EpubDocument.Open(Book($"<h2 style='{Typography};margin-bottom:8px'>A</h2><h2 style='{Typography};margin-top:12px;margin-bottom:4px'" + ending + $"<p style='{Typography};margin-top:6px'>A</p>"));
        var font = Font();
        Assert.Equal("A\n\nA", book.ReadText(0));
        var page = Assert.Single(book.CreateLayout(NativeOptions(font)).Pages);
        Assert.Equal(new[] { 0, 3 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 24 }, page.TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        Assert.Equal(new double[] { 30, 13.5 }, new PdfPageContentReader(pdf).Read(0).Letters.Select(item => item.StartBaseLine.Y));
    }

    [Fact]
    public void HeadingInlineChildrenAndSemanticIdentityKeepSourceAndNavigation()
    {
        var book = EpubDocument.Open(Book($"<h2 id='mark' class='heading' epub:type='title' style='{Typography}'><span>A</span><br/><a href='#mark'>A</a></h2>"));
        var font = Font();
        Assert.Equal("A\nA", book.ReadText(0));
        var layout = book.CreateLayout(NativeOptions(font));
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new[] { 0, 2 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal("doc.xhtml#mark", page.TextPlacements[1].LinkTarget);
        var target = layout.ResolveNavigationTarget("doc.xhtml#mark");
        Assert.Equal(0, target.PageIndex);
        Assert.Equal(0, target.TextOffset);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        Assert.Equal("AA", string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(item => item.Value)));
    }

    [Fact]
    public void ExplicitHeadingImageRetainsItsPaintAndFollowingParagraphOrder()
    {
        var book = EpubDocument.Open(Book($"<h2 style='{Typography}'><img src='image.png'/></h2><p style='{Typography}'>A</p>", image: true));
        var font = Font();
        var layout = book.CreateLayout(NativeOptions(font));
        Assert.Equal("A", Assert.Single(layout.Pages).Text);
        Assert.Equal(12, Assert.Single(layout.Pages[0].TextPlacements).Top);
        var native = layout.RenderPage(0);
        Assert.Contains(Enumerable.Range(0, 40 * 50), index => native.Pixels[index * 4] == 1
            && native.Pixels[index * 4 + 1] == 0 && native.Pixels[index * 4 + 2] == 0);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        Assert.Equal(22.5, Assert.Single(new PdfPageContentReader(pdf).Read(0).Letters).StartBaseLine.Y);
        var saved = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(40, 50));
        Assert.Empty(saved.Diagnostics);
        Assert.Contains(Enumerable.Range(0, 40 * 50), index => saved.Pixels.Span[index * 4] == 0
            && saved.Pixels.Span[index * 4 + 1] == 0 && saved.Pixels.Span[index * 4 + 2] == 255);
    }

    [Theory]
    [InlineData("page-break-before:always")]
    [InlineData("page-break-after:always")]
    public void ExplicitHeadingBreakSeparatesContentPagesAndAnchor(string forcedBreak)
    {
        string body = forcedBreak.StartsWith("page-break-before", StringComparison.Ordinal)
            ? $"<p style='{Typography}'>A</p><h2 id='next' style='{Typography};{forcedBreak}'>A</h2>"
            : $"<h2 style='{Typography};{forcedBreak}'>A</h2><p id='next' style='{Typography}'>A</p>";
        var book = EpubDocument.Open(Book(body));
        var font = Font();
        var layout = book.CreateLayout(NativeOptions(font));
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(2, Assert.Single(layout.Pages[1].TextPlacements).Top);
        Assert.Equal(1, layout.ResolveNavigationTarget("doc.xhtml#next").PageIndex);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(30, Assert.Single(reader.Read(1).Letters).StartBaseLine.Y);
    }

    [Theory]
    [InlineData("inline", 8)]
    [InlineData("none", 2)]
    public void HeadingDisplayOverrideKeepsStructuralTextIndependentOfPaint(string display, double left)
    {
        var book = EpubDocument.Open(Book($"<h2 style='{Typography};display:{display}'>A</h2><p style='{Typography};display:inline'>A</p>"));
        var font = Font();
        Assert.Equal("A\n\nA", book.ReadText(0));
        var page = Assert.Single(book.CreateLayout(NativeOptions(font)).Pages);
        Assert.Equal(left, page.TextPlacements[^1].Left);
        Assert.Equal(2, page.TextPlacements[^1].Top);
        Assert.Equal(display == "none" ? "A" : "A\n\nA", page.Text);
        var pdf = PdfDocument.Open(book.ConvertToPdf(PdfOptions(font)));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(display == "none" ? "A" : "AA", string.Concat(letters.Select(item => item.Value)));
        Assert.Equal(display == "none" ? 1.5 : 6, letters[^1].StartBaseLine.X);
    }

    [Theory]
    [InlineData("<h2><a href='javascript:execute()'>A</a></h2>", "External or unresolved EPUB XHTML references are unsupported.")]
    // The linked image itself is admitted now; its unpackaged source is what still refuses the chapter.
    [InlineData("<h2><a href='#x'><img src='image.png'/></a></h2>", "External or unresolved EPUB XHTML references are unsupported.")]
    public void InvalidNestingAndUnsafeReferencesRetainExplicitRefusal(string body, string message)
        => AssertRefused(body, message);

    // Nesting HTML admits, unknown elements and an image inside a link lay out; the link has no image hit region yet.
    [Theory]
    [InlineData("<p><h2>A</h2></p>")]
    [InlineData("<h2><h2>A</h2></h2>")]
    [InlineData("<h2><p>A</p></h2>")]
    [InlineData("<h7>A</h7>")]
    public void NestedHeadingsUnknownElementsAndLinkedImagesLayOut(string body)
    {
        EpubCompatibilityTests.AssertLaysOut(EpubDocument.Open(Book(body, "h2{font-weight:normal}")), NativeOptions(Font()));
    }

    private static void AssertRefused(string body, string message)
    {
        var book = EpubDocument.Open(Book(body, "h2{font-weight:normal}"));
        var font = Font();
        Assert.Equal(message, Assert.Throws<NotSupportedException>(() => book.CreateLayout(NativeOptions(font))).Message);
        Assert.Equal(message, Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(PdfOptions(font))).Message);
    }

    private static TrueTypeFont Font() => TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
    private static EpubLayoutOptions NativeOptions(TrueTypeFont font) => new(40, 50, font) { FontSize = 10, Margin = 2 };
    private static EpubPdfConversionOptions PdfOptions(TrueTypeFont font) => new(font)
    { PageWidthPoints = 30, PageHeightPoints = 37.5, MarginPoints = 1.5, FontSizePoints = 7.5 };
    private static byte[] Book(string body, string? css = null, bool image = false)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>h2-box</dc:identifier><dc:title>H2 box</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/>" + (css is null ? "" : "<item id='css' href='style.css' media-type='text/css'/>") + (image ? "<item id='image' href='image.png' media-type='image/png'/>" : "") + "</manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml' xmlns:epub='http://www.idpf.org/2007/ops'>" + (css is null ? "" : "<head><link rel='stylesheet' href='style.css'/></head>") + "<body>" + body + "</body></html>");
            if (css is not null) Add("style.css", css);
            if (image)
            {
                using var stream = zip.CreateEntry("image.png", CompressionLevel.NoCompression).Open();
                stream.Write(Png());
            }
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }

    private static byte[] Png()
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        Chunk("IHDR", [0, 0, 0, 2, 0, 0, 0, 2, 8, 2, 0, 0, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, true))
            zlib.Write(new byte[] { 0, 255, 0, 0, 0, 255, 0, 0, 0, 0, 255, 0, 0, 0 });
        Chunk("IDAT", compressed.ToArray());
        Chunk("IEND", []);
        return output.ToArray();
        void Chunk(string type, byte[] data)
        {
            Span<byte> integer = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(integer, (uint)data.Length);
            output.Write(integer);
            byte[] name = Encoding.ASCII.GetBytes(type);
            output.Write(name);
            output.Write(data);
            uint crc = uint.MaxValue;
            foreach (byte value in name.Concat(data))
            {
                crc ^= value;
                for (int bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
            }
            BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
            output.Write(integer);
        }
    }
}
