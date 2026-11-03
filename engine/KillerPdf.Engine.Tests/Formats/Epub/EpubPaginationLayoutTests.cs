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

public sealed class EpubPaginationLayoutTests
{
    [Fact]
    public void WrappedWordStartsTheNextPageAndRetainsOriginalSourceRanges()
    {
        var book = EpubDocument.Open(Book());
        Assert.Equal("A A", book.ReadText(0));
        var layout = book.CreateLayout(Options());
        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal("A ", layout.Pages[0].Text);
        Assert.Equal("A", layout.Pages[1].Text);
        Assert.Equal(0, layout.Pages[0].TextStart);
        Assert.Equal(2, layout.Pages[1].TextStart);
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(0, 2, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
        for (int index = 0; index < 2; index++)
        {
            var frame = layout.RenderPage(index);
            Assert.Equal(16, frame.Width);
            Assert.Equal(23, frame.Height);
            Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(frame.Pixels[(5 * 16 + 7) * 4 + channel], 0.21413370f, 0.21413373f);
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 16 + 14) * 4, 4).ToArray());
            for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
                Assert.Equal(1f, frame.Pixels[alpha]);
        }
    }

    [Fact]
    public void TwoLineBoxesThatExactlyFitStayOnOnePage()
    {
        var page = Assert.Single(EpubDocument.Open(Book()).CreateLayout(Options(24)).Pages);
        Assert.Equal("A A", page.Text);
        Assert.Equal(0, page.TextStart);
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 2, 1, 2, 12, 6, 10) }, page.TextPlacements);
    }

    [Fact]
    public void ThreeWrappedWordsKeepContiguousSlicesAndIndependentPagePlacements()
    {
        var book = EpubDocument.Open(Book("<p>A A A</p>"));
        var layout = book.CreateLayout(Options());
        Assert.Equal(3, layout.Pages.Count);
        Assert.Equal(new[] { "A ", "A ", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 2, 4 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(book.ReadText(0), string.Concat(layout.Pages.Select(page => page.Text)));
        for (int index = 0; index < 3; index++)
            Assert.Equal(new[] { new EpubTextPlacement(0, index * 2, 1, 2, 2, 6, 10) },
                layout.Pages[index].TextPlacements);
        var pages = Assert.IsAssignableFrom<IList<EpubLayoutPage>>(layout.Pages);
        Assert.True(pages.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => pages.Clear());
        var placements = Assert.IsAssignableFrom<IList<EpubTextPlacement>>(layout.Pages[0].TextPlacements);
        Assert.True(placements.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => placements.Clear());
    }

    [Theory]
    [InlineData(33, 2, 2)]
    [InlineData(34, 1, 22)]
    public void ParagraphGapIsPreservedWhenItFitsAndNeverCreatesABlankPage(double height, int pageCount, double secondTop)
    {
        var book = EpubDocument.Open(Book("<p>A</p><p>A</p>"));
        Assert.Equal("A\n\nA", book.ReadText(0));
        var layout = book.CreateLayout(Options(height));
        Assert.Equal(pageCount, layout.Pages.Count);
        Assert.Equal("A\n\nA", string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal(new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10), layout.Pages[0].TextPlacements[0]);
        Assert.Equal(new EpubTextPlacement(0, 3, 1, 2, secondTop, 6, 10), layout.Pages[^1].TextPlacements[^1]);
        if (pageCount == 2)
        {
            Assert.Equal(new[] { "A\n\n", "A" }, layout.Pages.Select(page => page.Text));
            Assert.Equal(new[] { 0, 3 }, layout.Pages.Select(page => page.TextStart));
            Assert.All(layout.Pages, page => Assert.Single(page.TextPlacements));
        }
        else
        {
            Assert.Equal(0, layout.Pages[0].TextStart);
            Assert.Equal(2, layout.Pages[0].TextPlacements.Count);
        }
    }

    [Fact]
    public void EmptyStructuralTextKeepsOneEmptyPageAndCancellationPrecedesLayout()
    {
        var book = EpubDocument.Open(Book("<p></p>"));
        Assert.Equal("", book.ReadText(0));
        var layout = book.CreateLayout(Options());
        var page = Assert.Single(layout.Pages);
        Assert.Equal("", page.Text);
        Assert.Equal(0, page.TextStart);
        Assert.Empty(page.TextPlacements);
        var frame = layout.RenderPage(0);
        Assert.All(frame.Pixels.ToArray(), channel => Assert.Equal(1f, channel));
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(Options(), canceled));
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(null!, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(-1, canceled));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.CreateLayout(Options(13)));
    }

    [Fact]
    public void ReadSnapshotExposesNaturalGeometryAndRenderingForEveryPage()
    {
        var snapshot = PdfReadSnapshot.OpenEpub(Book("<p>A A A</p>"), Options());
        var identity = snapshot.LayoutIdentity;
        string source = snapshot.SourceSha256;
        Assert.Equal(3, snapshot.PageCount);
        for (int index = 0; index < 3; index++)
        {
            var geometry = snapshot.GetPageGeometry(index);
            Assert.Equal(16, geometry.Width);
            Assert.Equal(23, geometry.Height);
            Assert.Equal(96, geometry.DpiX);
            Assert.Equal(96, geometry.DpiY);
            Assert.Equal(PdfReadPageUnit.CssPixel, geometry.Unit);
            Assert.Equal(0, geometry.IntrinsicRotation);
            var frame = snapshot.RenderPage(index);
            Assert.Equal(16, frame.Width);
            Assert.Equal(23, frame.Height);
            Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(frame.Pixels[(5 * 16 + 7) * 4 + channel], 0.21413370f, 0.21413373f);
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((5 * 16 + 14) * 4, 4).ToArray());
            for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
                Assert.Equal(1f, frame.Pixels[alpha]);
        }
        Assert.Equal(identity, snapshot.LayoutIdentity);
        Assert.Equal(source, snapshot.SourceSha256);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(3));
    }

    private static EpubLayoutOptions Options(double height = 23) => new(16, height,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001)
    };

    private static byte[] Book(string body = "<p>A A</p>")
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            Add("EPUB/book.opf", """
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:pagination</dc:identifier><dc:title>Pagination</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest><item id="chapter" href="text.xhtml" media-type="application/xhtml+xml"/><item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine><itemref idref="chapter"/></spine>
                </package>
                """);
            Add("EPUB/text.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Pagination</title></head><body>{body}</body></html>
                """);
            Add("EPUB/nav.xhtml", """
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="text.xhtml">Pagination</a></li></ol></nav></body></html>
                """);
            void Add(string path, string content, CompressionLevel compression = CompressionLevel.Optimal)
            {
                using var stream = zip.CreateEntry(path, compression).Open();
                stream.Write(Encoding.UTF8.GetBytes(content));
            }
        }
        return output.ToArray();
    }
}
