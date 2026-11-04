using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionRegionFrameTests
{
    [Fact]
    public void RegionPreservesPreciseQuadrantColorsWithinTheFullPageByteBudget()
    {
        var document = PdfDocument.Open(DocumentWithGrayQuadrants());
        var renderer = new PdfPageRenderer(document);
        var options = new PdfRenderOptions(400, 200,
            includeAnnotations: false, includeFormFields: false, maximumPixelBytes: 320_000);

        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, options));

        PdfRenderedFrame frame = renderer.RenderFrameRegion(0, options,
            left: 199, top: 99, width: 2, height: 2);

        Assert.Equal(2, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(PdfFrameColorEncoding.LinearSrgbD65, frame.ColorEncoding);
        Assert.Equal(16, frame.Pixels.Length);
        float[] expected = [0.21413371f, 0.21422631f, 0.05087609f, 0.52252155f];
        const float tolerance = 0.000002f;
        for (int pixel = 0; pixel < expected.Length; pixel++)
        {
            int offset = pixel * 4;
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(frame.Pixels[offset + channel],
                    expected[pixel] - tolerance, expected[pixel] + tolerance);
            Assert.Equal(1f, frame.Pixels[offset + 3]);
        }
        Assert.True(frame.Pixels[4] - frame.Pixels[0] > 0.00008f);
    }

    [Theory]
    [InlineData(-1, 0, 1, 1)]
    [InlineData(0, -1, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0, 0, 1, 0)]
    [InlineData(400, 0, 1, 1)]
    [InlineData(0, 200, 1, 1)]
    [InlineData(int.MaxValue, 0, 2, 1)]
    [InlineData(1, 0, int.MaxValue, 1)]
    public void RegionOutsideThePageRasterIsRejected(int left, int top, int width, int height)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithGrayQuadrants()));

        Assert.Throws<ArgumentOutOfRangeException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(400, 200), left, top, width, height));
    }

    [Fact]
    public void CanceledRegionRenderDoesNotReturnAFrame()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithGrayQuadrants()));

        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(400, 200), 199, 99, 2, 2, new CancellationToken(true)));
    }

    [Fact]
    public void RegionExceedingCombinedPixelStorageIsRejected()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(DocumentWithGrayQuadrants()));

        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(400, 200, maximumPixelBytes: 320_000), 0, 0, 100, 100));
    }

    private static byte[] DocumentWithGrayQuadrants()
    {
        const string content = "0.5001 g 0 25 50 25 re f\n"
            + "0.5002 g 50 25 50 25 re f\n"
            + "0.25 g 0 0 50 25 re f\n"
            + "0.75 g 50 0 50 25 re f\n";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 50] /Resources << >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}endstream"
        ];
        var text = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(text.ToString()));
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(text.ToString());
        text.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets)
            text.Append(offset.ToString("D10", System.Globalization.CultureInfo.InvariantCulture) + " 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
