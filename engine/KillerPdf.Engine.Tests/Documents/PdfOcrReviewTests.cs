using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrReviewTests
{
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

    private static PdfOcrWord Word(string id, int page, int sequence, string text, double confidence) =>
        new(id, page, sequence, text, text, new PdfContentBounds(0, 0, 10, 10), confidence, "en-US");
}
