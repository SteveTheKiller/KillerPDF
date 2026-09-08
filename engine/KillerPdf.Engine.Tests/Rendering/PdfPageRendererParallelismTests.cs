using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererParallelismTests
{
    private static PdfDocument LargePage()
    {
        // A big image over a big fill, both larger than the row-parallel threshold at 1400 px.
        var pixels = new byte[300 * 200 * 3];
        for (int index = 0; index < pixels.Length; index++) pixels[index] = (byte)(index * 7 + index / 300);
        PdfImage image = PdfImage.FromRgb(300, 200, pixels);
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(0.9, 0.3, 0.1).Rectangle(10, 10, 580, 700).Fill()
            .SetFillRgb(0.1, 0.4, 0.8).Transform(1, 0.2, -0.1, 1, 40, 60).Rectangle(0, 0, 400, 300).Fill()
            .DrawImage(image, 30, 200, 550, 500);
        return PdfDocument.Open(new PdfDocumentBuilder().AddPage(612, 792, content).Build());
    }

    [Theory]
    [InlineData(2)]
    [InlineData(6)]
    public void Render_ParallelRowsProduceTheSequentialPixels(int threads)
    {
        PdfDocument document = LargePage();
        var sequential = new PdfRenderOptions(1400, 1811, includeAnnotations: false,
            includeFormFields: false) { CacheResult = false };
        var parallel = new PdfRenderOptions(1400, 1811, includeAnnotations: false,
            includeFormFields: false) { CacheResult = false, MaximumParallelism = threads };

        PdfRenderedPage expected = new PdfPageRenderer(document).Render(0, sequential);
        PdfRenderedPage actual = new PdfPageRenderer(document).Render(0, parallel);

        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
    }

    [Fact]
    public void Render_ParallelRowsHonorCancellation()
    {
        PdfDocument document = LargePage();
        var options = new PdfRenderOptions(1400, 1811, includeAnnotations: false,
            includeFormFields: false) { CacheResult = false, MaximumParallelism = 4 };
        using var source = new CancellationTokenSource();
        source.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new PdfPageRenderer(document).Render(0, options, source.Token));
    }

    [Fact]
    public void Options_DefaultParallelismIsOne()
    {
        Assert.Equal(1, new PdfRenderOptions(10, 10).MaximumParallelism);
    }
}
