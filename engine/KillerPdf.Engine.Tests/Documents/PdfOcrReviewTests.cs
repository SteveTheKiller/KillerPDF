using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrReviewTests
{
    [Fact]
    public void OptionsPreserveLanguagePriorityPreprocessingAndOutputMode()
    {
        var options = new PdfOcrOptions(["en-US", "de-DE", "EN-us"],
            PdfOcrOutputMode.ExactImage, removeBackground: true, removeNoise: true);

        Assert.Equal(["en-US", "de-DE"], options.Languages);
        Assert.Equal(PdfOcrOutputMode.ExactImage, options.OutputMode);
        Assert.True(options.Deskew);
        Assert.True(options.CorrectOrientation);
        Assert.True(options.RemoveBackground);
        Assert.True(options.RemoveNoise);
        Assert.True(options.DetectPageSegments);
        Assert.Throws<ArgumentException>(() => new PdfOcrOptions([]));
        Assert.Throws<ArgumentException>(() => new PdfOcrOptions(["../../model"]));
    }

    [Fact]
    public void BatchRunnerSuppliesTheSameValidatedOptionsToEveryPage()
    {
        var options = new PdfOcrOptions(["en-US", "es"],
            PdfOcrOutputMode.Editable, deskew: false);
        PdfOcrBatchPage[] pages = [
            new("first.pdf", 0, new byte[] { 1 }),
            new("second.pdf", 1, new byte[] { 2 })];

        IReadOnlyList<PdfOcrBatchResult> results = PdfOcrBatchRunner.Run(
            pages, options, (page, supplied, _) =>
            {
                Assert.Same(options, supplied);
                return new PdfOcrReview([
                    Word(page.SourceName, page.PageIndex, 0, "ok", 1)]);
            });

        Assert.All(results, result => Assert.True(result.Succeeded));
    }

    [Fact]
    public void ReviewCorrectsIgnoresAndReplacesWithoutChangingOriginal()
    {
        var original = new PdfOcrReview([
            Word("a", 0, 0, "Kiler", 0.42), Word("b", 0, 1, "PDF", 0.99),
            Word("c", 1, 0, "Kiler", 0.61)]);

        PdfOcrReview reviewed = original.Correct("a", "Killer").Ignore("b")
            .ReplaceAll("Kiler", "Killer");

        Assert.All(reviewed.Words.Where(word => word.OriginalText == "Kiler"),
            word => Assert.Equal(("Killer", PdfOcrWordStatus.Corrected), (word.Text, word.Status)));
        Assert.Equal(PdfOcrWordStatus.Ignored, reviewed.Words[1].Status);
        Assert.Equal("Kiler", original.Words[0].Text);
        Assert.Equal("Killer PDF" + Environment.NewLine + "Killer", reviewed.ExportText());
    }

    [Fact]
    public void LowConfidenceListUsesInclusiveThresholdAndPendingState()
    {
        var review = new PdfOcrReview([
            Word("a", 0, 0, "one", 0.5), Word("b", 0, 1, "two", 0.5001)])
            .Ignore("a");

        Assert.Empty(review.GetLowConfidenceWords(0.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => review.GetLowConfidenceWords(1.1));
    }

    [Fact]
    public void ReportEscapesTextAndUsesStableInvariantConfidence()
    {
        var review = new PdfOcrReview([Word("a", 0, 0, "Hello, \"world\"", 0.125)]);

        string report = review.ExportReport();

        Assert.Contains("\"Hello, \"\"world\"\"\"", report);
        Assert.Contains(",0.125,", report);
    }

    [Fact]
    public void BatchRunnerPreservesSourcesAndIsolatesFailedPages()
    {
        PdfOcrBatchPage[] pages = [
            new("first.pdf", 0, new byte[] { 1 }), new("bad.pdf", 1, new byte[] { 2 }),
            new("third.pdf", 2, new byte[] { 3 })];

        IReadOnlyList<PdfOcrBatchResult> results = PdfOcrBatchRunner.Run(pages, (page, _) =>
        {
            byte[] providerBuffer = page.Source.ToArray();
            providerBuffer[0] = 9;
            if (page.Source.Span[0] == 2) throw new InvalidOperationException("Unreadable page");
            return new PdfOcrReview([Word(page.SourceName, page.PageIndex, 0, "ok", 1)]);
        });

        Assert.True(results[0].Succeeded);
        Assert.Equal("Unreadable page", results[1].Error);
        Assert.True(results[2].Succeeded);
        Assert.Equal(1, pages[0].Source.Span[0]);
    }

    [Fact]
    public void BatchRunnerRecordsCancellationAndStops()
    {
        using var cancellation = new CancellationTokenSource();
        PdfOcrBatchPage[] pages = [
            new("first.pdf", 0, new byte[] { 1 }), new("second.pdf", 0, new byte[] { 2 })];

        IReadOnlyList<PdfOcrBatchResult> results = PdfOcrBatchRunner.Run(pages, (_, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return new PdfOcrReview([]);
        }, cancellation.Token);

        Assert.Single(results);
        Assert.True(results[0].WasCanceled);
    }

    [Fact]
    public void JsonRoundTripPreservesCorrectionsGeometryAndReviewState()
    {
        var review = new PdfOcrReview([
            Word("a", 0, 0, "Kiler", 0.42), Word("b", 1, 0, "café", 0.91)])
            .Correct("a", "Killer").Ignore("b");

        string json = review.ExportJson();
        PdfOcrReview restored = PdfOcrReview.ImportJson(json);

        Assert.Equal(review.Words, restored.Words);
        Assert.Equal(PdfOcrWordStatus.Corrected, restored.Words[0].Status);
        Assert.Equal(PdfOcrWordStatus.Ignored, restored.Words[1].Status);
        Assert.Contains("\"Version\":1", json);
        Assert.Throws<NotSupportedException>(() => PdfOcrReview.ImportJson(
            json.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal)));
    }

    [Fact]
    public void AccuracyReportSummarizesConfidenceAndReviewWarningsByPage()
    {
        var review = new PdfOcrReview([
            Word("a", 0, 0, "Kiler", 0.4), Word("b", 0, 1, "PDF", 1),
            Word("c", 1, 0, "", 0.8)])
            .Correct("a", "Killer").Ignore("b");

        PdfOcrAccuracyReport report = review.CreateAccuracyReport(0.5);

        Assert.Equal(3, report.WordCount);
        Assert.Equal(2.2 / 3, report.AverageConfidence, 10);
        Assert.True(report.HasWarnings);
        Assert.Equal(1, report.Pages[0].LowConfidenceCount);
        Assert.Equal(1, report.Pages[0].CorrectedCount);
        Assert.Equal(1, report.Pages[0].IgnoredCount);
        Assert.Equal(1, report.Pages[1].PendingCount);
        Assert.Equal(1, report.Pages[1].EmptyTextCount);
    }

    [Fact]
    public void ReviewedTextCanBeWrittenAsASearchableInvisibleLayer()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        var review = new PdfOcrReview([
            new PdfOcrWord("a", 0, 0, "A", "A",
                new PdfContentBounds(20, 30, 50, 50), 0.9, "en-US")]);
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));

        PdfDocument searchable = PdfDocument.Open(review.WriteSearchableText(original, font));

        PdfPageContent extracted = new PdfPageContentReader(searchable).Read(0);
        Assert.Equal("A", extracted.Text);
        Assert.Equal(20, extracted.Words[0].BoundingBox.Left, 6);
        Assert.Empty(new PdfPageContentReader(original).Read(0).Words);
    }

    private static PdfOcrWord Word(string id, int page, int sequence, string text, double confidence) =>
        new(id, page, sequence, text, text, new PdfContentBounds(0, 0, 10, 10), confidence, "en-US");
}
