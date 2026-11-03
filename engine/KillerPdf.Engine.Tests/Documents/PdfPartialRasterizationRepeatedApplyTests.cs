using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPartialRasterizationRepeatedApplyTests
{
    [Fact]
    public void PlanOmitsUnpaintedClippingPathsButCountsPaintedPathsThatAlsoClip()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(source).SetPageContent(0,
            "q 0 0 100 100 re W n Q q 10 10 20 20 re W S Q 60 60 20 20 re f"u8.ToArray()).Build());
        PdfPageContent page = new PdfPageContentReader(document).Read(0);

        PdfPartialRasterizationPlan blank = PdfPartialRasterization.Plan(
            page, new PdfContentBounds(40, 40, 50, 50), 72);
        Assert.Empty(blank.Paths);
        Assert.Empty(blank.Images);
        Assert.Empty(blank.TextRuns);
        PdfExtractedPath stroked = Assert.Single(PdfPartialRasterization.Plan(
            page, new PdfContentBounds(5, 5, 35, 35), 72).Paths);
        Assert.Equal("S", stroked.PaintOperator);
        Assert.True(stroked.IsClippingPath);
        PdfExtractedPath filled = Assert.Single(PdfPartialRasterization.Plan(
            page, new PdfContentBounds(55, 55, 85, 85), 72).Paths);
        Assert.Equal("f", filled.PaintOperator);
    }

    [Fact]
    public void ApplyingTwoRegionsKeepsEarlierFormImageResourcesAndOutsideText()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(60, 70).ShowLatin1Text("keep").EndText()).Build();
        PdfDocument original = PdfDocument.Open(source);
        PdfContentBounds firstRegion = new(5, 5, 25, 25);
        PdfContentBounds secondRegion = new(35, 5, 55, 25);
        PdfImage red = PdfImage.FromRgb(20, 20,
            Enumerable.Range(0, 400).SelectMany(_ => new byte[] { 255, 0, 0 }).ToArray());
        PdfImage blue = PdfImage.FromRgb(20, 20,
            Enumerable.Range(0, 400).SelectMany(_ => new byte[] { 0, 0, 255 }).ToArray());
        byte[] first = PdfPartialRasterization.Apply(original, 0,
            PdfPartialRasterization.Plan(new PdfPageContentReader(original).Read(0), firstRegion, 72), red);
        PdfDocument intermediate = PdfDocument.Open(first);
        byte[] second = PdfPartialRasterization.Apply(intermediate, 0,
            PdfPartialRasterization.Plan(new PdfPageContentReader(intermediate).Read(0), secondRegion, 72), blue);

        PdfDocument reopened = PdfDocument.Open(second);
        PdfPageContent result = new PdfPageContentReader(reopened).Read(0);
        Assert.Equal(new[] { firstRegion, secondRegion }, result.Images.Select(image => image.BoundingBox));
        Assert.Contains(result.GetWords(), word => word.Text == "keep");
        Assert.True(second.AsSpan(0, first.Length).SequenceEqual(first));
        PdfRenderedPage rendered = new PdfPageRenderer(reopened).Render(0, new PdfRenderOptions(100, 100));
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, rendered.Pixels.Slice((85 * 100 + 15) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, rendered.Pixels.Slice((85 * 100 + 45) * 4, 4).ToArray());
        PdfRenderedPage baseline = new PdfPageRenderer(original).Render(0, new PdfRenderOptions(100, 100));
        for (int y = 0; y < 70; y++)
            Assert.Equal(baseline.Pixels.Slice(y * 400, 400).ToArray(), rendered.Pixels.Slice(y * 400, 400).ToArray());
    }
}
