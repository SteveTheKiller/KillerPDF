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

    [Fact]
    public void PlanIncludesOnlyShadingsThatIntersectTheRegion()
    {
        var shading = new PdfAxialGradient(0, 0, 100, 0, [
            new PdfGradientStop(0, new PdfRgbColor(1, 0, 0)),
            new PdfGradientStop(1, new PdfRgbColor(0, 0, 1))]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .SaveState().Rectangle(10, 10, 20, 20).Clip().EndPath()
                .PaintShading(shading).RestoreState())
            .Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);

        PdfPartialRasterizationPlan inside = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(15, 15, 25, 25), 144);
        PdfPartialRasterizationPlan outside = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(50, 50, 60, 60), 144);

        Assert.Single(inside.Shadings);
        Assert.Empty(outside.Shadings);
    }
}
