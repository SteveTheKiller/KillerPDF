using System.Buffers.Binary;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubConversionIsolationTests
{
    private const string Unscripted = "<p>B</p><script>x</script>";

    [Fact]
    public void UnexpectedChapterFailurePreservesTheNextChapterInLayoutAndConversion()
    {
        var book = EpubDocument.Open(new Book { Chapters =
        { Chapter("<p style='font-family:broken'>A</p>"), Chapter("<p>ALPHA</p>") } }.Build());
        TrueTypeFont? Resolve(EpubFontFamily family) => family.Name == "broken"
            ? throw new InvalidOperationException("The host font lookup failed.") : null;
        var layout = book.CreateLayout(Options() with { FontFamilyResolver = Resolve });
        Assert.Equal(2, layout.Pages.Count);
        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(0, diagnostic.SpineIndex);
        Assert.Equal(EpubOmissionReason.Other, diagnostic.Reason);
        Assert.Equal("The host font lookup failed.", diagnostic.Message);
        Assert.Equal("ALPHA", layout.Pages[1].Text.Trim());
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Liberation())
        { IsolateChapterFailures = true, FontFamilyResolver = Resolve }));
        var reader = new PdfPageContentReader(pdf);
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("ALPHA", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChapterIsolationNeverSwallowsCancellationOrOutOfMemory(bool outOfMemory)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p style='font-family:broken'>A</p>") } }.Build());
        Exception failure = outOfMemory ? new OutOfMemoryException("No memory.") : new OperationCanceledException();
        TrueTypeFont? Resolve(EpubFontFamily _) => throw failure;
        Assert.Same(failure, Record.Exception(() => book.CreateLayout(Options() with { FontFamilyResolver = Resolve })));
        Assert.Same(failure, Record.Exception(() => book.ConvertToPdf(new EpubPdfConversionOptions(Liberation())
        { IsolateChapterFailures = true, FontFamilyResolver = Resolve })));
    }

    [Fact]
    public void IsolatedConversionReplacesARefusedChapterWithItsLabeledUnavailablePage()
    {
        var font = Liberation();
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>ALPHA</p>"), Chapter(Unscripted) } }.Build());

        byte[] converted = book.ConvertToPdf(new EpubPdfConversionOptions(font)
        {
            IsolateChapterFailures = true, UnavailableChapterLabel = "Kapitel fehlt"
        });

        var reader = new PdfPageContentReader(PdfDocument.Open(converted));
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("ALPHA", string.Concat(reader.Read(0).Letters.Select(letter => letter.Value)));
        // The diagnosis page carries the caller's label, the chapter path and the refusal, one line each.
        Assert.Equal("Kapitel fehlt" + "OEBPS/text/c1.xhtml" + "Scripted EPUB XHTML text is unsupported.",
            string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
    }

    [Fact]
    public void ANoticePageWithANativeScriptChapterNameStillConverts()
    {
        // The refused chapter's file name holds U+7B2C, which the fallback font does not map.
        var book = new Book { ExtraManifest = "<item id=\"u\" href=\"Text/%E7%AC%AC.xhtml\" media-type=\"application/xhtml+xml\"/>",
            ExtraSpine = "<itemref idref=\"u\"/>" };
        book.Resources.Add(("Text/第.xhtml", System.Text.Encoding.UTF8.GetBytes(Chapter(Unscripted))));
        var opened = EpubDocument.Open(book.Build());

        var pdf = PdfDocument.Open(opened.ConvertToPdf(new EpubPdfConversionOptions(Liberation())
        { IsolateChapterFailures = true, UnavailableChapterLabel = "Chapter unavailable" }));

        Assert.Equal(1, new PdfPageContentReader(pdf).PageCount);
    }

    [Fact]
    public void ANoticePageWithABengaliLabelStillConverts()
    {
        var book = new Book { Chapters = { Chapter(Unscripted) } };

        var pdf = PdfDocument.Open(EpubDocument.Open(book.Build()).ConvertToPdf(new EpubPdfConversionOptions(Liberation())
        { IsolateChapterFailures = true, UnavailableChapterLabel = "এই অধ্যায়টি দেখানো যায়নি।" }));

        var letters = string.Concat(new PdfPageContentReader(pdf).Read(0).Letters.Select(letter => letter.Value));
        Assert.Contains("OEBPS/text/c0.xhtml", letters);
    }

    // PDF conversion sets each run's font size from its placement height, and body text records the font size as that
    // height, so a notice run does the same; the line pitch (1.2 times the size) only spaces the lines.
    [Fact]
    public void ANoticeRunIsRecordedAtTheFontSizeNotTheLineHeight()
    {
        var book = new Book { Chapters = { Chapter(Unscripted) } };
        var options = new EpubLayoutOptions(400, 600, Liberation()) { FontSize = 16, Margin = 2, IsolateChapterFailures = true,
            UnavailableChapterLabel = "Chapter unavailable" };

        var page = Assert.Single(EpubDocument.Open(book.Build()).CreateLayout(options).Pages);

        Assert.NotEmpty(page.TextPlacements);
        Assert.All(page.TextPlacements.Where(run => run.Length > 0), run => Assert.Equal(16, run.Height));
        // Lines still advance by 1.2 times the size.
        double[] tops = [.. page.TextPlacements.Select(run => run.Top).Distinct()];
        Assert.Equal(16 * 1.2, tops[1] - tops[0], 6);
    }

    // A host that cannot write a notice page gets a plain page with the same text, drawn in the default font, so the PDF
    // page is not blank; if it cannot write that either, the page stays blank with a diagnostic and the book still converts.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AnUnwritableNoticePageBecomesAPlainPageThenABlankOneWithoutEndingTheConversion(bool alwaysFails)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(Unscripted), Chapter("<p>B</p>") } }.Build());
        var options = new EpubLayoutOptions(400, 600, Liberation()) { FontSize = 16, Margin = 2, IsolateChapterFailures = true,
            UnavailableChapterLabel = "Chapter unavailable" };
        var received = new List<EpubLayoutPage>();
        int notices = 0;
        void Resident(EpubLayoutPage page, int index)
        {
            received.Add(page);
            if (!page.Text.Contains("OEBPS/text/c0.xhtml")) return;
            // The first attempt always fails; a second failure is asked for only when the page has something to draw.
            if (++notices == 1 || (alwaysFails && page.TextPlacements.Count > 0))
                throw new InvalidOperationException("The host cannot write this page.");
        }

        var layout = book.CreateLayout(options, Resident, CancellationToken.None);

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal("B", received[^1].Text.Trim());
        var written = received.Last(page => page.Text.Contains("OEBPS/text/c0.xhtml"));
        Assert.Equal("Chapter unavailable" + "\n" + "OEBPS/text/c0.xhtml" + "\n" + "Scripted EPUB XHTML text is unsupported.", written.Text);
        if (alwaysFails)
        {
            Assert.Empty(written.TextPlacements);
            Assert.Contains(layout.ContentDiagnostics, item => item.SpineIndex == 0 && item.Message.Contains("could not be written"));
        }
        else
        {
            Assert.NotEmpty(written.TextPlacements);
            Assert.NotEmpty(written.Glyphs);
        }
    }

    [Fact]
    public void ANoticeLabelThatNoFontCanDrawShowsAMissingGlyphBoxPerCharacter()
    {
        var book = new Book { Chapters = { Chapter(Unscripted) } };
        var options = new EpubLayoutOptions(400, 600, Liberation()) { FontSize = 10, Margin = 2, IsolateChapterFailures = true,
            UnavailableChapterLabel = char.ConvertFromUtf32(0x65E5) };

        var page = Assert.Single(EpubDocument.Open(book.Build()).CreateLayout(options).Pages);

        var cluster = page.Clusters[0];
        Assert.True(cluster.Missing);
        Assert.Equal<ushort>(0, cluster.Glyphs[0].GlyphId);
        Assert.Contains(page.Glyphs, paint => paint is NativeGlyphPaint { SourceOffset: 0 });
    }

    [Fact]
    public void ANoticeLabelDrawsThroughTheHostFaceThatHasItsScript()
    {
        // U+65E5 is in no default-font cmap; the host's face maps it, so the heading must use that face.
        var host = HostFaceMapping(0x65E5);
        var book = new Book { Chapters = { Chapter(Unscripted) } };
        string label = "A" + char.ConvertFromUtf32(0x65E5) + "B";
        Func<EpubFontFaceRequest, EpubFontFaceResult?> resolver = _ => new EpubFontFaceResult(host, 400, EpubFontMatch.Exact);
        var options = new EpubLayoutOptions(400, 600, Liberation()) { FontSize = 10, Margin = 2, IsolateChapterFailures = true,
            UnavailableChapterLabel = label, FontFaceResolver = resolver };

        var opened = EpubDocument.Open(book.Build());
        var page = Assert.Single(opened.CreateLayout(options).Pages);

        var cluster = page.Clusters.Values.Single(item => item.Start == 1);
        Assert.False(cluster.Missing);
        Assert.Same(host, cluster.Font);
        Assert.Equal<ushort>(2, cluster.Glyphs[0].GlyphId);
        // The conversion writes the whole page without refusing the label.
        var pdf = opened.ConvertToPdf(new EpubPdfConversionOptions(Liberation())
        { IsolateChapterFailures = true, UnavailableChapterLabel = label, FontFaceResolver = resolver });
        Assert.Equal(1, new PdfPageContentReader(PdfDocument.Open(pdf)).PageCount);
    }

    [Fact]
    public void ANoticePageTheHostCannotWriteBecomesAPlainPageInsteadOfEndingTheBook()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(Unscripted), Chapter("<p>ALPHA</p>") } }.Build());
        var written = new List<(int Index, bool HadClusters)>();

        var layout = book.CreateLayout(Options(), (page, index) =>
        {
            written.Add((index, page.Clusters.Count > 0));
            if (index == 0 && page.Clusters.Count > 0) throw new ArgumentException("The font has no glyph for this notice.");
        }, CancellationToken.None);

        Assert.Equal(2, layout.Pages.Count);
        Assert.Equal(new[] { (0, true), (0, false), (1, true) }, written);
        Assert.Contains("OEBPS/text/c0.xhtml", layout.Pages[0].Text);
    }

    // Glyph 2 is the A outline; the cmap maps the one character to it.
    private static TrueTypeFont HostFaceMapping(int scalar)
    {
        byte[] cmap = new byte[40];
        void U16(int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(cmap.AsSpan(offset), value);
        void U32(int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(cmap.AsSpan(offset), value);
        U16(2, 1); U16(4, 3); U16(6, 10); U32(8, 12);
        U16(12, 12); U32(16, 28); U32(24, 1);
        U32(28, (uint)scalar); U32(32, (uint)scalar); U32(36, 2);
        return TrueTypeFont.Load(TrueTypeFontTests.BuildTestFont(true, includeOutlines: true, includeCompound: true, cmap: cmap));
    }

    [Fact]
    public void ConversionWithoutIsolationStillRefusesTheBook()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>ALPHA</p>"), Chapter(Unscripted) } }.Build());

        var error = Assert.Throws<NotSupportedException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Liberation())));

        Assert.Equal("Scripted EPUB XHTML text is unsupported.", error.Message);
    }

    [Fact]
    public void LayoutReportsIgnoredDeclarationsPerLaidOutChapterAndWithdrawsThemWithAnIsolatedChapter()
    {
        // One unknown property and one rule dropped for its pseudo-element selector: two per linked chapter.
        const string css = "p { foo-bar: 1; font-size: 12px } q::x { color: red }";
        const string unlinked = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>C</title></head><body><p>A</p></body></html>";
        var book = new Book
        {
            Css = css,
            Chapters = { Chapter("<p style='bogus-name: 1'>A</p>"), unlinked, Chapter(Unscripted), Chapter("<p>A</p>") }
        };

        var layout = EpubDocument.Open(book.Build()).CreateLayout(Options());

        Assert.Equal(2, Assert.Single(layout.ChapterDiagnostics).SpineIndex);
        Assert.Equal(new[]
        {
            new EpubIgnoredDeclarations(0, "OEBPS/text/c0.xhtml", 3),
            new EpubIgnoredDeclarations(3, "OEBPS/text/c3.xhtml", 2)
        }, layout.IgnoredDeclarations);
    }

    [Fact]
    public void LayoutReportsProgressAfterEverySpineItemIncludingAnIsolatedOne()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>"), Chapter(Unscripted), Chapter("<p>A</p>") } }.Build());
        var calls = new List<(int Completed, int Total)>();

        var layout = book.CreateLayout(Options() with { ChapterProgress = (completed, total) => calls.Add((completed, total)) });

        Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(new[] { (1, 3), (2, 3), (3, 3) }, calls);
    }

    [Fact]
    public void CancellingFromProgressStopsLayoutBeforeTheNextSpineItem()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>"), Chapter("<p>A</p>"), Chapter("<p>A</p>") } }.Build());
        using var cancellation = new CancellationTokenSource();
        var calls = new List<int>();

        Assert.ThrowsAny<OperationCanceledException>(() => book.CreateLayout(Options() with
        {
            ChapterProgress = (completed, _) => { calls.Add(completed); cancellation.Cancel(); }
        }, cancellation.Token));

        Assert.Equal(new[] { 1 }, calls);
    }

    [Theory]
    [InlineData(800, 1000, 16, 16, true)]
    [InlineData(32768, 128, 0, 16, true)]
    // 128 MiB over two float RGBA planes is 4,194,304 pixels: 2048 by 2048 fits and one more row does not.
    [InlineData(2048, 2048, 0, 16, true)]
    [InlineData(2048, 2049, 0, 16, false)]
    [InlineData(0, 1000, 16, 16, false)]
    [InlineData(32769, 100, 0, 16, false)]
    [InlineData(800, double.NaN, 16, 16, false)]
    [InlineData(800, 1000, -1, 16, false)]
    [InlineData(32, 1000, 16, 16, false)]
    [InlineData(800, 48, 16, 16, true)]
    [InlineData(800, 48, 16, 16.5, false)]
    [InlineData(800, 1000, 16, 0, false)]
    [InlineData(800, 1000, 16, double.PositiveInfinity, false)]
    public void PageLimitsMatchWhatLayoutAccepts(double width, double height, double margin, double fontSize, bool fits)
    {
        Assert.Equal(fits, EpubLayoutOptions.FitsPageLimits(width, height, margin, fontSize));
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>") } }.Build());
        var options = new EpubLayoutOptions(width, height, Options().DefaultFont) { Margin = margin, FontSize = fontSize };
        if (fits) Assert.NotEmpty(book.CreateLayout(options).Pages);
        else Assert.ThrowsAny<Exception>(() => book.CreateLayout(options));
    }

    private static TrueTypeFont Liberation()
    {
        using Stream stream = typeof(TrueTypeFont).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return TrueTypeFont.Load(bytes.ToArray());
    }
}
