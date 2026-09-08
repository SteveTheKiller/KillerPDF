using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPageRendererRectangularClipTests
{
    private static PdfImage GradientImage(int width, int height, int components)
    {
        var pixels = new byte[width * height * components];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                for (int channel = 0; channel < components; channel++)
                    pixels[(y * width + x) * components + channel] =
                        (byte)((x * 37 + y * 11 + channel * 90) & 0xFF);
        return components == 3 ? PdfImage.FromRgb(width, height, pixels)
            : PdfImage.FromGray(width, height, pixels);
    }

    private static PdfRenderedPage Render(PdfContentStreamBuilder content, int width, int height)
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(width / 2.0, height / 2.0, content).Build());
        return new PdfPageRenderer(document).Render(0, new PdfRenderOptions(width, height,
            includeAnnotations: false, includeFormFields: false));
    }

    private static byte[] Pixel(PdfRenderedPage page, int x, int y) =>
        page.Pixels.Slice((y * page.Width + x) * 4, 4).ToArray();

    [Theory]
    [InlineData(3)]
    [InlineData(1)]
    public void Render_RectangularClipOnlyRemovesImagePixelsOutsideTheClip(int components)
    {
        PdfImage image = GradientImage(37, 29, components);
        PdfRenderedPage unclipped = Render(new PdfContentStreamBuilder()
            .DrawImage(image, 3, 4, 44, 32), 100, 80);
        // Clip edges land on whole pixels at the 2x scale, so the clip is a rectangle mask.
        PdfRenderedPage clipped = Render(new PdfContentStreamBuilder()
            .Rectangle(10, 8, 20, 16).Clip().DrawImage(image, 3, 4, 44, 32), 100, 80);

        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 100; x++)
            {
                bool inside = x >= 20 && x < 60 && y >= 80 - 48 && y < 80 - 16;
                byte[] expected = inside ? Pixel(unclipped, x, y) : [255, 255, 255, 255];
                Assert.Equal(expected, Pixel(clipped, x, y));
            }
        // The image actually painted something inside the clip.
        Assert.NotEqual([255, 255, 255, 255], Pixel(clipped, 30, 40));
    }

    [Fact]
    public void Render_RectangularClipOnlyRemovesFillPixelsOutsideTheClip()
    {
        PdfRenderedPage unclipped = Render(new PdfContentStreamBuilder()
            .SetFillRgb(0.2, 0.4, 0.9).Rectangle(2.5, 3.25, 40, 30).Fill(), 100, 80);
        PdfRenderedPage clipped = Render(new PdfContentStreamBuilder()
            .Rectangle(10, 8, 20, 16).Clip()
            .SetFillRgb(0.2, 0.4, 0.9).Rectangle(2.5, 3.25, 40, 30).Fill(), 100, 80);

        for (int y = 0; y < 80; y++)
            for (int x = 0; x < 100; x++)
            {
                bool inside = x >= 20 && x < 60 && y >= 80 - 48 && y < 80 - 16;
                byte[] expected = inside ? Pixel(unclipped, x, y) : [255, 255, 255, 255];
                Assert.Equal(expected, Pixel(clipped, x, y));
            }
        Assert.Equal([230, 102, 51, 255], Pixel(clipped, 30, 40));
    }

    [Fact]
    public void Render_AntialiasedClipStillAppliesPerPixelCoverageToImages()
    {
        PdfImage image = GradientImage(16, 16, 3);
        PdfRenderedPage unclipped = Render(new PdfContentStreamBuilder()
            .DrawImage(image, 0, 0, 50, 40), 100, 80);
        // A rotated square clip has fractional edges, so it is a coverage mask, not a rectangle.
        PdfRenderedPage clipped = Render(new PdfContentStreamBuilder()
            .Transform(0.7071, 0.7071, -0.7071, 0.7071, 25, 5).Rectangle(0, 0, 20, 20).Clip()
            .Transform(0.7071, -0.7071, 0.7071, 0.7071, -21.2132, 14.1421)
            .DrawImage(image, 0, 0, 50, 40), 100, 80);

        // Corners of the page are outside the rotated clip.
        Assert.Equal([255, 255, 255, 255], Pixel(clipped, 2, 2));
        Assert.Equal([255, 255, 255, 255], Pixel(clipped, 97, 77));
        // The clip center shows the image exactly.
        Assert.NotEqual([255, 255, 255, 255], Pixel(clipped, 50, 80 - 38));
        Assert.Equal(Pixel(unclipped, 50, 80 - 38), Pixel(clipped, 50, 80 - 38));
        // Edge pixels are blends between the image and the white page.
        int partial = 0;
        for (int y = 0; y < clipped.Height; y++)
            for (int x = 0; x < clipped.Width; x++)
            {
                byte[] pixel = Pixel(clipped, x, y);
                if (pixel[0] == 255 && pixel[1] == 255 && pixel[2] == 255) continue;
                if (!pixel.SequenceEqual(Pixel(unclipped, x, y))) partial++;
            }
        Assert.True(partial > 20, $"Expected antialiased clip edges, found {partial} blended pixels.");
    }
}
