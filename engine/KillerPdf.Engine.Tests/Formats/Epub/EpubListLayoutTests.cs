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

public sealed class EpubListLayoutTests
{
    [Fact]
    public void MarkersFollowFirstLinePageAndNeverRepeatOnContinuationPages()
    {
        byte[] source = Book("<p>A<br/>A</p><ul><li>A<br/>A<br/>A</li><li id='next'>A</li></ul>",
            "p{margin:0}ul{margin:0;color:red}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(120, 26, font) { FontSize = 10, Margin = 2 });
        Assert.Equal(3, layout.Pages.Count);
        Assert.Equal("A\nA\n\nA\nA\nA\nA", string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(new[] { 0, 2 }, layout.Pages[0].TextPlacements.Select(item => item.Start));
        Assert.Equal(new[] { 5, 7 }, layout.Pages[1].TextPlacements.Select(item => item.Start));
        Assert.Equal(new[] { 9, 11 }, layout.Pages[2].TextPlacements.Select(item => item.Start));
        Assert.Equal(new EpubNavigationDestination(2, 0, 11, 42, 12), layout.ResolveNavigationTarget("doc.xhtml#next"));
        Assert.Equal(new float[] { 1, 1, 1, 1 }, layout.RenderPage(0).Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, layout.RenderPage(1).Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        var last = layout.RenderPage(2);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, last.Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, last.Pixels.Slice((16 * 120 + 33) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 90, PageHeightPoints = 19.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        Assert.Equal(3, new PdfPageContentReader(pdf).PageCount);
        var renderer = new PdfPageRenderer(pdf);
        var before = renderer.Render(0, new PdfRenderOptions(120, 26));
        var first = renderer.Render(1, new PdfRenderOptions(120, 26));
        var continuation = renderer.Render(2, new PdfRenderOptions(120, 26));
        Assert.Empty(before.Diagnostics); Assert.Empty(first.Diagnostics); Assert.Empty(continuation.Diagnostics);
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, before.Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, first.Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, continuation.Pixels.Slice((6 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, continuation.Pixels.Slice((16 * 120 + 33) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void GeneratedDiscsStaySeparateFromLiteralSourceBulletAndItemDestination()
    {
        byte[] source = Book("<ul><li>&#8226;A</li><li id='next'>A</li></ul>", "ul{margin:0;color:red}"),
            original = source.ToArray();
        int[] scalars = [0x41, 0x2022];
        byte[] cmap = new byte[52];
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(4), 3);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(6), 10);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(8), 12);
        BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(12), 12);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(16), 40);
        BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(24), 2);
        for (int index = 0; index < scalars.Length; index++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(28 + index * 12), (uint)scalars[index]);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(32 + index * 12), (uint)scalars[index]);
            BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(36 + index * 12), 1);
        }
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true, cmap: cmap));
        var book = EpubDocument.Open(source);
        Assert.Equal("\u2022A\nA", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(120, 80, font) { FontSize = 20, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("\u2022A\nA", page.Text);
        Assert.Equal(new[] { 0, 1, 3 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new EpubNavigationDestination(0, 0, 3, 42, 22), layout.ResolveNavigationTarget("doc.xhtml#next"));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((11 * 120 + 26) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((7 * 120 + 22) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((5 * 120 + 52) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 90, PageHeightPoints = 60, MarginPoints = 1.5, FontSizePoints = 15 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "\u2022", "A", "A" }, letters.Select(letter => letter.Value));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(120, 80));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((11 * 120 + 26) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((7 * 120 + 22) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((5 * 120 + 52) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Theory]
    [InlineData("<ul><li><br/></li></ul>")]
    [InlineData("<ul><li><p style='padding-top:1px'></p></li></ul>")]
    [InlineData("<ul><li/></ul>")]
    [InlineData("<ul><li>  </li></ul>")]
    [InlineData("<ul>A<li>A</li></ul>")]
    [InlineData("<ul><li><ul><li>A</li></ul></li></ul>")]
    public void UnsupportedListShapesKeepExplicitResults(string body)
    {
        byte[] source = Book(body, "ul{margin:0}"), original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(new EpubLayoutOptions(120, 60, font)
        { FontSize = 10, Margin = 2 }));
        Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(font)));
        Assert.Equal(original, source);
    }

    // A paragraph directly in a list flows as a block, and ordered-list items lay out as indented blocks
    // (numbers are not drawn yet) instead of refusing the chapter.
    [Theory]
    [InlineData("<ul><p>A</p></ul>")]
    [InlineData("<ol><li>A</li></ol>")]
    public void ParagraphsInListsAndOrderedListsLayOut(string body)
    {
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        EpubCompatibilityTests.AssertLaysOut(EpubDocument.Open(Book(body, "ul{margin:0}")), new EpubLayoutOptions(120, 60, font) { FontSize = 10, Margin = 2 });
    }

    [Fact]
    public void UnorderedListPaintsOutsideMarkersWithoutAddingSourceTextOrOffsets()
    {
        byte[] source = Book("<ul><li>A</li><li>A</li></ul><p>A</p>", "p{margin:0}ul{margin:0;color:red}"),
            original = source.ToArray();
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var book = EpubDocument.Open(source);
        Assert.Equal("A\nA\n\nA", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(120, 60, font) { FontSize = 10, Margin = 2 });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA\n\nA", page.Text);
        Assert.Equal(new[] { 0, 2, 5 }, page.TextPlacements.Select(item => item.Start));
        Assert.All(page.TextPlacements, item => Assert.Equal(1, item.Length));
        Assert.Equal(new double[] { 42, 42, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 12, 22 }, page.TextPlacements.Select(item => item.Top));
        var native = layout.RenderPage(0);
        for (int row = 0; row < 2; row++)
        {
            bool marker = false;
            for (int y = 2 + row * 10; y < 12 + row * 10; y++)
                for (int x = 20; x < 40; x++)
                {
                    int offset = (y * 120 + x) * 4;
                    marker |= native.Pixels[offset] > .9f && native.Pixels[offset + 1] < .5f;
                }
            Assert.True(marker, "Each item must paint its own red marker outside the text column.");
        }
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 90, PageHeightPoints = 45, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { "A", "A", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 31.5, 31.5, 1.5 }, letters.Select(letter => letter.StartBaseLine.X));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(120, 60));
        Assert.Empty(rendered.Diagnostics);
        for (int row = 0; row < 2; row++)
        {
            bool marker = false;
            for (int y = 2 + row * 10; y < 12 + row * 10; y++)
                for (int x = 20; x < 40; x++)
                {
                    int offset = (y * 120 + x) * 4;
                    marker |= rendered.Pixels.Span[offset + 2] > 230 && rendered.Pixels.Span[offset + 1] < 128;
                }
            Assert.True(marker, "Saved PDF must retain both outside markers.");
        }
        Assert.Equal(original, source);
    }

    private static byte[] Book(string body, string css)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>list</dc:identifier><dc:title>List</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/><item id='css' href='style.css' media-type='text/css'/></manifest><spine><itemref idref='doc'/></spine></package>");
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
