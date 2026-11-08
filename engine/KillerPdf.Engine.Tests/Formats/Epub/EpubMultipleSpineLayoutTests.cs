using System.Buffers.Binary;
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

public sealed class EpubMultipleSpineLayoutTests
{
    [Fact]
    public void OrdinaryChaptersRemainCompleteBeyondThePrototypeAggregateTextBudget()
    {
        string body = "<p>" + string.Join(" ", Enumerable.Repeat("A", 30000)) + "</p>";
        byte[] source = Book([body, body]), original = source.ToArray();
        var book = EpubDocument.Open(source);
        var font = FontWithMappedSpace();
        var layout = book.CreateLayout(Options() with { PageWidth = 32768, PageHeight = 14, DefaultFont = font });
        Assert.Equal(59999, layout.Pages.Where(page => page.SpineIndex == 0).Sum(page => page.Text.Length));
        Assert.Equal(59999, layout.Pages.Where(page => page.SpineIndex == 1).Sum(page => page.Text.Length));
        Assert.Equal(59998, layout.Pages.Last().TextPlacements.Last().Start);
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(font)
        { PageWidthPoints = 24576, PageHeightPoints = 10.5, MarginPoints = 1.5, FontSizePoints = 7.5 }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(layout.Pages.Count, reader.PageCount);
        Assert.Equal(60000, Enumerable.Range(0, reader.PageCount)
            .Sum(index => reader.Read(index).Letters.Count(letter => letter.Value == "A")));
        Assert.Equal(original, source);
    }

    [Fact]
    public void PaddingPageLimitIsSharedAcrossChaptersAndAcceptsTheExactBoundary()
    {
        const string body = "<p style='margin:0;padding-bottom:4999.5px'>A</p>";
        var font = TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true));
        var options = new EpubLayoutOptions(100, 21, font) { Margin = 10, FontSize = .5 };
        Assert.Equal(5000, EpubDocument.Open(Book([body])).CreateLayout(options).Pages.Count);
        var exact = EpubDocument.Open(Book([body, body])).CreateLayout(options);
        Assert.Equal(10000, exact.Pages.Count);
        Assert.Equal(5000, exact.Pages.Count(page => page.SpineIndex == 0));
        Assert.Equal(5000, exact.Pages.Count(page => page.SpineIndex == 1));
        byte[] source = Book([body, body, "<p></p>"]), preserved = source.ToArray();
        var error = Assert.Throws<InvalidDataException>(() => EpubDocument.Open(source).CreateLayout(options));
        Assert.Equal("Native EPUB layout exceeds its 10000-page limit.", error.Message);
        Assert.Equal(preserved, source);
    }

    [Fact]
    public void IsolatedLayoutReplacesOnlyTheRefusedChapterWithADiagnosisPage()
    {
        // Scripting is a lasting chapter refusal; sup used to refuse but now lays out (KP-B slice 5).
        byte[] source = Book(["<p>A A</p>", "<p onclick='go()'>AA</p>"]), original = source.ToArray();
        var book = EpubDocument.Open(source);
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options()));

        var layout = book.CreateLayout(Options() with
        {
            IsolateChapterFailures = true, UnavailableChapterLabel = "Chapter unavailable"
        });

        Assert.Equal(new[] { "A ", "A" }, layout.Pages.Where(page => page.SpineIndex == 0).Select(page => page.Text));
        var placeholder = Assert.Single(layout.Pages, page => page.SpineIndex == 1);
        Assert.StartsWith("Chapter unavailable", placeholder.Text);
        Assert.Contains("chapter1.xhtml", placeholder.Text);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(1, diagnostic.SpineIndex);
        Assert.EndsWith("chapter1.xhtml", diagnostic.Href);
        Assert.Equal("Scripted EPUB XHTML text is unsupported.", diagnostic.Message);
        Assert.Equal(16, layout.RenderPage(layout.Pages.ToList().IndexOf(placeholder)).Width);
        Assert.Equal(original, source);
    }

    [Fact]
    public void LinksIntoAnIsolatedChapterResolveToItsDiagnosisPage()
    {
        var book = EpubDocument.Open(Book(["<p>A A</p>", "<p id='note' onclick='go()'>AA</p>"]));
        var layout = book.CreateLayout(Options() with { IsolateChapterFailures = true });
        string href = Assert.Single(layout.ChapterDiagnostics).Href;
        int placeholder = layout.Pages.ToList().FindIndex(page => page.SpineIndex == 1);

        Assert.Equal(placeholder, layout.ResolveNavigationTarget(href + "#note").PageIndex);
        Assert.Equal(placeholder, layout.ResolveNavigationTarget(href + "#missing").PageIndex);
        Assert.Equal(placeholder, layout.ResolveNavigationTarget(href).PageIndex);
    }

    [Fact]
    public void ChaptersStartFreshPagesAndPreserveTheirOwnSourceCoordinates()
    {
        var book = EpubDocument.Open(Book());
        Assert.Equal("A A", book.ReadText(0));
        Assert.Equal("AA", book.ReadText(1));
        var layout = book.CreateLayout(Options());
        Assert.Equal(3, layout.Pages.Count);
        Assert.Equal(new[] { 0, 0, 1 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Equal(new[] { 0, 2, 0 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] { "A ", "A", "AA" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(0, 2, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
        Assert.Equal(new[] {
            new EpubTextPlacement(1, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(1, 1, 1, 8, 2, 6, 10) }, layout.Pages[2].TextPlacements);
        for (int index = 0; index < 3; index++)
        {
            var frame = layout.RenderPage(index);
            Assert.Equal(16, frame.Width);
            Assert.Equal(23, frame.Height);
            Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
            for (int channel = 0; channel < 3; channel++)
            {
                Assert.InRange(frame.Pixels[(5 * 16 + 7) * 4 + channel], 0.21413370f, 0.21413373f);
                if (index == 2)
                    Assert.InRange(frame.Pixels[(5 * 16 + 13) * 4 + channel], 0.21413370f, 0.21413373f);
            }
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((15 * 16 + 14) * 4, 4).ToArray());
            for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
                Assert.Equal(1f, frame.Pixels[alpha]);
        }
    }

    [Fact]
    public void DeclaredReadingOrderControlsChapterOrderIndependentlyOfManifestAndFilenames()
    {
        var book = EpubDocument.Open(Book(spineOrder: [1, 0]));
        Assert.Equal("AA", book.ReadText(0));
        Assert.Equal("A A", book.ReadText(1));
        var layout = book.CreateLayout(Options());
        Assert.Equal(new[] { "AA", "A ", "A" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 1, 1 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Equal(new[] { 0, 0, 2 }, layout.Pages.Select(page => page.TextStart));
        Assert.Equal(new[] {
            new EpubTextPlacement(0, 0, 1, 2, 2, 6, 10),
            new EpubTextPlacement(0, 1, 1, 8, 2, 6, 10) }, layout.Pages[0].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(1, 0, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(1, 2, 1, 2, 2, 6, 10) }, layout.Pages[2].TextPlacements);
    }

    [Fact]
    public void EmptyAndParagraphChaptersRetainEntryLocalPageOffsets()
    {
        var layout = EpubDocument.Open(Book(["<p></p>", "<p>A</p><p>A</p>", "<p></p>"]))
            .CreateLayout(Options());
        Assert.Equal(4, layout.Pages.Count);
        Assert.Equal(new[] { "", "A\n\n", "A", "" }, layout.Pages.Select(page => page.Text));
        Assert.Equal(new[] { 0, 1, 1, 2 }, layout.Pages.Select(page => page.SpineIndex));
        Assert.Equal(new[] { 0, 0, 3, 0 }, layout.Pages.Select(page => page.TextStart));
        Assert.Empty(layout.Pages[0].TextPlacements);
        Assert.Empty(layout.Pages[3].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(1, 0, 1, 2, 2, 6, 10) }, layout.Pages[1].TextPlacements);
        Assert.Equal(new[] { new EpubTextPlacement(1, 3, 1, 2, 2, 6, 10) }, layout.Pages[2].TextPlacements);
        Assert.All(layout.RenderPage(0).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
        Assert.All(layout.RenderPage(3).Pixels.ToArray(), channel => Assert.Equal(1f, channel));
    }

    [Fact]
    public void TextWorkLimitIsSharedAcrossIndividuallySmallerChapters()
    {
        string body = "<p>" + string.Join(" ", Enumerable.Repeat("A", 30000)) + "</p>";
        var options = Options() with { PageWidth = 32768, PageHeight = 14 };
        var individual = EpubDocument.Open(Book([body]));
        Assert.Equal(59999, individual.ReadText(0).Length);
        Assert.NotEmpty(individual.CreateLayout(options).Pages);
        var combined = EpubDocument.Open(Book(Enumerable.Repeat(body, 17).ToArray()));
        Assert.Equal(59999, combined.ReadText(0).Length);
        Assert.Equal(59999, combined.ReadText(1).Length);
        // 17 chapters of 59,999 characters exceeded the prototype 1,000,000 limit; under the 16,000,000 limit they lay out.
        Assert.Equal(17 * 59999, combined.CreateLayout(options).Pages.Sum(page => page.Text.Length));
    }

    [Fact]
    public void AggregateTextAtTheExactWorkLimitIsAccepted()
    {
        string body = "<p>" + string.Join(" ", Enumerable.Repeat("A", 25000)) + "A</p>";
        string[] chapters = Enumerable.Repeat(body, 20).ToArray();
        var book = EpubDocument.Open(Book(chapters));
        Assert.Equal(50000, book.ReadText(0).Length);
        Assert.Equal(50000, book.ReadText(1).Length);
        var layout = book.CreateLayout(Options() with { PageWidth = 32768, PageHeight = 14 });
        Assert.Equal(50000, layout.Pages.Where(page => page.SpineIndex == 0).Sum(page => page.Text.Length));
        Assert.Equal(50000, layout.Pages.Where(page => page.SpineIndex == 1).Sum(page => page.Text.Length));
        Assert.Equal(1_000_000, layout.Pages.Sum(page => page.Text.Length));
        Assert.Equal(19, layout.Pages.Last().SpineIndex);
        // The book-wide limit was raised from 1,000,000 to 16,000,000 characters (KP-B slice 10), so the
        // prototype boundary plus one more chapter now lays out; the new boundary is too large to lay out in a unit test.
        var overPrototype = EpubDocument.Open(Book([.. chapters, "<p>A</p>"]));
        Assert.Equal(1_000_001, overPrototype.CreateLayout(Options() with { PageWidth = 32768, PageHeight = 14 })
            .Pages.Sum(page => page.Text.Length));
    }

    [Theory]
    [InlineData("<p>AA</p>", " linear='no'", "")]
    [InlineData("<p>AA</p>", " properties='page-spread-left'", "")]
    [InlineData("<p>AA</p>", "", " properties='scripted'")]
    public void UnsupportedLaterEntriesDiagnoseInsteadOfReturningAPartialLayout(
        string body, string spineAttributes, string itemProperties)
    {
        var book = EpubDocument.Open(Book(["<p>A A</p>", body],
            lastSpineAttributes: spineAttributes, lastItemProperties: itemProperties));
        Assert.Throws<NotSupportedException>(() => book.CreateLayout(Options()));
    }

    [Fact]
    public void CancellationPrecedesOptionsAndPropagatesThroughNativeReading()
    {
        byte[] bytes = Book();
        var book = EpubDocument.Open(bytes);
        var canceled = new CancellationToken(true);
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(null!, canceled));
        Assert.Throws<OperationCanceledException>(() => book.CreateLayout(Options(), canceled));
        Assert.Throws<OperationCanceledException>(() => PdfReadSnapshot.OpenEpub(bytes, Options(), canceled));
        var layout = book.CreateLayout(Options());
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(2, canceled));
        Assert.Throws<OperationCanceledException>(() => layout.RenderPage(-1, null!, canceled));
    }

    [Fact]
    public void NativeSnapshotExposesAndRendersAllChapterPagesWithoutChangingIdentity()
    {
        var snapshot = PdfReadSnapshot.OpenEpub(Book(), Options());
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
            {
                Assert.InRange(frame.Pixels[(5 * 16 + 7) * 4 + channel], 0.21413370f, 0.21413373f);
                if (index == 2)
                    Assert.InRange(frame.Pixels[(5 * 16 + 13) * 4 + channel], 0.21413370f, 0.21413373f);
            }
            Assert.Equal(new float[] { 1, 1, 1, 1 }, frame.Pixels.Slice((15 * 16 + 14) * 4, 4).ToArray());
            for (int alpha = 3; alpha < frame.Pixels.Length; alpha += 4)
                Assert.Equal(1f, frame.Pixels[alpha]);
        }
        Assert.Equal(identity, snapshot.LayoutIdentity);
        Assert.Equal(source, snapshot.SourceSha256);
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetPageGeometry(3));
        Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.RenderPage(3));
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
        var font = TrueTypeFont.Load(bytes);
        Assert.Equal((ushort)2, font.GetGlyphId(' '));
        return font;
    }

    private static EpubLayoutOptions Options() => new(16, 23,
        TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(false, includeOutlines: true)))
    {
        FontSize = 10, Margin = 2, TextColor = new PdfRgbColor(0.5001, 0.5001, 0.5001)
    };

    private static byte[] Book(string[]? bodies = null, int[]? spineOrder = null,
        string lastSpineAttributes = "", string lastItemProperties = "")
    {
        bodies ??= ["<p>A A</p>", "<p>AA</p>"];
        spineOrder ??= Enumerable.Range(0, bodies.Length).ToArray();
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            Add("mimetype", "application/epub+zip", CompressionLevel.NoCompression);
            Add("META-INF/container.xml", """
                <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0"><rootfiles><rootfile full-path="EPUB/book.opf" media-type="application/oebps-package+xml"/></rootfiles></container>
                """);
            string manifest = string.Concat(Enumerable.Range(0, bodies.Length).Reverse().Select(index =>
                $"<item id='chapter{index}' href='chapter{bodies.Length - index}.xhtml' media-type='application/xhtml+xml'{(index == bodies.Length - 1 ? lastItemProperties : "")}/>"));
            string spine = string.Concat(spineOrder.Select((index, position) =>
                $"<itemref idref='chapter{index}'{(position == spineOrder.Length - 1 ? lastSpineAttributes : "")}/>"));
            Add("EPUB/book.opf", $"""
                <package xmlns="http://www.idpf.org/2007/opf" version="3.0" unique-identifier="id">
                  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/"><dc:identifier id="id">urn:test:multiple-spine</dc:identifier><dc:title>Multiple chapters</dc:title><dc:language>en</dc:language><meta property="dcterms:modified">2026-11-03T00:00:00Z</meta></metadata>
                  <manifest>{manifest}<item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="nav"/></manifest>
                  <spine>{spine}</spine>
                </package>
                """);
            for (int index = 0; index < bodies.Length; index++)
                Add($"EPUB/chapter{bodies.Length - index}.xhtml", $"""
                    <html xmlns="http://www.w3.org/1999/xhtml"><head><title>Chapter</title></head><body>{bodies[index]}</body></html>
                    """);
            Add("EPUB/nav.xhtml", $"""
                <html xmlns="http://www.w3.org/1999/xhtml" xmlns:epub="http://www.idpf.org/2007/ops"><head><title>Contents</title></head><body><nav epub:type="toc"><ol><li><a href="chapter{bodies.Length}.xhtml">Chapters</a></li></ol></nav></body></html>
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
