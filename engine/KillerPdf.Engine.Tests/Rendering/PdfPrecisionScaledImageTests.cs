using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Scanned pages, DjVu pages converted to PDF and masked artwork draw their images at whatever
// scale the view asks for. The float frame samples them the way the byte render does: 1-bit and
// stencil reductions average the source under each pixel, enlargements keep crisp samples,
// image soft masks and /Mask stencils of any size and depth are sampled at the image position.
public sealed class PdfPrecisionScaledImageTests
{
    [Fact]
    public void ReducedSoftMaskWorkedPixelRetainsUnroundedMeansAndInterpolation()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Scenario("soft-mask-reduced")));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(16, 16, transparentBackground: true));
        var opaque = renderer.RenderFrame(0, new PdfRenderOptions(16, 16));
        var bytes = renderer.Render(0, new PdfRenderOptions(16, 16));
        // Pixel (9,10) reads source (32,26), blue 10. Its four virtual mask cells
        // contain [105,234], [139,177], [182,188,84,204], [192,2,231,219].
        // Their means 169.5,158,164.5,161 interpolate with four equal weights.
        const int pixel = 10 * 16 + 9;
        double alpha = (169.5 + 158 + 164.5 + 161) / 4 / 255;
        double composite = (10 * alpha + 255 * (1 - alpha)) / 255;
        double linear = Math.Pow((composite + .055) / 1.055, 2.4);
        Assert.InRange(frame.Pixels[pixel * 4 + 3], alpha - 1e-7, alpha + 1e-7);
        Assert.InRange(opaque.Pixels[pixel * 4 + 2], linear - 1e-7, linear + 1e-7);
        // The byte path rounds means to 170,158,165,161, interpolation to 164,
        // then the white composite to 97. Source blue is an exact eight-bit sample.
        Assert.Equal(97, bytes.Pixels.Span[pixel * 4]);
    }

    [Fact]
    public void SingleAxisStencilLatticeRefusesExcessWorkBeforeSampling()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("10 0 0 10 0 0 cm",
            Stencil(11000, 1, new string('A', 2750) + ">"))));
        // 1100 by 1000 pixels with an 8 by 8 lattice exceeds 64 million source visits.
        var error = Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(1100, 1000, transparentBackground: true)));
        Assert.Contains("weighted work limit", error.Message);
        Assert.Empty(renderer.RenderFrame(0, new PdfRenderOptions(100, 1)).Diagnostics);
    }

    [Fact]
    public void SingleAxisStencilReductionRetainsLiteralHalfCoverage()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(".2 .4 .6 rg 10 0 0 10 0 0 cm",
            Stencil(4, 1, "A0>"))));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 4, transparentBackground: true));
        // Each centered 4 by 4 lattice visits two zero columns and two one columns.
        for (int pixel = 0; pixel < 8; pixel++)
            Assert.Equal(.5f, frame.Pixels[pixel * 4 + 3]);
    }

    [Fact]
    public void WeightedSixteenBitGrayReductionAppliesReversedDecodeBeforeTransfer()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("10 0 0 10 0 0 cm",
            Image(3, 1, "/DeviceGray", 16, "00008000FFFF>", "/Decode [1 0]"))));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1));
        double[] means = [1 - (32768d / 65535) / 3, 1 - (32768d / 65535 + 2) / 3];
        for (int pixel = 0; pixel < 2; pixel++)
        {
            double encoded = means[pixel];
            double expected = encoded <= .04045 ? encoded / 12.92 : Math.Pow((encoded + .055) / 1.055, 2.4);
            Assert.InRange(frame.Pixels[pixel * 4], expected - 1e-7, expected + 1e-7);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentSizeSixteenBitSoftMaskRetainsAlphaAndDecode(bool reversed)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("10 0 0 10 0 0 cm",
            Image(2, 1, "/DeviceGray", 8, "FFFF>", "/SMask 5 0 R"),
            Image(1, 1, "/DeviceGray", 16, "8001>", reversed ? "/Decode [1 0]" : ""))));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 1, transparentBackground: true));
        double expected = reversed ? 1 - 32769d / 65535 : 32769d / 65535;
        for (int pixel = 0; pixel < 4; pixel++)
        {
            Assert.Equal(1f, frame.Pixels[pixel * 4]);
            Assert.InRange(frame.Pixels[pixel * 4 + 3], expected - 1e-7, expected + 1e-7);
        }
    }

    [Theory]
    [InlineData("gray-fractional-reduction")]
    [InlineData("rgb-fractional-reduction")]
    [InlineData("bitonal-offset")]
    [InlineData("stencil-reduced")]
    [InlineData("soft-mask-reduced")]
    public void CroppingPreservesFullPageSamplingCoordinates(string scenario)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Scenario(scenario)));
        var options = new PdfRenderOptions(16, 16, transparentBackground: true);
        var full = renderer.RenderFrame(0, options);
        var crop = renderer.RenderFrameRegion(0, options, 3, 4, 7, 8);
        for (int y = 0; y < 8; y++)
            for (int x = 0; x < 7; x++)
                for (int channel = 0; channel < 4; channel++)
                    Assert.Equal(full.Pixels[((y + 4) * 16 + x + 3) * 4 + channel],
                        crop.Pixels[(y * 7 + x) * 4 + channel]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FractionalIccReductionAveragesSixteenBitSourceBeforeGamma(bool gray)
    {
        byte[] gamma = [.. "curv"u8, 0, 0, 0, 0, 0, 0, 0, 1, 2, 0];
        var tags = new List<(string Name, byte[] Data)> { ("wtpt", TestIccProfiles.Xyz(TestIccProfiles.D50White)) };
        if (gray) tags.Add(("kTRC", gamma));
        else
        {
            tags.AddRange([("rXYZ", TestIccProfiles.Xyz([63190, 0, 0])),
                ("gXYZ", TestIccProfiles.Xyz([0, 65536, 0])), ("bXYZ", TestIccProfiles.Xyz([0, 0, 54061])),
                ("rTRC", gamma), ("gTRC", gamma), ("bTRC", gamma)]);
        }
        byte[] profile = TestIccProfiles.Profile("mntr", gray ? "GRAY" : "RGB ", "XYZ ", tags);
        string samples = gray ? "40008000C000>" : "400000000000800000000000C00000000000>";
        byte[] pdf = TestIccProfiles.Pdf("<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 1] /Resources << /XObject << /Im 4 0 R >> >> /Contents 6 0 R >>",
            Image(3, 1, "[/ICCBased 5 0 R]", 16, samples),
            TestIccProfiles.Stream(gray ? "/N 1" : "/N 3", profile),
            TestIccProfiles.Stream("", "2 0 0 1 0 0 cm /Im Do"u8.ToArray()));
        var frame = new PdfPageRenderer(PdfDocument.Open(pdf)).RenderFrame(0,
            new PdfRenderOptions(2, 1, transparentBackground: true));
        // Independent gamma-2 ICC/Bradford vectors for encoded mean 32768/65535,
        // multiplied by (2/3)^2 and (4/3)^2 for the two fractional footprints.
        double[] basis = gray ? [.25000924, .25000696, .25000958] : [.75542361, -.23593843, .01734821];
        for (int pixel = 0; pixel < 2; pixel++)
        {
            double factor = pixel == 0 ? 4d / 9 : 16d / 9;
            for (int channel = 0; channel < 3; channel++)
                Assert.InRange(frame.Pixels[pixel * 4 + channel], basis[channel] * factor - 6e-6,
                    basis[channel] * factor + 6e-6);
            Assert.Equal(1f, frame.Pixels[pixel * 4 + 3]);
        }
    }

    [Fact]
    public void FractionalGrayReductionRetainsTheWeightedSourceSample()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("10 0 0 10 0 0 cm",
            Image(3, 1, "/DeviceGray", 8, "0080FF>"))));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1));
        // Each output spans 1.5 source samples. Encoded levels are 128/3 and (128+510)/3,
        // before the independent sRGB transfer into the returned linear frame.
        Assert.InRange(frame.Pixels[0], .023820112244838074 - 1e-7, .023820112244838074 + 1e-7);
        Assert.InRange(frame.Pixels[4], .6630449886483243 - 1e-7, .6630449886483243 + 1e-7);
        Assert.Equal(1f, frame.Pixels[3]);
        Assert.Equal(1f, frame.Pixels[7]);
    }

    // The byte render rounds each averaged sample and each composite to eight bits (half a level
    // each), so a float frame drawing the same samples is within one level of it.
    [Theory]
    [InlineData("bitonal-reduced", 16)]
    [InlineData("bitonal-mildly-reduced", 40)]
    [InlineData("bitonal-enlarged", 40)]
    [InlineData("bitonal-inverted-decode", 16)]
    [InlineData("bitonal-offset", 16)]
    [InlineData("gray-fractional-reduction", 16)]
    [InlineData("rgb-fractional-reduction", 16)]
    [InlineData("stencil-reduced", 16)]
    [InlineData("stencil-enlarged", 40)]
    [InlineData("stencil-one-axis-reduced", 40)]
    [InlineData("explicit-mask", 40)]
    [InlineData("explicit-mask-reduced", 16)]
    [InlineData("soft-mask-other-size", 40)]
    [InlineData("soft-mask-one-bit", 40)]
    [InlineData("soft-mask-reduced", 16)]
    [InlineData("soft-mask-smoothed", 37)]
    public void TheFloatFrameDrawsTheImageAsTheByteRenderDoes(string scenario, int side)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Scenario(scenario)));
        var options = new PdfRenderOptions(side, side);

        PdfRenderedPage page = renderer.Render(0, options);
        PdfRenderedFrame frame = renderer.RenderFrame(0, options);

        ReadOnlySpan<byte> bytes = page.Pixels.Span;
        double worst = 0;
        int partial = 0;
        for (int pixel = 0; pixel < side * side; pixel++)
            for (int channel = 0; channel < 3; channel++)
            {
                double encoded = Encode(frame.Pixels[pixel * 4 + channel]) * 255;
                worst = Math.Max(worst, Math.Abs(encoded - bytes[pixel * 4 + 2 - channel]));
                if (bytes[pixel * 4 + 2 - channel] is not (255 or 0)) partial++;
            }
        // Reduced soft masks add a rounded mask mean before the rounded interpolation
        // and composite. Each contributes at most half a byte level; colors are exact bytes.
        Assert.InRange(worst, 0, scenario == "soft-mask-reduced" ? 1.5 : 1);
        if (scenario == "bitonal-enlarged")
        {
            Assert.Equal(0, partial); // Nearest enlargement preserves the two literal source levels.
            Assert.Contains((byte)0, bytes.ToArray());
            Assert.Contains((byte)255, bytes.ToArray());
        }
        else Assert.True(partial > 0, "The scenario paints no intermediate value.");
    }

    private static double Encode(double linear) => linear <= .0031308
        ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;

    private static byte[] Scenario(string name)
    {
        string bits53x41 = Bits(53, 41, 3);
        string fullPage = "10 0 0 10 0 0 cm";
        return name switch
        {
            "bitonal-reduced" => Page(fullPage, Image(53, 41, "/DeviceGray", 1, bits53x41)),
            "bitonal-mildly-reduced" => Page(fullPage, Image(53, 41, "/DeviceGray", 1, bits53x41)),
            "bitonal-enlarged" => Page(fullPage, Image(7, 5, "/DeviceGray", 1, Bits(7, 5, 5))),
            "bitonal-inverted-decode" => Page(fullPage, Image(53, 41, "/DeviceGray", 1, bits53x41, "/Decode [1 0]")),
            "bitonal-offset" => Page("9.3 0 0 9.1 .37 .41 cm", Image(53, 41, "/DeviceGray", 1, bits53x41)),
            "gray-fractional-reduction" => Page(fullPage, Image(53, 41, "/DeviceGray", 8, Bytes(53 * 41, 7))),
            "rgb-fractional-reduction" => Page(fullPage, Image(53, 41, "/DeviceRGB", 8, Bytes(53 * 41 * 3, 11))),
            "stencil-reduced" => Page(".2 .4 .6 rg " + fullPage, Stencil(53, 41, bits53x41)),
            "stencil-enlarged" => Page(".2 .4 .6 rg " + fullPage, Stencil(7, 5, Bits(7, 5, 5))),
            "stencil-one-axis-reduced" => Page(".2 .4 .6 rg " + fullPage, Stencil(53, 5, Bits(53, 5, 9))),
            "explicit-mask" => Page(fullPage, Image(10, 10, "/DeviceRGB", 8, Bytes(300, 13), "/Mask 5 0 R"),
                Stencil(20, 20, Bits(20, 20, 17))),
            "explicit-mask-reduced" => Page(fullPage, Image(53, 41, "/DeviceRGB", 8, Bytes(53 * 41 * 3, 19), "/Mask 5 0 R"),
                Stencil(53, 41, bits53x41)),
            "soft-mask-other-size" => Page(fullPage, Image(10, 10, "/DeviceRGB", 8, Bytes(300, 13), "/SMask 5 0 R"),
                Image(5, 5, "/DeviceGray", 8, Bytes(25, 23))),
            "soft-mask-one-bit" => Page(fullPage, Image(10, 10, "/DeviceRGB", 8, Bytes(300, 13), "/SMask 5 0 R"),
                Image(20, 20, "/DeviceGray", 1, Bits(20, 20, 29))),
            "soft-mask-reduced" => Page(fullPage, Image(53, 41, "/DeviceRGB", 8, Bytes(53 * 41 * 3, 31), "/SMask 5 0 R"),
                Image(53, 41, "/DeviceGray", 8, Bytes(53 * 41, 37))),
            "soft-mask-smoothed" => Page(fullPage, Image(10, 10, "/DeviceRGB", 8, Bytes(300, 41), "/SMask 5 0 R"),
                Image(10, 10, "/DeviceGray", 8, Bytes(100, 43))),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    // One image XObject (object 4) placed by the page, with an optional mask or soft mask (object 5).
    private static byte[] Page(string placement, string image, string? mask = null)
    {
        string content = $"q {placement} /Im Do Q";
        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /Im 4 0 R >> >> /Contents 6 0 R >>",
            image,
            mask ?? "<< >>",
            PdfPrecisionSoftMaskTests.Stream(content)
        ];
        return PdfPrecisionSoftMaskTests.Pdf(objects);
    }

    private static string Image(int width, int height, string space, int bits, string hex, string extra = "") =>
        $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {space} /BitsPerComponent {bits} {extra} /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream";

    private static string Stencil(int width, int height, string hex) =>
        $"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ImageMask true /Filter /ASCIIHexDecode /Length {hex.Length} >>\nstream\n{hex}\nendstream";

    // Packed 1-bit rows with pseudo-random content, so averages take many intermediate values.
    private static string Bits(int width, int height, int seed)
    {
        var random = new Random(seed);
        int rowBytes = (width + 7) / 8;
        var text = new StringBuilder();
        for (int index = 0; index < rowBytes * height; index++) text.Append(random.Next(256).ToString("X2"));
        return text.Append('>').ToString();
    }

    private static string Bytes(int count, int seed)
    {
        var random = new Random(seed);
        var text = new StringBuilder();
        for (int index = 0; index < count; index++) text.Append(random.Next(256).ToString("X2"));
        return text.Append('>').ToString();
    }
}
