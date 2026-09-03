using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageContentBatchTests
{
    [Fact]
    public void MalformedPageDoesNotPreventLaterPageExtraction()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(200, 200, "Q"u8.ToArray())
            .AddBlankPage(200, 200).Build());

        IReadOnlyList<PdfPageContentBatchResult> results = PdfPageContentBatch.Read(document);

        Assert.Equal(2, results.Count);
        Assert.False(results[0].Succeeded);
        Assert.Contains("Unbalanced graphics state", results[0].Error);
        Assert.True(results[1].Succeeded);
        Assert.Equal(1, results[1].PageIndex);
    }

    [Fact]
    public void CancellationStopsWithoutReadingAnotherPage()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().Build());

        IReadOnlyList<PdfPageContentBatchResult> results =
            PdfPageContentBatch.Read(document, cancellation.Token);

        Assert.Empty(results);
    }
}
