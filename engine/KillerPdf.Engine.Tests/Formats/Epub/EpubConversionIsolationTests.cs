using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubConversionIsolationTests
{
    private const string Unscripted = "<p>B</p><script>x</script>";

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
