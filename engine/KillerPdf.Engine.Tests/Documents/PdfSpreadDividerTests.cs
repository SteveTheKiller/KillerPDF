using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfSpreadDividerTests
{
    [Fact]
    public void DividesSelectedSpreadsInDocumentOrder()
    {
        byte[] sourceBytes = new PdfDocumentBuilder()
            .AddBlankPage(400, 200)
            .AddBlankPage(300, 500)
            .AddBlankPage(600, 200)
            .Build();
        PdfDocument source = PdfDocument.Open(sourceBytes);

        PdfSpreadDivisionResult result = PdfSpreadDivider.Divide(source,
        [
            new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical),
            new PdfSpreadDivisionRequest(2, PdfSpreadDivisionDirection.Vertical, 0.25)
        ]);

        PdfDocument output = PdfDocument.Open(result.Document);
        IReadOnlyList<PdfPageBoxInformation> pages = PdfPageBoxInformation.Read(output);

        Assert.Equal(5, pages.Count);
        Assert.Equal(new PdfPageBoxBounds(0, 0, 200, 200), pages[0].CropBox);
        Assert.Equal(new PdfPageBoxBounds(200, 0, 400, 200), pages[1].CropBox);
        Assert.Equal(new PdfPageBoxBounds(0, 0, 300, 500), pages[2].CropBox);
        Assert.Equal(new PdfPageBoxBounds(0, 0, 150, 200), pages[3].CropBox);
        Assert.Equal(new PdfPageBoxBounds(150, 0, 600, 200), pages[4].CropBox);
        Assert.Equal([
            new PdfSpreadDivisionMapping(0, 0, 1),
            new PdfSpreadDivisionMapping(2, 3, 4)
        ], result.Mappings);
        Assert.True(result.Document.Span.StartsWith(sourceBytes));
    }

    [Theory]
    [InlineData(0, PdfSpreadDivisionDirection.Horizontal, 0, 300, 400, 500, 0, 0, 400, 300)]
    [InlineData(90, PdfSpreadDivisionDirection.Vertical, 0, 0, 400, 200, 0, 200, 400, 500)]
    [InlineData(180, PdfSpreadDivisionDirection.Vertical, 240, 0, 400, 500, 0, 0, 240, 500)]
    [InlineData(270, PdfSpreadDivisionDirection.Horizontal, 240, 0, 400, 500, 0, 0, 240, 500)]
    public void DivisionUsesDisplayedOrientation(
        int rotation, PdfSpreadDivisionDirection direction,
        double firstLeft, double firstBottom, double firstRight, double firstTop,
        double secondLeft, double secondBottom, double secondRight, double secondTop)
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(400, 500)
            .SetPageRotation(0, rotation)
            .Build());

        PdfDocument output = PdfDocument.Open(PdfSpreadDivider.Divide(source,
            [new PdfSpreadDivisionRequest(0, direction, 0.4)]).Document);
        IReadOnlyList<PdfPageBoxInformation> pages = PdfPageBoxInformation.Read(output);

        Assert.Equal(new PdfPageBoxBounds(
            firstLeft, firstBottom, firstRight, firstTop), pages[0].CropBox);
        Assert.Equal(new PdfPageBoxBounds(
            secondLeft, secondBottom, secondRight, secondTop), pages[1].CropBox);
        Assert.All(PdfPageInformation.Read(output), page =>
            Assert.Equal(rotation, page.Rotation));
    }

    [Fact]
    public void RejectsInvalidOrDuplicateRequestsBeforeWriting()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Throws<ArgumentException>(() => PdfSpreadDivider.Divide(source, []));
        Assert.Throws<ArgumentException>(() => PdfSpreadDivider.Divide(source,
        [
            new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical),
            new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Horizontal)
        ]));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfSpreadDivider.Divide(source,
            [new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical, 1)]));
        Assert.Throws<OperationCanceledException>(() => PdfSpreadDivider.Divide(source,
            [new PdfSpreadDivisionRequest(0, PdfSpreadDivisionDirection.Vertical)],
            new CancellationToken(true)));
    }
}
