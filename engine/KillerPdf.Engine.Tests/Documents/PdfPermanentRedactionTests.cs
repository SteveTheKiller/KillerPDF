using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPermanentRedactionTests
{
    [Fact]
    public void RebuildCreatesSingleRevisionImageOnlyPagesWithoutSourceData()
    {
        PdfImage blackedOutPage = PdfImage.FromRgb(2, 1, new byte[] { 0, 0, 0, 255, 255, 255 });

        byte[] output = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(612, 792, blackedOutPage)]);
        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, ["account 12345"]);

        Assert.True(report.Succeeded);
        PdfPageContent content = new PdfPageContentReader(PdfDocument.Open(output)).Read(0);
        Assert.Empty(content.Text);
        Assert.Single(content.Images);
    }

    [Fact]
    public void VerifierRejectsSearchableOrNonRasterOutput()
    {
        byte[] output = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 100).ShowLatin1Text("secret account").EndText()).Build();

        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, ["secret"]);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Code == "NonRasterContent");
        Assert.Contains(report.Findings, finding => finding.Code == "ProhibitedText");
        Assert.Contains(report.Findings, finding => finding.Code == "ProhibitedObjectText");
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(report.Findings.Count,
            json.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void VerifierFindsProhibitedTextInHiddenActiveObjects()
    {
        byte[] clean = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100,
                PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(clean));
        update.AddObject(new PdfString("hidden secret"u8.ToArray(), PdfStringForm.Literal));

        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            update.Build(), 1, ["secret"]);

        PdfRedactionVerificationFinding finding = Assert.Single(report.Findings,
            item => item.Code == "ProhibitedObjectText");
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void RebuildValidatesPageInputAndVerifierHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => PdfPermanentRedaction.RebuildFromSanitizedPages([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfSanitizedRasterPage(0, 100,
            PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 })));
        byte[] output = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100, PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        Assert.Throws<OperationCanceledException>(() => PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void BatchRebuildIsOrderedAndIsolatesDocumentFailures()
    {
        var page = new PdfSanitizedRasterPage(100, 100,
            PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }));

        IReadOnlyList<PdfRedactionBatchResult> results = PdfPermanentRedaction.RebuildBatch([
            new PdfRedactionBatchInput("first.pdf", [page], ["private"]),
            new PdfRedactionBatchInput("broken.pdf", []),
            new PdfRedactionBatchInput("last.pdf", [page])]);

        Assert.Equal([0, 1, 2], results.Select(result => result.Index));
        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.NotNull(results[1].Error);
        Assert.True(results[2].Succeeded);
        Assert.True(results[2].Document.Length > 0);
        Assert.NotNull(PdfDocument.Open(results[2].Document));
    }
}
