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

public sealed class EpubPreformattedLayoutTests
{
    [Fact]
    public void DefaultPreformattedMarginsFollowItsComputedFontSize()
    {
        byte[] source = Book("<pre style='font-size:20px'>A</pre><p style='margin:0'>A</p>"),
            original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 100, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(new double[] { 22, 62 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal(new double[] { 20, 10 }, page.TextPlacements.Select(item => item.Height));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((10 * 80 + 12) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((25 * 80 + 12) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 75, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => font }));
        Assert.Equal(new double[] { 46.5, 22.5 }, new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.StartBaseLine.Y));
        Assert.Equal(original, source);
    }

    // CSS Text 3 tab-size: 8, measured in the pre font's space advance (6px at 10px here), so stops fall every 48px
    // from the line start; a tab that starts on a stop moves to the following one.
    [Theory]
    [InlineData("<pre style='margin:0'>A\tA</pre>", 1, 8, 42, 50)]
    [InlineData("<pre style='margin:0'>\tA</pre>", 0, 2, 48, 50)]
    [InlineData("<pre style='margin:0'>AAAAAAAA\tA</pre>", 8, 50, 48, 98)]
    // CSS Text 3: a tab advancing less than 0.5ch goes on to the next stop. The font has no "0", so 1ch is 0.5em (5px):
    // after 47.4px (seven A of 6px and one of 5.4px at 9px) the stop at 48 is 0.6px away, so the tab ends at 96.
    [InlineData("<pre style='margin:0'>AAAAAAA<span style='font-size:9px'>A</span>\tA</pre>", 8, 49.4, 48.6, 98)]
    public void PreformattedTabsAdvanceToTheNextEightSpaceStop(string body, int tab, double tabLeft, double tabWidth, double nextLeft)
    {
        byte[] source = Book(body), original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(120, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font, IsolateChapterFailures = true });
        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Equal('\t', page.Text[tab]);
        var placed = page.TextPlacements.Single(item => item.Start == tab);
        Assert.Equal(tabLeft, placed.Left, 6);
        Assert.Equal(tabWidth, placed.Width, 6);
        Assert.Equal(nextLeft, page.TextPlacements.Single(item => item.Start == tab + 1).Left, 6);
        // In the PDF the letter after the tab keeps its place: 0.75 points per CSS pixel.
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 90, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => font }));
        Assert.Equal(nextLeft * 0.75, new PdfPageContentReader(pdf).Read(0).Letters.Last().StartBaseLine.X, 6);
        Assert.Equal(original, source);
    }

    // A preformatted line wider than the page breaks before the A that no longer fits (the 56px line holds nine); the tab
    // on the wrapped line then measures its stops from that line's start: after AA (2 to 14) it advances to 2 + 48.
    [Fact]
    public void APreformattedTabAfterAnEmergencyWrapMeasuresFromTheWrappedLine()
    {
        byte[] source = Book("<pre style='margin:0'>AAAAAAAAAAA\tA</pre>");
        var font = FontWithMappedSpace();
        var layout = EpubDocument.Open(source).CreateLayout(new EpubLayoutOptions(60, 60, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font, IsolateChapterFailures = true });

        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Equal(12, page.TextPlacements.Single(item => item.Start == 9).Top);
        var tab = page.TextPlacements.Single(item => item.Start == 11);
        Assert.Equal((14d, 36d), (tab.Left, tab.Width));
        Assert.Equal(50, page.TextPlacements.Single(item => item.Start == 12).Left, 6);
    }

    // The parser already folds literal line breaks (XML 1.0 section 2.11), but a CR written as &#13; survives it. Like the
    // XML end-of-line rule, a CR LF pair and a lone CR then end the line as a single line feed (CSS Text 3 would show the
    // lone CR as a space instead; KP-B4 follows the XML rule).
    [Theory]
    [InlineData("<pre style='margin:0'>A&#13;A</pre>")]
    [InlineData("<pre style='margin:0'>A&#13;&#10;A</pre>")]
    [InlineData("<pre style='margin:0'>A&#13;\nA</pre>")]
    public void PreformattedCarriageReturnsEndTheLineOnce(string body)
    {
        byte[] source = Book(body), original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        Assert.Equal("A\nA", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font, IsolateChapterFailures = true });
        Assert.Empty(layout.ChapterDiagnostics);
        var page = Assert.Single(layout.Pages);
        Assert.Equal("A\nA", page.Text);
        Assert.Equal(new double[] { 2, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Equal(new double[] { 2, 2 }, page.TextPlacements.Select(item => item.Left));
        Assert.NotEmpty(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5, FontFamilyResolver = _ => font }));
        Assert.Equal(original, source);
    }

    // Decision 0009: a preformatted line wider than the content box breaks where it overflows,
    // keeping every character and its preserved spaces instead of refusing the chapter.
    [Theory]
    [InlineData("<pre>AAAAAAAAAAAAA</pre>", 13)]
    [InlineData("<pre>AAAAAA AAAAAA</pre>", 13)]
    [InlineData("<pre style='margin:0;text-indent:60px'>AAA</pre>", 3)]
    public void PreformattedLinesWiderThanTheContentBoxBreakInsideIt(string body, int placed)
    {
        var font = FontWithMappedSpace();
        var layout = EpubDocument.Open(Book(body)).CreateLayout(new EpubLayoutOptions(80, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        var placements = layout.Pages.SelectMany(page => page.TextPlacements).ToArray();
        Assert.Equal(placed, placements.Length);
        Assert.All(placements, placement => Assert.True(placement.Left + placement.Width <= 78));
        Assert.True(placements.Select(placement => placement.Top).Distinct().Count() > 1);
    }

    // Display and whitespace overrides that would collapse preformatted text are dropped, so the
    // text keeps preserved whitespace in block flow instead of refusing the chapter.
    [Theory]
    [InlineData("<pre style='display:inline'>A</pre>")]
    [InlineData("<pre style='all:initial;display:block'>A</pre>")]
    [InlineData("<pre style='all:inherit'>A</pre>")]
    [InlineData("<pre><span style='all:initial'>A</span></pre>")]
    [InlineData("<pre><span style='display:block'>A</span></pre>")]
    [InlineData("<pre style='white-space:normal'>A</pre>")]
    public void PreformattedOverridesKeepWhitespacePreserved(string body)
    {
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(Book(body));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 40, font) { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        Assert.Equal("A", string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Single(layout.Pages.SelectMany(page => page.TextPlacements));
        Assert.NotEmpty(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5, FontFamilyResolver = _ => font }));
    }

    [Fact]
    public void WhitespaceOnlyPreformattedBlockHonorsThePrecedingPageBreak()
    {
        byte[] source = Book("<p style='margin:0;page-break-after:always'>A</p>"
            + "<pre style='margin:0'>   </pre><p style='margin:0'>A</p>"), original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        Assert.Equal(new[] { "A\n\n", "   \n\nA" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 3 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] { 3, 4, 5, 8 }, layout.Pages[1].TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 2, 2, 12 }, layout.Pages[1].TextPlacements.Select(item => item.Top));
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => font }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(new[] { " ", " ", " ", "A" }, reader.Read(1).Letters.Select(letter => letter.Value));
        Assert.Equal(original, source);
    }

    [Fact]
    public void InlineStylesKeepPreservedSpacesAndBreaksAcrossOwnerBoundaries()
    {
        byte[] source = Book("<pre style='margin:0'> A<span style='color:red'> \n  A</span> A </pre>"),
            original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        var page = Assert.Single(layout.Pages);
        Assert.Equal(" A \n  A A ", book.ReadText(0));
        Assert.Equal(" A \n  A A ", page.Text);
        Assert.Equal(new[] { 0, 1, 2, 4, 5, 6, 7, 8, 9 }, page.TextPlacements.Select(item => item.Start));
        Assert.Equal(new double[] { 2, 8, 14, 2, 8, 14, 20, 26, 32 }, page.TextPlacements.Select(item => item.Left));
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 1, 0, 0, 1 }, native.Pixels.Slice((15 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((15 * 80 + 31) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => font }));
        Assert.Equal(new[] { " ", "A", " ", " ", " ", "A", " ", "A", " " },
            new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(80, 40));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((15 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 80 + 31) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void PreservedBlankLinesAndTerminalSpacesKeepContiguousPageSlices()
    {
        byte[] source = Book("<pre style='margin:0'>\n A\n\n  A  </pre>"), original = source.ToArray();
        var font = FontWithMappedSpace();
        var book = EpubDocument.Open(source);
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 24, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = _ => font });
        Assert.Equal(new[] { "\n A\n", "\n  A  " }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 4 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal("\n A\n\n  A  ", string.Concat(layout.Pages.Select(page => page.Text)));
        Assert.Equal("\n A\n\n  A  ", book.ReadText(0));
        Assert.Equal(new[] { 1, 2 }, layout.Pages[0].TextPlacements.Select(item => item.Start));
        Assert.Equal(new[] { 5, 6, 7, 8, 9 }, layout.Pages[1].TextPlacements.Select(item => item.Start));
        Assert.All(layout.Pages.SelectMany(page => page.TextPlacements), item => Assert.Equal(12, item.Top));
        var first = layout.RenderPage(0);
        var second = layout.RenderPage(1);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, first.Pixels.Slice((15 * 80 + 13) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, second.Pixels.Slice((15 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, second.Pixels.Slice((5 * 80 + 19) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 18, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = _ => font }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal(new[] { " ", "A" }, reader.Read(0).Letters.Select(letter => letter.Value));
        Assert.Equal(new[] { " ", " ", "A", " ", " " }, reader.Read(1).Letters.Select(letter => letter.Value));
        var rendered = new PdfPageRenderer(pdf).Render(1, new PdfRenderOptions(80, 24));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    [Fact]
    public void PreformattedLinesKeepMappedSpacesOffsetsAndMonospaceInBothOutputs()
    {
        byte[] source = Book("<pre style='margin:0'>  A\n    A</pre>"), original = source.ToArray();
        var font = FontWithMappedSpace();
        var requests = new List<EpubFontFamily>();
        TrueTypeFont? Resolve(EpubFontFamily family)
        {
            requests.Add(family);
            return family.IsGeneric && family.Name == "monospace" ? font : null;
        }
        var book = EpubDocument.Open(source);
        Assert.Equal("  A\n    A", book.ReadText(0));
        var layout = book.CreateLayout(new EpubLayoutOptions(80, 40, font)
        { FontSize = 10, Margin = 2, FontFamilyResolver = Resolve });
        var page = Assert.Single(layout.Pages);
        Assert.Equal("  A\n    A", page.Text);
        Assert.Equal(new[] { 0, 1, 2, 4, 5, 6, 7, 8 }, page.TextPlacements.Select(item => item.Start));
        Assert.All(page.TextPlacements, item => Assert.Equal(1, item.Length));
        Assert.Equal(new double[] { 2, 8, 14, 2, 8, 14, 20, 26 }, page.TextPlacements.Select(item => item.Left));
        Assert.Equal(new double[] { 2, 2, 2, 12, 12, 12, 12, 12 }, page.TextPlacements.Select(item => item.Top));
        Assert.Contains(requests, family => family.IsGeneric && family.Name == "monospace");
        var native = layout.RenderPage(0);
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((5 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new float[] { 0, 0, 0, 1 }, native.Pixels.Slice((15 * 80 + 31) * 4, 4).ToArray());
        Assert.Equal(new float[] { 1, 1, 1, 1 }, native.Pixels.Slice((5 * 80 + 5) * 4, 4).ToArray());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 60, PageHeightPoints = 30, MarginPoints = 1.5, FontSizePoints = 7.5,
            FontFamilyResolver = Resolve }));
        var letters = new PdfPageContentReader(pdf).Read(0).Letters;
        Assert.Equal(new[] { " ", " ", "A", " ", " ", " ", " ", "A" }, letters.Select(letter => letter.Value));
        Assert.Equal(new double[] { 1.5, 6, 10.5, 1.5, 6, 10.5, 15, 19.5 }, letters.Select(letter => letter.StartBaseLine.X));
        Assert.Equal(new double[] { 22.5, 22.5, 22.5, 15, 15, 15, 15, 15 }, letters.Select(letter => letter.StartBaseLine.Y));
        var rendered = new PdfPageRenderer(pdf).Render(0, new PdfRenderOptions(80, 40));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((5 * 80 + 19) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels.Slice((15 * 80 + 31) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, rendered.Pixels.Slice((5 * 80 + 5) * 4, 4).ToArray());
        Assert.Equal(original, source);
    }

    private static TrueTypeFont FontWithMappedSpace()
    {
        byte[] cmap = new byte[52];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(offset), value);
        U16(2, 1); U16(4, 3); U16(6, 10); U32(8, 12);
        U16(12, 12); U32(16, 40); U32(24, 2);
        U32(28, 32); U32(32, 32); U32(36, 2);
        U32(40, 65); U32(44, 65); U32(48, 1);
        byte[] bytes = TrueTypeFontTests.BuildTestFont(true, includeOutlines: true, includeCompound: true, cmap: cmap);
        int count = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4));
        for (int record = 12; record < 12 + count * 16; record += 16)
        {
            if (!bytes.AsSpan(record, 4).SequenceEqual("loca"u8)) continue;
            int offset = (int)BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(record + 8));
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 4), 20);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 8), 44);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset + 12), 44);
        }
        return TrueTypeFont.Load(bytes);
    }

    private static byte[] Book(string body)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip");
            Add("META-INF/container.xml", "<container xmlns='urn:oasis:names:tc:opendocument:xmlns:container' version='1.0'><rootfiles><rootfile full-path='package.opf' media-type='application/oebps-package+xml'/></rootfiles></container>");
            Add("package.opf", "<package xmlns='http://www.idpf.org/2007/opf' version='3.0' unique-identifier='id'><metadata xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:identifier id='id'>pre-layout</dc:identifier><dc:title>Preformatted layout</dc:title><dc:language>en</dc:language></metadata><manifest><item id='doc' href='doc.xhtml' media-type='application/xhtml+xml'/></manifest><spine><itemref idref='doc'/></spine></package>");
            Add("doc.xhtml", "<html xmlns='http://www.w3.org/1999/xhtml'><head/><body>" + body + "</body></html>");
            void Add(string name, string value)
            {
                using var stream = zip.CreateEntry(name, CompressionLevel.NoCompression).Open();
                stream.Write(Encoding.UTF8.GetBytes(value));
            }
        }
        return output.ToArray();
    }
}
