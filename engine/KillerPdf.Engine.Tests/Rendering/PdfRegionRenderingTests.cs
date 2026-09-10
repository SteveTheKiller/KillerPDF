using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfRegionRenderingTests
{
    [Theory]
    [InlineData(false, "-20 4 m 90 59 l 4 75 l h S")]
    [InlineData(true, "-20 4 m 90 59 l 4 75 l h S")]
    [InlineData(false, "-8 20 m 10 100 90 -40 75 40 c S")]
    [InlineData(true, "-8 20 m 10 100 90 -40 75 40 c S")]
    [InlineData(false, "4 4 56 56 re W n [3 2] 1 d -10 55 m 74 8 l S")]
    [InlineData(true, "4 4 56 56 re W n [3 2] 1 d -10 55 m 74 8 l S")]
    public void RegionsPreserveStrokesCrossingTheirBoundaries(bool transparent, string path)
    {
        var renderer = CreateRenderer("0.2 0.7 0.4 RG 5.25 w 1 J 1 j " + path);
        var options = new PdfRenderOptions(128, 128, transparentBackground: transparent)
        {
            CacheResult = false
        };
        PdfRenderedPage full = renderer.Render(0, options);
        Assert.Empty(full.Diagnostics);
        Assert.Contains(full.Pixels.ToArray(), value => value is > 0 and < 255);
        foreach (int top in new[] { 0, 17, 57, 96 })
        foreach (int left in new[] { 0, 19, 61, 96 })
        {
            PdfRenderedPage region = renderer.RenderRegion(0, options, left, top, 32, 32);
            Assert.Empty(region.Diagnostics);
            Assert.Equal(32, region.Width);
            Assert.Equal(32, region.Height);
            for (int row = 0; row < 32; row++)
                Assert.Equal(full.Pixels.Slice(((top + row) * 128 + left) * 4, 128).ToArray(),
                    region.Pixels.Slice(row * 128, 128).ToArray());
        }
    }

    [Fact]
    public void RegionRejectsInvalidBoundsAndHonorsCancellation()
    {
        var renderer = CreateRenderer("0 0 m 64 64 l S");
        var options = new PdfRenderOptions(128, 128);
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderRegion(0, options, -1, 0, 32, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderRegion(0, options, 0, 0, 0, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderRegion(0, options, 100, 100, 32, 32));
        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderRegion(0, options, int.MaxValue, 0, 32, 32));
        Assert.Throws<OperationCanceledException>(() => renderer.RenderRegion(0, options, 0, 0, 32, 32,
            new CancellationToken(canceled: true)));
    }

    private static PdfPageRenderer CreateRenderer(string content) => new(PdfDocument.Open(
        new PdfDocumentBuilder().AddPage(64, 64, Encoding.ASCII.GetBytes(content)).Build()));
}
