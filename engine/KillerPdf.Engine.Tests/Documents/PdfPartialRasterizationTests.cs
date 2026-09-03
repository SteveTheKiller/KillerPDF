using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPartialRasterizationTests
{
    [Fact]
    public void PlanCalculatesPixelsAndSelectsOnlyIntersectingContent()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 20, 40, 60 });
        var content = new PdfContentStreamBuilder()
            .Rectangle(10, 10, 20, 20).Stroke()
            .DrawImage(image, 50, 50, 20, 20);
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);

        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 30, 30), 144);

        Assert.Equal(50, plan.PixelWidth);
        Assert.Equal(50, plan.PixelHeight);
        Assert.Single(plan.Paths);
        Assert.Empty(plan.Images);
        Assert.Empty(plan.TextRuns);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfPartialRasterization.Plan(
                page, new PdfContentBounds(-1, 0, 10, 10), 144));
    }
}
