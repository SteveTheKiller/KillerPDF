using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
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

    [Fact]
    public void ApplyClipsOriginalContentOutsideRegionAndPlacesExactRaster()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .Rectangle(10, 10, 20, 20).Stroke()
                .Rectangle(60, 60, 20, 20).Stroke())
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 30, 30), 72);
        PdfImage raster = PdfImage.FromRgb(25, 25,
            Enumerable.Repeat((byte)180, 25 * 25 * 3).ToArray());

        byte[] changed = PdfPartialRasterization.Apply(document, 0, plan, raster);
        PdfDocument reopened = PdfDocument.Open(changed);
        PdfPageContent result = new PdfPageContentReader(reopened).Read(0);

        Assert.True(changed.AsSpan(0, source.Length).SequenceEqual(source));
        PdfExtractedImage image = Assert.Single(result.Images);
        Assert.Equal(new PdfContentBounds(5, 5, 30, 30), image.BoundingBox);
        IReadOnlyList<PdfContentInstruction> instructions =
            new PdfPageContentReader(reopened).ReadInstructions(0);
        Assert.Contains(instructions, instruction => instruction.Operator == "W*");
        Assert.True(instructions.Count(instruction => instruction.Operator == "re") >= 3);
    }

    [Fact]
    public void ApplyRejectsRasterDimensionsThatDoNotMatchPlan()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfPartialRasterizationPlan plan = PdfPartialRasterization.Plan(
            new PdfPageContentReader(document).Read(0),
            new PdfContentBounds(0, 0, 10, 10), 72);

        Assert.Throws<ArgumentException>(() => PdfPartialRasterization.Apply(
            document, 0, plan, PdfImage.FromRgb(9, 10, new byte[9 * 10 * 3])));
    }

    [Fact]
    public void ApplyReplacesMultipleRegionsInOneRevision()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .Rectangle(5, 5, 20, 20).Stroke()
                .Rectangle(60, 60, 20, 20).Stroke())
            .Build();
        PdfDocument document = PdfDocument.Open(source);
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan first = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 25, 25), 72);
        PdfPartialRasterizationPlan second = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(60, 60, 80, 80), 72);

        byte[] changed = PdfPartialRasterization.Apply(document,
        [
            new PdfPartialRasterizationReplacement(0, first,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3])),
            new PdfPartialRasterizationReplacement(0, second,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3]))
        ]);
        PdfPageContent result = new PdfPageContentReader(PdfDocument.Open(changed)).Read(0);

        Assert.True(changed.AsSpan(0, source.Length).SequenceEqual(source));
        Assert.Equal([first.Region, second.Region],
            result.Images.Select(image => image.BoundingBox));
    }

    [Fact]
    public void ApplyRejectsOverlappingReplacementRegions()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);
        PdfPartialRasterizationPlan first = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 25, 25), 72);
        PdfPartialRasterizationPlan second = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(20, 20, 40, 40), 72);

        Assert.Throws<ArgumentException>(() => PdfPartialRasterization.Apply(document,
        [
            new PdfPartialRasterizationReplacement(0, first,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3])),
            new PdfPartialRasterizationReplacement(0, second,
                PdfImage.FromRgb(20, 20, new byte[20 * 20 * 3]))
        ]));
    }
}
