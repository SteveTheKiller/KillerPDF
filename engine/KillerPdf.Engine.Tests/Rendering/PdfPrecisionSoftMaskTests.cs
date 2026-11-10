using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// Graphics-state soft masks (ISO 32000-1 11.6.5.2 and 11.4) in the float renderer behind soft
// proofing and monitor color management. Mask values come from the same mask group render as the
// byte renderer, eight bits per value; the masked paint keeps its float color and composites in float.
public sealed class PdfPrecisionSoftMaskTests
{
    // A gray group painting 0.6 g everywhere has luminosity 0.6, stored as 153 of 255, which is
    // exactly 0.6. Gray 0.5001 at alpha 0.6 over white is 0.5001 * 0.6 + 0.4 = 0.70006 encoded,
    // linear ((0.70006 + 0.055) / 1.055)^2.4, worked with 40 digits.
    [Fact]
    public void ALuminosityMaskScalesAFloatFillByItsGroupLuminosity()
    {
        var document = PdfDocument.Open(Masked("/S /Luminosity", "/DeviceGray", "0.6 g 0 0 10 10 re f",
            ".5001 .5001 .5001 rg 0 0 10 10 re f"));

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(10, 10));

        AssertGray(frame, 5, 5, 0.44807386134265647);
    }

    // A natural-size DeviceRGB image of samples 128 under the same 0.6 mask over white:
    // 128 / 255 * 0.6 + 0.4 encoded, linear 0.44966561570837678 (40 digits).
    [Fact]
    public void ALuminosityMaskScalesAFloatImage()
    {
        string samples = string.Concat(Enumerable.Repeat("80", 300)) + ">";
        var document = PdfDocument.Open(Masked("/S /Luminosity", "/DeviceGray", "0.6 g 0 0 10 10 re f",
            "10 0 0 10 0 0 cm /X Do", "",
            $"<< /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {samples.Length} >>\nstream\n{samples}\nendstream"));

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(10, 10));

        AssertGray(frame, 5, 5, 0.44966561570837678);
    }

    // An isolated and a non-isolated RGB transparency group painted under the 0.6 mask composite as
    // one object at alpha 0.6 (11.4.8): the same 0.70006 encoded result as the plain fill.
    [Theory]
    [InlineData("/I true")]
    [InlineData("")]
    public void ALuminosityMaskScalesATransparencyGroupAsOneObject(string isolation)
    {
        string group = ".5001 .5001 .5001 rg 0 0 10 10 re f";
        var document = PdfDocument.Open(Masked("/S /Luminosity", "/DeviceGray", "0.6 g 0 0 10 10 re f",
            "/X Do", "",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Group << /Type /Group /S /Transparency /CS /DeviceRGB {isolation} >> /Length {group.Length} >>\nstream\n{group}\nendstream"));

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0, new PdfRenderOptions(10, 10));

        AssertGray(frame, 5, 5, 0.44807386134265647);
    }

    // The float frame matches the byte render of the same page. Both read the same eight-bit mask
    // values, so the only differences are the byte path's roundings: the source color quantized to
    // a level (at most 0.5 level, scaled by alpha) and each byte composite rounded (0.5 level).
    // One masked composite over an opaque backdrop is therefore within 1 level; a mask scaling a
    // group adds the group's own composite rounding, so nested cases get 1.5 levels.
    [Theory]
    [InlineData("alpha")]
    [InlineData("luminosity-backdrop")]
    [InlineData("luminosity-transfer")]
    [InlineData("luminosity-backdrop-transfer")]
    [InlineData("alpha-transfer")]
    [InlineData("rgb-luminosity")]
    [InlineData("nested")]
    [InlineData("inside-group")]
    [InlineData("image-soft-mask")]
    [InlineData("masked-image-soft-mask")]
    [InlineData("shading")]
    public void TheFloatFrameMatchesTheByteRenderWithinItsRounding(string scenario)
    {
        var (pdf, tolerance) = Scenario(scenario);
        var renderer = new PdfPageRenderer(PdfDocument.Open(pdf));
        var options = new PdfRenderOptions(40, 40);

        PdfRenderedPage page = renderer.Render(0, options);
        PdfRenderedFrame frame = renderer.RenderFrame(0, options);

        Assert.Empty(page.Diagnostics);
        Assert.Empty(frame.Diagnostics);
        ReadOnlySpan<byte> bytes = page.Pixels.Span;
        double worst = 0, masked = 0;
        for (int pixel = 0; pixel < 40 * 40; pixel++)
        {
            for (int channel = 0; channel < 3; channel++)
            {
                double encoded = Encode(frame.Pixels[pixel * 4 + channel]) * 255;
                worst = Math.Max(worst, Math.Abs(encoded - bytes[pixel * 4 + 2 - channel]));
                if (bytes[pixel * 4 + 2 - channel] is not (255 or 0)) masked++;
            }
        }
        Assert.InRange(worst, 0, tolerance);
        // The scenario really paints something partially masked.
        Assert.True(masked > 0, "No partially masked pixel.");
    }

    private static double Encode(double linear) => linear <= .0031308
        ? 12.92 * linear : 1.055 * Math.Pow(linear, 1 / 2.4) - .055;

    private static (byte[] Pdf, double Tolerance) Scenario(string name)
    {
        const string triangle = "0 0 m 10 0 l 0 10 l f";
        const string paint = ".5001 .2001 .7001 rg 0 0 10 10 re f";
        const string exponential = "/TR << /FunctionType 2 /Domain [0 1] /Range [0 1] /C0 [0] /C1 [1] /N 2 >>";
        string image = string.Concat(Enumerable.Range(0, 100).Select(i => $"{(i * 7) % 256:X2}{(i * 13) % 256:X2}{(i * 29) % 256:X2}")) + ">";
        string alpha = string.Concat(Enumerable.Range(0, 100).Select(i => $"{(i * 2 + 30) % 256:X2}")) + ">";
        string imageObject = $"<< /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceRGB /BitsPerComponent 8 /SMask 8 0 R /Filter /ASCIIHexDecode /Length {image.Length} >>\nstream\n{image}\nendstream";
        string alphaObject = $"<< /Type /XObject /Subtype /Image /Width 10 /Height 10 /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /ASCIIHexDecode /Length {alpha.Length} >>\nstream\n{alpha}\nendstream";
        return name switch
        {
            "alpha" => (Masked("/S /Alpha", "/DeviceGray", triangle, paint), 1),
            "luminosity-backdrop" => (Masked("/S /Luminosity /BC [0.8]", "/DeviceGray", "0.25 g 0 0 5 10 re f", paint), 1),
            "luminosity-transfer" => (Masked("/S /Luminosity " + exponential, "/DeviceGray", "0.7 g " + triangle, paint), 1),
            "luminosity-backdrop-transfer" => (Masked("/S /Luminosity /BC [0.3] " + exponential, "/DeviceGray", "0.9 g 0 0 5 10 re f", paint), 1),
            "alpha-transfer" => (Masked("/S /Alpha " + exponential, "/DeviceGray", triangle, paint), 1),
            "rgb-luminosity" => (Masked("/S /Luminosity /BC [0 0.5 1]", "/DeviceRGB", "1 0 0 rg 0 0 5 10 re f", paint), 1),
            "image-soft-mask" => (Pdf([
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /X 7 0 R >> >> /Contents 6 0 R >>",
                "<< >>", "<< >>",
                Stream("q 10 0 0 10 0 0 cm /X Do Q"),
                imageObject, alphaObject]), 1),
            // The image's own soft mask replaces the graphics-state mask (11.6.5.3), in both renderers.
            "masked-image-soft-mask" => (Masked("/S /Luminosity", "/DeviceGray", "0.6 g " + triangle,
                "10 0 0 10 0 0 cm /X Do", "", imageObject, alphaObject), 1),
            "nested" => (Pdf([
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ExtGState << /M 4 0 R >> >> /Contents 6 0 R >>",
                "<< /Type /ExtGState /SMask << /S /Luminosity /G 5 0 R >> >>",
                FormObject("/DeviceGray", "<< /ExtGState << /N 7 0 R >> >>", "q /N gs 0.9 g 0 0 10 10 re f Q"),
                Stream("q /M gs " + paint + " Q"),
                "<< /Type /ExtGState /SMask << /S /Alpha /G 8 0 R >> >>",
                FormObject("/DeviceGray", "<< >>", triangle)]), 1.5),
            "inside-group" => (Pdf([
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /XObject << /G 4 0 R >> >> /Contents 6 0 R >>",
                FormObject("/DeviceRGB", "<< /ExtGState << /M 7 0 R >> >>", "0.1 0.9 0.2 rg 0 0 10 4 re f q /M gs " + paint + " Q", "/I true"),
                "<< >>",
                Stream("/G Do"),
                "<< /Type /ExtGState /SMask << /S /Luminosity /BC [0.2] /G 8 0 R >> >>",
                FormObject("/DeviceGray", "<< >>", "0.75 g " + triangle)]), 1.5),
            "shading" => (Pdf([
                "<< /Type /Catalog /Pages 2 0 R >>",
                "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
                "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ExtGState << /M 4 0 R >> /Shading << /Sh 7 0 R >> >> /Contents 6 0 R >>",
                "<< /Type /ExtGState /SMask << /S /Luminosity /G 5 0 R >> >>",
                FormObject("/DeviceGray", "<< >>", "0.55 g " + triangle),
                Stream("q /M gs /Sh sh Q"),
                "<< /ShadingType 2 /ColorSpace /DeviceRGB /Coords [0 0 10 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0.1 0.2 0.9] /C1 [0.9 0.6 0.1] /N 1 >> >>"]), 1),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };

        static string FormObject(string space, string resources, string content, string extra = "") =>
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources {resources} /Group << /Type /Group /S /Transparency /CS {space} {extra} >> /Length {content.Length} >>\nstream\n{content}\nendstream";
    }

    // A 40 x 40 float frame reserves 36 bytes a pixel (57,600 bytes). Its full-page mask keeps one
    // byte a pixel (1,600) and renders through a four-byte tile surface plus one-byte tile samples
    // (8,000 more while it renders), so 1,000 spare bytes cannot hold it.
    [Fact]
    public void AMaskThatDoesNotFitBesideTheFrameIsRefusedByTheFrameBudget()
    {
        var document = PdfDocument.Open(Masked("/S /Luminosity", "/DeviceGray", "0.6 g 0 0 10 10 re f",
            ".5001 .5001 .5001 rg 0 0 10 10 re f"));

        var error = Assert.Throws<ArgumentException>(() => new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(40, 40, maximumPixelBytes: 57_600 + 1_000)));

        Assert.Contains("storage limit", error.Message, StringComparison.Ordinal);
    }

    // A converted DjVu page sets one mask per foreground color, each inside q and Q. A restored
    // state can no longer reach its mask, so twenty of them fit the budget one mask needs
    // (57,600 for the frame, 1,600 kept and 8,000 while rendering). Twenty white fills at mask
    // 0.6 over 0.5001 gray leave 1 - 0.4999 * 0.4^20 encoded.
    [Fact]
    public void MasksARestoredStateCannotReachNoLongerCountAgainstTheBudget()
    {
        string paint = ".5001 .5001 .5001 rg 0 0 10 10 re f"
            + string.Concat(Enumerable.Repeat(" q /M gs 1 1 1 rg 0 0 10 10 re f Q", 20));
        var document = PdfDocument.Open(Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ExtGState << /M 4 0 R >> >> /Contents 6 0 R >>",
            "<< /Type /ExtGState /SMask << /S /Luminosity /G 5 0 R >> >>",
            "<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << >> /Group << /Type /Group /S /Transparency /CS /DeviceGray >> /Length 20 >>\nstream\n0.6 g 0 0 10 10 re f\nendstream",
            Stream(paint)]));

        PdfRenderedFrame frame = new PdfPageRenderer(document).RenderFrame(0,
            new PdfRenderOptions(40, 40, maximumPixelBytes: 57_600 + 9_600 + 100));

        double encoded = .5001 * Math.Pow(.4, 20) + (1 - Math.Pow(.4, 20));
        AssertGray(frame, 20, 20, Math.Pow((encoded + .055) / 1.055, 2.4));
    }

    // Proofing converts the composited float page, so a masked page proofs exactly as the page
    // that paints its composited color directly: gray 0.70006 from test one.
    [Fact]
    public void AProofedSoftMaskedPageMatchesTheProofOfItsCompositedColor()
    {
        var proof = new KillerPdf.Engine.ColorManagement.PdfDocumentProofSettings(true,
            KillerPdf.Engine.ColorManagement.PdfColorProfile.Load(
                KillerPdf.Engine.Tests.ColorManagement.SyntheticCmykPrinter.Create()));
        var options = new PdfRenderOptions(10, 10) { Proof = proof };
        var masked = new PdfPageRenderer(PdfDocument.Open(Masked("/S /Luminosity", "/DeviceGray",
            "0.6 g 0 0 10 10 re f", ".5001 .5001 .5001 rg 0 0 10 10 re f"))).RenderFrame(0, options);
        var direct = new PdfPageRenderer(PdfDocument.Open(Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Contents 4 0 R >>",
            Stream(".70006 .70006 .70006 rg 0 0 10 10 re f")]))).RenderFrame(0, options);

        for (int index = 0; index < 4; index++)
            Assert.InRange(masked.Pixels[200 + index], direct.Pixels[200 + index] - .00001f,
                direct.Pixels[200 + index] + .00001f);
    }

    private static void AssertGray(PdfRenderedFrame frame, int x, int y, double expected)
    {
        int offset = (y * frame.Width + x) * 4;
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[offset + channel], expected - .0000005, expected + .0000005);
        Assert.Equal(1f, frame.Pixels[offset + 3]);
    }

    // A 10 x 10 page that paints under the ExtGState /M, whose soft mask is the form 5 0 R.
    // Extra objects are numbered from 7 and may be referenced from the mask entries or contents.
    internal static byte[] Masked(string maskEntries, string groupSpace, string maskContent, string paint,
        string groupExtra = "", params string[] extra)
    {
        string xobjects = extra.Length > 0 ? "/XObject << /X 7 0 R >>" : "";
        return Pdf([
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 10 10] /Resources << /ExtGState << /M 4 0 R >> {xobjects} >> /Contents 6 0 R >>",
            $"<< /Type /ExtGState /SMask << {maskEntries} /G 5 0 R >> >>",
            $"<< /Type /XObject /Subtype /Form /BBox [0 0 10 10] /Resources << {xobjects} >> /Group << /Type /Group /S /Transparency /CS {groupSpace} {groupExtra} >> /Length {maskContent.Length} >>\nstream\n{maskContent}\nendstream",
            Stream($"q /M gs {paint} Q"),
            .. extra
        ]);
    }

    internal static string Stream(string content) => $"<< /Length {content.Length} >>\nstream\n{content}\nendstream";

    internal static byte[] Pdf(IReadOnlyList<string> objects)
    {
        var text = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Count; index++)
        {
            offsets.Add(text.Length);
            text.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = text.Length;
        text.Append($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) text.Append($"{offset:D10} 00000 n \n");
        text.Append($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
