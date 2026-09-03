using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
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
}
