using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

[Collection(nameof(SoftMaskAllocationCollection))]
public sealed class PdfInvisibleImageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void InvisibleImagesDoNotAllocateDecodedColorOrMaskPlanes(int placement)
    {
        const int size = 1024;
        byte[] samples = new byte[size * size * 4];
        Array.Fill(samples, (byte)128);
        var image = PdfImage.FromRgba(size, size, samples);
        PdfDocument Create(PdfImage value)
        {
            var content = new PdfContentStreamBuilder();
            if (placement == 1) content.Rectangle(0, 0, 10, 10).Clip();
            if (placement == 2) content.SetOpacity(0);
            if (placement == 3) content.Transform(1, 0, 0, 0, 0, 0);
            if (placement == 4) content.MoveTo(0, 0).LineTo(10, 0).LineTo(0, 10).ClosePath().Clip();
            content.DrawImage(value, placement == 0 ? 200 : 20, 20, 50, 50);
            return PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, content).Build());
        }
        var options = new PdfRenderOptions(100, 100);
        byte[] pixels = new byte[100 * 100 * 4];
        var warm = new PdfPageRenderer(Create(PdfImage.FromRgba(1, 1, new byte[] { 128, 128, 128, 128 })));
        Assert.Empty(warm.RenderInto(0, options, pixels));
        var renderer = new PdfPageRenderer(Create(image));
        long before = GC.GetAllocatedBytesForCurrentThread();
        var diagnostics = renderer.RenderInto(0, options, pixels);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Empty(diagnostics);
        Assert.All(pixels, value => Assert.Equal(255, value));
        Assert.True(allocated < 256 * 1024, $"Invisible image allocated {allocated} bytes.");
    }

    [Fact]
    public void SkippedPlacementDoesNotSuppressLaterVisibleUseOfTheSameImage()
    {
        var image = PdfImage.FromRgba(1, 1, new byte[] { 200, 0, 0, 255 });
        var baseline = new PdfContentStreamBuilder().DrawImage(image, -10, 20, 50, 50);
        var repeated = new PdfContentStreamBuilder()
            .SaveState().Rectangle(0, 0, 10, 10).Clip().DrawImage(image, 20, 20, 50, 50).RestoreState()
            .DrawImage(image, -10, 20, 50, 50);
        var options = new PdfRenderOptions(100, 100);
        var expected = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, baseline).Build())).Render(0, options);
        var actual = new PdfPageRenderer(PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100, repeated).Build())).Render(0, options);
        Assert.Empty(actual.Diagnostics);
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        Assert.Contains((byte)200, actual.Pixels.ToArray());
    }
}
