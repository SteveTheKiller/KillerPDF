using System.Text.Json;
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
    public void MacroStepCreatesValidatedProviderOptions()
    {
        var step = new PdfMacroStep(PdfMacroOperation.Ocr,
            new Dictionary<string, string>
            {
                ["languages"] = "en-US, de-DE",
                ["outputMode"] = "exact-image",
                ["deskew"] = "false",
                ["removeNoise"] = "true",
                ["detectPageSegments"] = "false"
            });

        PdfOcrOptions options = PdfOcrOptions.FromMacroStep(step);

        Assert.Equal(["en-US", "de-DE"], options.Languages);
        Assert.Equal(PdfOcrOutputMode.ExactImage, options.OutputMode);
        Assert.False(options.Deskew);
        Assert.True(options.CorrectOrientation);
        Assert.True(options.RemoveNoise);
        Assert.False(options.DetectPageSegments);
        Assert.Throws<ArgumentException>(() => PdfOcrOptions.FromMacroStep(
            new PdfMacroStep(PdfMacroOperation.Save)));
        Assert.Throws<ArgumentException>(() => PdfOcrOptions.FromMacroStep(
            new PdfMacroStep(PdfMacroOperation.Ocr,
                new Dictionary<string, string> { ["deskew"] = "sometimes" })));
        Assert.Throws<ArgumentException>(() => PdfOcrOptions.FromMacroStep(
            new PdfMacroStep(PdfMacroOperation.Ocr,
                new Dictionary<string, string> { ["providerScript"] = "run" })));
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
    public void BatchRunnerUsesTheReusableProviderContract()
    {
        var options = new PdfOcrOptions(["en-US", "fr"]);
        var provider = new RecordingProvider();

        PdfOcrBatchReport report = PdfOcrBatchRunner.RunReport([
            new PdfOcrBatchPage("scan.pdf", 2, new byte[] { 7 })], options, provider);

        Assert.Equal(1, report.SucceededCount);
        Assert.Same(options, provider.Options);
        Assert.Equal(("scan.pdf", 2, (byte)7), provider.Input);
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
    public void TextExportPreservesEmptySourcePages()
    {
        var review = new PdfOcrReview([
            Word("first", 0, 0, "First", 1),
            Word("last", 2, 0, "Last", 1)]);

        string text = review.ExportText(4);

        Assert.Equal(string.Join(Environment.NewLine, ["First", "", "Last", ""]), text);
        Assert.Equal(string.Join(Environment.NewLine, ["First", "", "Last"]),
            review.ExportText());
        Assert.Throws<ArgumentOutOfRangeException>(() => review.ExportText(2));
    }

    [Fact]
    public void ReviewSelectsPagesWithoutChangingSourceState()
    {
        var original = new PdfOcrReview([
            Word("first", 0, 0, "First", 0.9),
            Word("middle", 1, 0, "Middle", 0.8),
            Word("last", 2, 0, "Last", 0.7)
        ]).Correct("middle", "Corrected");

        PdfOcrReview selected = original.SelectPages([2, 1]);

        Assert.Equal(["middle", "last"], selected.Words.Select(word => word.Id));
        Assert.Equal("Corrected", selected.Words[0].Text);
        Assert.Equal(3, original.Words.Count);
        Assert.Throws<ArgumentException>(() => original.SelectPages([]));
        Assert.Throws<ArgumentException>(() => original.SelectPages([1, 1]));
        Assert.Throws<ArgumentException>(() => original.SelectPages([-1]));
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
    public void ConfidentWordsCanBeAcceptedInOneReviewStep()
    {
        var original = new PdfOcrReview([
            Word("low", 0, 0, "Kiler", 0.49),
            Word("threshold", 0, 1, "PDF", 0.9),
            Word("high", 0, 2, "Engine", 0.99)]);

        PdfOcrReview reviewed = original.AcceptConfidentWords(0.9);

        Assert.Equal(PdfOcrWordStatus.Pending, reviewed.Words[0].Status);
        Assert.Equal(PdfOcrWordStatus.Ignored, reviewed.Words[1].Status);
        Assert.Equal(PdfOcrWordStatus.Ignored, reviewed.Words[2].Status);
        Assert.All(original.Words,
            word => Assert.Equal(PdfOcrWordStatus.Pending, word.Status));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            original.AcceptConfidentWords(-0.1));
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
    public void BatchRunnerBoundsParallelismAndPreservesInputOrder()
    {
        PdfOcrBatchPage[] pages = Enumerable.Range(0, 8)
            .Select(index => new PdfOcrBatchPage(
                $"page-{index}.pdf", index, new byte[] { (byte)index }))
            .ToArray();
        int running = 0, maximumRunning = 0;

        IReadOnlyList<PdfOcrBatchResult> results = PdfOcrBatchRunner.Run(
            pages, (page, _) =>
            {
                int current = Interlocked.Increment(ref running);
                lock (pages) maximumRunning = Math.Max(maximumRunning, current);
                Thread.Sleep(20);
                Interlocked.Decrement(ref running);
                return new PdfOcrReview([
                    Word(page.SourceName, page.PageIndex, 0, "ok", 1)]);
            }, maximumDegreeOfParallelism: 2);

        Assert.Equal(Enumerable.Range(0, pages.Length),
            results.Select(result => result.Input.PageIndex));
        Assert.InRange(maximumRunning, 1, 2);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfOcrBatchRunner.Run(
            pages, (_, _) => new PdfOcrReview([]), maximumDegreeOfParallelism: 0));
    }

    [Fact]
    public void BatchRunnerRejectsProviderResultsForAnotherPageAndContinues()
    {
        PdfOcrBatchPage[] pages = [
            new("wrong.pdf", 2, new byte[] { 1 }),
            new("next.pdf", 3, new byte[] { 2 })];

        IReadOnlyList<PdfOcrBatchResult> results = PdfOcrBatchRunner.Run(
            pages, (page, _) => new PdfOcrReview([
                Word(page.SourceName, page.Source.Span[0] == 1 ? 1 : page.PageIndex,
                    0, "text", 1)]));

        Assert.Equal("The OCR provider returned words for a different source page.",
            results[0].Error);
        Assert.False(results[0].Succeeded);
        Assert.True(results[1].Succeeded);
    }

    [Fact]
    public void BatchReportSummarizesPagesWithoutSourceOrRecognizedText()
    {
        PdfOcrBatchPage[] pages = [
            new("first.pdf", 0, new byte[] { 1, 2, 3 }),
            new("bad.pdf", 2, new byte[] { 4, 5, 6 })];

        PdfOcrBatchReport report = PdfOcrBatchRunner.RunReport(pages, (page, _) =>
            page.SourceName == "bad.pdf"
                ? throw new InvalidOperationException("Unreadable page")
                : new PdfOcrReview([Word("secret-id", 0, 0, "secret text", 1)]));
        string json = report.ToJson();
        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal(2, report.TotalPageCount);
        Assert.Equal(1, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(0, report.CanceledCount);
        Assert.Equal(0, report.UnprocessedCount);
        Assert.Equal(2, report.Sources.Count);
        Assert.Equal(new PdfOcrBatchSourceSummary("first.pdf", 1, 1, 0, 0), report.Sources[0]);
        Assert.Equal(new PdfOcrBatchSourceSummary("bad.pdf", 1, 0, 1, 0), report.Sources[1]);
        Assert.Equal("bad.pdf", parsed.RootElement.GetProperty("sources")[1]
            .GetProperty("sourceName").GetString());
        Assert.Equal("first.pdf", parsed.RootElement.GetProperty("results")[0]
            .GetProperty("sourceName").GetString());
        Assert.Equal(1, parsed.RootElement.GetProperty("results")[0]
            .GetProperty("wordCount").GetInt32());
        Assert.DoesNotContain("AQID", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret text", json, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-id", json, StringComparison.Ordinal);
        string text = report.ToText();
        Assert.Contains("OCR batch pages: 2", text, StringComparison.Ordinal);
        Assert.Contains("first.pdf | Page 1 | Succeeded | Words 1", text,
            StringComparison.Ordinal);
        Assert.Contains("bad.pdf | Page 3 | Failed: Unreadable page", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("secret text", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-id", text, StringComparison.Ordinal);
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
        string json = report.ToJson();
        string csv = report.ToCsv();
        Assert.Contains("\"lowConfidenceThreshold\":0.5", json, StringComparison.Ordinal);
        Assert.Contains("Page,Words,AverageConfidence", csv, StringComparison.Ordinal);
        Assert.Contains("1,2,0.7,1,0,1,1,0", csv, StringComparison.Ordinal);
        Assert.DoesNotContain("Killer", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Kiler", csv, StringComparison.Ordinal);
        string text = report.ToText();
        Assert.Contains("OCR accuracy: Review required", text, StringComparison.Ordinal);
        Assert.Contains("Words: 3", text, StringComparison.Ordinal);
        Assert.Contains("Page 1 | Words 2 | Confidence 0.7", text,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Killer", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Kiler", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ReviewedTextCanBeWrittenAsASearchableInvisibleLayer()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 100).Build());
        var review = new PdfOcrReview([
            new PdfOcrWord("a", 0, 0, "B", "B",
                new PdfContentBounds(20, 30, 50, 50), 0.9, "en-US")]);
        TrueTypeFont font = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));

        PdfDocument searchable = PdfDocument.Open(
            review.Correct("a", "A").WriteSearchableText(original, font));

        PdfPageContent extracted = new PdfPageContentReader(searchable).Read(0);
        Assert.Equal("A", extracted.Text);
        Assert.Equal(20, extracted.Words[0].BoundingBox.Left, 6);
        Assert.Empty(new PdfPageContentReader(original).Read(0).Words);
    }

    private static PdfOcrWord Word(string id, int page, int sequence, string text, double confidence) =>
        new(id, page, sequence, text, text, new PdfContentBounds(0, 0, 10, 10), confidence, "en-US");

    private sealed class RecordingProvider : IPdfOcrProvider
    {
        public PdfOcrOptions? Options { get; private set; }
        public (string Name, int Page, byte Value) Input { get; private set; }

        public PdfOcrReview Recognize(PdfOcrBatchPage page, PdfOcrOptions options,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Options = options;
            Input = (page.SourceName, page.PageIndex, page.Source.Span[0]);
            return new PdfOcrReview([Word("provider", page.PageIndex, 0, "text", 1)]);
        }
    }
}
