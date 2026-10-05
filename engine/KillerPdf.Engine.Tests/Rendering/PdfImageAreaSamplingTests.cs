using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfImageAreaSamplingTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void SlightlyReducedCheckerboardPreservesAverageGray(int bits)
    {
        byte[] samples = bits switch
        {
            1 => [0x40, 0xA0, 0x40],
            8 => [0, 255, 0, 255, 0, 255, 0, 255, 0],
            _ => [.. Enumerable.Range(0, 9).SelectMany(pixel =>
                new byte[] { pixel % 2 == 0 ? (byte)0 : (byte)255,
                    pixel % 2 == 0 ? (byte)0 : (byte)255 })]
        };
        var rendered = RenderGray(samples, bits, 3, 2, "2 0 0 2 0 0");
        // Each 1.5 by 1.5 footprint contains white area 1 out of total area 2.25.
        for (int pixel = 0; pixel < 4; pixel++)
            Assert.Equal(new byte[] { 113, 113, 113, 255 }, rendered.Pixels.Slice(pixel * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(1, false, false)]
    [InlineData(1, true, false)]
    [InlineData(1, false, true)]
    [InlineData(8, false, false)]
    [InlineData(16, false, false)]
    [InlineData(16, true, false)]
    [InlineData(16, false, true)]
    public void SlightReductionUsesTheActualFractionalPixelFootprint(int bits, bool flipX, bool flipY)
    {
        byte[] values = [.. Enumerable.Range(0, 25).Select(pixel => pixel % 5 == 1 ? (byte)255 : (byte)0)];
        byte[] samples = bits == 1 ? [0x40, 0x40, 0x40, 0x40, 0x40]
            : bits == 8 ? values : [.. values.SelectMany(value => new byte[] { value, value })];
        string matrix = $"{(flipX ? "-3" : "3")} 0 0 {(flipY ? "-3" : "3")} {(flipX ? "3.75" : "0.25")} {(flipY ? "3.25" : "0.25")}";
        var rendered = RenderGray(samples, bits, 5, 4, matrix);
        int x = flipX ? 2 : 1;
        // White contributes 0.75 source pixels to a footprint of width 5/3.
        Assert.Equal(new byte[] { 115, 115, 115, 255 }, rendered.Pixels.Slice((4 + x) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    [InlineData(16, false)]
    [InlineData(16, true)]
    public void SlightReductionUsesTheActualFractionalRowFootprint(int bits, bool flipY)
    {
        byte[] values = [.. Enumerable.Range(0, 25).Select(pixel => pixel / 5 == 1 ? (byte)255 : (byte)0)];
        byte[] samples = bits == 1 ? [0, 0xF8, 0, 0, 0]
            : bits == 8 ? values : [.. values.SelectMany(value => new byte[] { value, value })];
        var rendered = RenderGray(samples, bits, 5, 4, flipY ? "3 0 0 -3 .25 3.25" : "3 0 0 3 .25 .75");
        int y = flipY ? 2 : 1;
        Assert.Equal(new byte[] { 115, 115, 115, 255 }, rendered.Pixels.Slice((y * 4 + 1) * 4, 4).ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void CroppedNativeSizeImageDoesNotBecomeAReducedImage(int bits)
    {
        byte[] values = [.. Enumerable.Range(0, 25).Select(pixel => pixel % 5 == 1 ? (byte)255 : (byte)0)];
        byte[] samples = bits == 1 ? [0x40, 0x40, 0x40, 0x40, 0x40]
            : bits == 8 ? values : [.. values.SelectMany(value => new byte[] { value, value })];
        var rendered = RenderGray(samples, bits, 5, 3, "5 0 0 5 -.25 -.25");
        Assert.Equal(new byte[] { 0, 0, 0, 255 }, rendered.Pixels[..4].ToArray());
    }

    [Theory]
    [InlineData(1, 6, false)]
    [InlineData(8, 6, false)]
    [InlineData(16, 6, false)]
    [InlineData(1, 3, false)]
    [InlineData(8, 3, false)]
    [InlineData(16, 3, false)]
    [InlineData(1, 6, true)]
    [InlineData(8, 6, true)]
    [InlineData(16, 6, true)]
    [InlineData(1, 3, true)]
    [InlineData(8, 3, true)]
    [InlineData(16, 3, true)]
    public void PageCropDoesNotChangeImageSamplingScale(int bits, int drawnSize, bool rowStripe)
    {
        byte[] values = [.. Enumerable.Range(0, 36).Select(pixel =>
            (rowStripe ? pixel / 6 == 4 : pixel % 6 == 1) ? (byte)255 : (byte)0)];
        byte[] samples = bits == 1 ? rowStripe ? [0, 0, 0, 0, 0xFC, 0]
                : [0x40, 0x40, 0x40, 0x40, 0x40, 0x40]
            : bits == 8 ? values : [.. values.SelectMany(value => new byte[] { value, value })];
        var rendered = RenderGray(samples, bits, 6, 2, $"{drawnSize} 0 0 {drawnSize} 0 0");
        // Native pixels retain black/white; a twofold reduction averages pairs.
        byte[] expected = rowStripe ? drawnSize == 6 ? [255, 0] : [0, 128]
            : drawnSize == 6 ? [0, 255] : [128, 0];
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
        {
            byte gray = expected[rowStripe ? y : x];
            Assert.Equal(new byte[] { gray, gray, gray, 255 },
                rendered.Pixels.Slice((y * 2 + x) * 4, 4).ToArray());
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(16)]
    public void FractionalNativeSizeCropMatchesUncroppedSamples(int bits)
    {
        byte[] values = [.. Enumerable.Range(0, 36).Select(pixel => pixel % 6 == 1 ? (byte)255 : (byte)0)];
        byte[] samples = bits == 1 ? [0x40, 0x40, 0x40, 0x40, 0x40, 0x40]
            : bits == 8 ? values : [.. values.SelectMany(value => new byte[] { value, value })];
        var cropped = RenderGray(samples, bits, 6, 3, "6 0 0 6 -.25 1", pageHeight: 8);
        var full = RenderGray(samples, bits, 6, 10, "6 0 0 6 3.75 1", pageHeight: 8);
        for (int y = 1; y < 7; y++)
        for (int x = 0; x < 3; x++)
            Assert.Equal(full.Pixels.Slice((y * 10 + x + 4) * 4, 4).ToArray(),
                cropped.Pixels.Slice((y * 3 + x) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, cropped.Pixels.Slice((3 * 3 + 1) * 4, 4).ToArray());
    }

    [Fact]
    public void FractionalNativeSizeCropDoesNotInterpolateSoftMask()
    {
        byte[] rgba = [.. Enumerable.Range(0, 36).SelectMany(pixel =>
            new byte[] { 255, 0, 0, pixel % 6 == 1 ? (byte)0 : (byte)255 })];
        PdfRenderedPage Render(int pageWidth, double x)
        {
            var content = new PdfContentStreamBuilder().DrawImage(PdfImage.FromRgba(6, 6, rgba), x, 1, 6, 6);
            var document = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageWidth, 8, content).Build());
            var result = new PdfPageRenderer(document).Render(0, new PdfRenderOptions(pageWidth, 8));
            Assert.Empty(result.Diagnostics);
            return result;
        }
        var cropped = Render(3, -.25);
        var full = Render(10, 3.75);
        for (int y = 1; y < 7; y++)
        for (int x = 0; x < 3; x++)
            Assert.Equal(full.Pixels.Slice((y * 10 + x + 4) * 4, 4).ToArray(),
                cropped.Pixels.Slice((y * 3 + x) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, cropped.Pixels.Slice((3 * 3 + 1) * 4, 4).ToArray());
        Assert.Equal(new byte[] { 0, 0, 255, 255 }, cropped.Pixels.Slice(3 * 3 * 4, 4).ToArray());
    }

    [Fact]
    public void FractionalNativeSizeStencilCropMatchesUncroppedSamples()
    {
        byte[] samples = [0x40, 0x40, 0x40, 0x40, 0x40, 0x40];
        var cropped = RenderGray(samples, 1, 6, 3, "6 0 0 6 -.25 1", pageHeight: 8, imageMask: true);
        var full = RenderGray(samples, 1, 6, 10, "6 0 0 6 3.75 1", pageHeight: 8, imageMask: true);
        for (int y = 1; y < 7; y++)
        for (int x = 0; x < 3; x++)
            Assert.Equal(full.Pixels.Slice((y * 10 + x + 4) * 4, 4).ToArray(),
                cropped.Pixels.Slice((y * 3 + x) * 4, 4).ToArray());
        Assert.NotEqual(cropped.Pixels.Slice(3 * 3 * 4, 4).ToArray(),
            cropped.Pixels.Slice((3 * 3 + 1) * 4, 4).ToArray());
    }

    [Fact]
    public void ReducedStencilCroppedInBothDimensionsUsesFullImagePlane()
    {
        byte[] samples = [0x30, 0x30, 0x30, 0x30, 0x30, 0x30];
        var cropped = RenderGray(samples, 1, 6, 2, "3 0 0 3 -1 -1", imageMask: true);
        var full = RenderGray(samples, 1, 6, 8, "3 0 0 3 3 3", imageMask: true);
        for (int y = 0; y < 2; y++)
        for (int x = 0; x < 2; x++)
            Assert.Equal(full.Pixels.Slice(((y + 2) * 8 + x + 4) * 4, 4).ToArray(),
                cropped.Pixels.Slice((y * 2 + x) * 4, 4).ToArray());
        Assert.NotEqual(cropped.Pixels[..4].ToArray(), cropped.Pixels.Slice(4, 4).ToArray());
    }

    [Fact]
    public void FractionalReductionWithPageCropKeepsRectangularClipSamplesAligned()
    {
        byte[] samples = [.. Enumerable.Range(0, 64).SelectMany(pixel =>
            new byte[] { (byte)(pixel % 8 * 32), (byte)(pixel % 8 * 32) })];
        const string matrix = "6.4 0 0 6.4 -2.25 0";
        var full = RenderGray(samples, 16, 8, 4, matrix);
        var clipped = RenderGray(samples, 16, 8, 4, matrix, contentPrefix: "1 0 2 4 re W n ");
        for (int y = 0; y < 4; y++)
        for (int x = 1; x < 3; x++)
            Assert.Equal(full.Pixels.Slice((y * 4 + x) * 4, 4).ToArray(),
                clipped.Pixels.Slice((y * 4 + x) * 4, 4).ToArray());
        Assert.NotEqual(full.Pixels.Slice(4, 4).ToArray(), full.Pixels.Slice(8, 4).ToArray());
    }

    private static PdfRenderedPage RenderGray(byte[] samples, int bits, int imageSize, int pageSize,
        string matrix, int? pageHeight = null, bool imageMask = false, string contentPrefix = "")
    {
        int height = pageHeight ?? pageSize;
        var source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(pageSize, height,
            Encoding.ASCII.GetBytes(contentPrefix + matrix + " cm /Image Do")).Build());
        PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
        KeyValuePair<PdfName, PdfObject> Entry(string name, PdfObject value) => new(Name(name), value);
        var catalog = (PdfDictionary)source.Resolve((PdfIndirectReference)source.Trailer[Name("Root")]);
        var pages = (PdfDictionary)source.Resolve((PdfIndirectReference)catalog[Name("Pages")]);
        var pageReference = (PdfIndirectReference)((PdfArray)pages[Name("Kids")])[0];
        var page = (PdfDictionary)source.Resolve(pageReference);
        var update = new PdfIncrementalUpdateBuilder(source);
        var imageEntries = new List<KeyValuePair<PdfName, PdfObject>>([
            Entry("Subtype", Name("Image")), Entry("Width", new PdfInteger(imageSize)),
            Entry("Height", new PdfInteger(imageSize)), Entry("BitsPerComponent", new PdfInteger(bits))])
        {
            imageMask ? Entry("ImageMask", new PdfBoolean(true))
            : Entry("ColorSpace", Name("DeviceGray"))
        };
        var image = update.AddObject(new PdfStream(new PdfDictionary(imageEntries), samples));
        var resources = new PdfDictionary([Entry("XObject", new PdfDictionary([Entry("Image", image)]))]);
        update.ReplaceObject(pageReference.ObjectNumber, new PdfDictionary(page
            .Where(pair => !pair.Key.Equals(Name("Resources"))).Append(Entry("Resources", resources))));
        var rendered = new PdfPageRenderer(PdfDocument.Open(update.Build())).Render(0, new PdfRenderOptions(pageSize, height));
        Assert.Empty(rendered.Diagnostics);
        return rendered;
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ReducedAlternatingSamplesPreserveAverageColor(bool rgb, bool rotated)
    {
        byte[] values = [.. Enumerable.Range(0, 16).SelectMany(index => rgb
            ? index % 2 == 0 ? new byte[] { 255, 0, 0 } : [0, 0, 255]
            : [index % 2 == 0 ? (byte)0 : (byte)255])];
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
        byte[] values = [.. Enumerable.Range(0, 256).Select(index => index % 2 == 0 ? (byte)0 : (byte)255)];
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
    [InlineData(1)]
    [InlineData(3)]
    public void PrecomputedColumnsMatchDirectSampling(int components)
    {
        byte[] samples = [.. Enumerable.Range(0, 17 * 13 * components).Select(index => (byte)(index * 37 + index / 17 * 13))];
        var row = new PdfImageAreaSampler.Row(13, 6.25, 2.75);
        foreach (double center in new[] { .25, 1.5, 8.75, 16.8 })
        foreach (double footprint in new[] { 1.25, 1.999, 2, 2.75, 5.5 })
        {
            uint expected = PdfImageAreaSampler.Sample(samples, 17, components,
                center, footprint, row);
            var column = new PdfImageAreaSampler.Column(17, center, footprint);
            Assert.Equal(expected, PdfImageAreaSampler.Sample(samples, 17,
                components, column, row));
        }
    }

    [Theory]
    [InlineData(5, false)]
    [InlineData(5, true)]
    [InlineData(8, false)]
    [InlineData(8, true)]
    public void PackedBinaryReductionMatchesByteSamples(int outputSize, bool rotated)
    {
        byte[] values = [.. Enumerable.Range(0, 17 * 19).Select(index => (index % 17 * 11 + index / 17 * 7) % 8 > 3 ? (byte)255 : (byte)0)];
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
    public void NativeBitonalImageClipsTrailingSourceColumn()
    {
        byte[] values = [.. Enumerable.Range(0, 512 * 512).Select(index =>
            (byte)(((index % 512 / 3 + index / 512 / 7) & 1) == 0 ? 0 : 255))];
        var content = new PdfContentStreamBuilder().DrawImage(
            PdfImage.FromBitonal(512, 512, values), 0, 0, 511.0002, 512);
        var document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(511, 512, content).Build());

        PdfRenderedPage rendered = new PdfPageRenderer(document).Render(
            0, new PdfRenderOptions(511, 512));

        byte[] expected = new byte[511 * 512 * 4];
        for (int y = 0; y < 512; y++)
        for (int x = 0; x < 511; x++)
        {
            byte gray = values[y * 512 + x];
            int offset = (y * 511 + x) * 4;
            expected[offset] = gray;
            expected[offset + 1] = gray;
            expected[offset + 2] = gray;
            expected[offset + 3] = 255;
        }
        Assert.Empty(rendered.Diagnostics);
        Assert.Equal(expected, rendered.Pixels.ToArray());
    }

    [Fact]
    public void SmallFootprintsMatchRetainedScalarBoundaryDigest()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> packed = stackalloc byte[4];
        foreach (int components in new[] { 1, 3 })
        {
            byte[] samples = [.. Enumerable.Range(0, 17 * 13 * components).Select(index => (byte)(index * 37 + index / 17 * 13))];
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
    [InlineData(17, 19, 12, 14)]
    [InlineData(17, 19, 5, 7)]
    [InlineData(32768, 14, 16384, 2)]
    [InlineData(32768, 15, 16384, 2)]
    public void ConvertedImageReductionMatchesIndependentAreaAverage(int width, int height, int outputWidth, int outputHeight)
    {
        byte[] samples = [.. Enumerable.Range(0, width * height * 4).Select(index => (byte)(index * 37 + index / width * 13))];
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
