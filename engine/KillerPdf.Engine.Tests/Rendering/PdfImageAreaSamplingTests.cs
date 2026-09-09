using System.Buffers.Binary;
using System.Security.Cryptography;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfImageAreaSamplingTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReducedAlternatingSamplesPreserveAverageColor(bool rgb, bool rotated)
    {
        byte[] values = Enumerable.Range(0, 16).SelectMany(index => rgb
            ? index % 2 == 0 ? new byte[] { 255, 0, 0 } : new byte[] { 0, 0, 255 }
            : new byte[] { index % 2 == 0 ? (byte)0 : (byte)255 }).ToArray();
        PdfImage image = rgb ? PdfImage.FromRgb(4, 4, values) : PdfImage.FromGray(4, 4, values);
        var content = new PdfContentStreamBuilder();
        if (rotated) content.Transform(0, 1, -1, 0, 2, 0);
        content.DrawImage(image, 0, 0, 2, 2);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 2, content).Build());
        var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(2, 2));
        Assert.Empty(result.Diagnostics);
        for (int pixel = 0; pixel < 4; pixel++)
            Assert.Equal(new byte[] { 128, rgb ? (byte)0 : (byte)128, 128, 255 },
                result.Pixels.Slice(pixel * 4, 4).ToArray());
    }

    [Fact]
    public void ReductionUnderNonrectangularClipPreservesFineStripes()
    {
        byte[] values = Enumerable.Range(0, 256).Select(index => index % 2 == 0 ? (byte)0 : (byte)255).ToArray();
        var content = new PdfContentStreamBuilder().MoveTo(0, 0).LineTo(8, 0).LineTo(0, 8)
            .ClosePath().Clip().DrawImage(PdfImage.FromGray(16, 16, values), 0, 0, 8, 8);
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(8, 8, content).Build());
        var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(8, 8));
        Assert.Empty(result.Diagnostics);
        Assert.Equal(new byte[] { 128, 128, 128, 255 }, result.Pixels.Slice((6 * 8 + 1) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, result.Pixels.Slice((1 * 8 + 6) * 4, 4).ToArray());
    }

    [Fact]
    public void FractionalFootprintWeightsEdgeSamples()
    {
        // Half of each outer sample and all of the middle sample contribute.
        Assert.Equal(0x404040u, PdfImageAreaSampler.Sample([0, 128, 0], 3, 1, 1,
            1.5, 0.5, 2, 1));
        Assert.Equal(0x808080u, PdfImageAreaSampler.Sample([128], 1, 1, 1,
            0.25, 0.25, 2, 2));
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void PackedBinaryReductionMatchesByteSamples(int outputSize, bool rotated)
    {
        byte[] values = Enumerable.Range(0, 17 * 19)
            .Select(index => (index % 17 * 11 + index / 17 * 7) % 8 > 3 ? (byte)255 : (byte)0).ToArray();
        byte[] Render(PdfImage image)
        {
            var content = new PdfContentStreamBuilder();
            if (rotated) content.Transform(0, 1, -1, 0, 10, 0);
            content.MoveTo(0, 0).LineTo(10, 0).LineTo(0, 10).ClosePath().Clip()
                .DrawImage(image, 0, 0, 10, 10);
            var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(10, 10, content).Build());
            var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(outputSize, outputSize));
            Assert.Empty(result.Diagnostics);
            return result.Pixels.ToArray();
        }
        Assert.Equal(Render(PdfImage.FromGray(17, 19, values)), Render(PdfImage.FromBitonal(17, 19, values)));
    }

    [Fact]
    public void SmallFootprintsMatchRetainedScalarBoundaryDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> packed = stackalloc byte[4];
        foreach (int components in new[] { 1, 3 })
        {
            byte[] samples = Enumerable.Range(0, 17 * 13 * components)
                .Select(index => (byte)(index * 37 + index / 17 * 13)).ToArray();
            foreach (double width in new[] { .25, .75, 1, 1.25, 1.999, 2, 2.5, 3, 3.25, 5.5, 19 })
            foreach (double height in new[] { .5, 1, 1.75, 3.5, 15 })
            for (int y = 0; y < 52; y++)
            for (int x = 0; x < 68; x++)
            {
                uint color = PdfImageAreaSampler.Sample(samples, 17, 13, components,
                    (x + .5) / 4, (y + .5) / 4, width, height);
                BinaryPrimitives.WriteUInt32LittleEndian(packed, color);
                hash.AppendData(packed);
            }
        }
        // 388,960 gray/RGB footprints from the scalar sampler, including image edges.
        Assert.Equal("E0BE05592416EF857D24F5BC65FE0AE713606DDA0314CABF0B26DA74A02F32D0",
            Convert.ToHexString(hash.GetHashAndReset()));
    }

    [Fact]
    public void SmallRgbFootprintObservesCancellation()
    {
        Assert.Throws<OperationCanceledException>(() => PdfImageAreaSampler.Sample(new byte[27],
            3, 3, 3, 1.5, 1.5, 2, 2, new CancellationToken(canceled: true)));
    }

    [Theory]
    [InlineData(17, 19, 5, 7)]
    [InlineData(32768, 14, 16384, 2)]
    [InlineData(32768, 15, 16384, 2)]
    public void ConvertedImageReductionMatchesIndependentAreaAverage(int width, int height, int outputWidth, int outputHeight)
    {
        byte[] samples = Enumerable.Range(0, width * height * 4)
            .Select(index => (byte)(index * 37 + index / width * 13)).ToArray();
        var content = new PdfContentStreamBuilder().DrawImage(
            PdfImage.FromCmyk(width, height, samples), 0, 0, outputWidth, outputHeight);
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(outputWidth, outputHeight, content).Build());
        var rendered = new PdfPageRenderer(document).Render(0,
            new PdfRenderOptions(outputWidth, outputHeight));
        Assert.Empty(rendered.Diagnostics);
        for (int py = 0; py < outputHeight; py++)
        for (int px = 0; px < outputWidth; px++)
        {
            double left = px * (double)width / outputWidth;
            double right = (px + 1) * (double)width / outputWidth;
            double top = py * (double)height / outputHeight;
            double bottom = (py + 1) * (double)height / outputHeight;
            double red = 0, green = 0, blue = 0;
            for (int y = (int)top; y < Math.Ceiling(bottom); y++)
            for (int x = (int)left; x < Math.Ceiling(right); x++)
            {
                uint ink = BinaryPrimitives.ReadUInt32LittleEndian(samples.AsSpan((y * width + x) * 4));
                uint rgb = PdfDeviceCmyk.ToRgb(ink);
                double weight = (Math.Min(y + 1, bottom) - Math.Max(y, top))
                    * (Math.Min(x + 1, right) - Math.Max(x, left));
                red += (byte)(rgb >> 16) * weight;
                green += (byte)(rgb >> 8) * weight;
                blue += (byte)rgb * weight;
            }
            double area = (right - left) * (bottom - top);
            Assert.Equal(new byte[] { (byte)Math.Round(blue / area), (byte)Math.Round(green / area),
                    (byte)Math.Round(red / area), 255 },
                rendered.Pixels.Slice((py * outputWidth + px) * 4, 4).ToArray());
        }
    }
}
