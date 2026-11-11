using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

public sealed class EpubTableWorkLimitTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(24)]
    public void NestedMultipageTablesStopAtAChapterWorkBudgetAndPreserveTheNextChapter(int nesting)
    {
        string body = "<p style='margin:0'>" + string.Join("<br/>", Enumerable.Repeat("A", 40)) + "</p>";
        for (int depth = 0; depth < nesting; depth++)
            body = "<table style='width:80px'><tr><td style='width:80px'>" + body + "</td></tr></table>";
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(body), Chapter("<p>A</p>") } }.Build());

        var layout = book.CreateLayout(Options(120, 40) with { TableFlowWorkLimit = 32 });

        var diagnostic = Assert.Single(layout.ChapterDiagnostics);
        Assert.Equal(0, diagnostic.SpineIndex);
        Assert.Equal(EpubOmissionReason.LimitExceeded, diagnostic.Reason);
        Assert.Contains("32-step", diagnostic.Message);
        Assert.Equal("A", layout.Pages[^1].Text.Trim());
        Assert.Equal(1, layout.Pages[^1].SpineIndex);
        var diagnostics = new List<EpubChapterDiagnostic>();
        var reader = new PdfPageContentReader(PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        {
            PageWidthPoints = 90, PageHeightPoints = 30, FontSizePoints = 7.5, MarginPoints = 1.5,
            IsolateChapterFailures = true, TableFlowWorkLimit = 32, DiagnosticReported = diagnostics.Add
        })));
        Assert.Equal(2, reader.PageCount);
        Assert.Equal("A", string.Concat(reader.Read(1).Letters.Select(letter => letter.Value)));
        Assert.Equal(EpubOmissionReason.LimitExceeded, Assert.Single(diagnostics).Reason);
    }

    [Fact]
    public void MeasuringATallCellStopsAfterItsFirstPageWithoutDiscardingItsLaterText()
    {
        string body = "<p style='margin:0'>" + string.Join("<br/>", Enumerable.Repeat("A", 40)) + "</p>";
        for (int depth = 0; depth < 8; depth++)
            body = "<table style='width:80px'><tr><td style='width:80px'>" + body + "</td></tr></table>";
        var book = EpubDocument.Open(new Book { Chapters = { Chapter(body) } }.Build());

        var layout = book.CreateLayout(Options(120, 40) with { TableFlowWorkLimit = 6000 });

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(40, layout.Pages.Sum(page => page.TextPlacements.Count));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(16_000_001)]
    public void InvalidTableBudgetsAreRejectedBeforeChapterIsolation(int budget)
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>") } }.Build());
        Assert.Throws<ArgumentOutOfRangeException>(() => book.CreateLayout(Options() with { TableFlowWorkLimit = budget }));
        Assert.Throws<ArgumentOutOfRangeException>(() => book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)
        { IsolateChapterFailures = true, TableFlowWorkLimit = budget }));
    }
}
