using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionOverprintTests
{
    [Theory]
    [InlineData("Normal")]
    [InlineData("Compatible")]
    public void ResetBlendModeBeforePaintingRestoresNativeEligibility(string blendMode)
    {
        byte[] Document(string prefix) => TestIccProfiles.Pdf(
            "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 1] /Resources << /ExtGState << /Over << /op true /OPM 1 >> /Mul << /BM /Multiply >> /Reset << /BM /" + blendMode + " >> >> >> /Contents 4 0 R >>",
            TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(prefix + "/Over gs 1 0 0 0 k 0 0 2 1 re f")));
        var options = new PdfRenderOptions(2, 1);
        var expected = new PdfPageRenderer(PdfDocument.Open(Document(""))).RenderFrame(0, options);
        var renderer = new PdfPageRenderer(PdfDocument.Open(Document("/Mul gs /Reset gs ")));
        Assert.Equal(expected.Pixels.ToArray(), renderer.RenderFrame(0, options).Pixels.ToArray());
        Assert.Equal(expected.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0, options, 0, 0, 1, 1).Pixels.ToArray());
    }

    [Fact]
    public void HiddenOverprintDoesNotChangeVisibleRgbOrItsStorageBoundary()
    {
        const string visible = "1 0 0 rg 0 0 2 1 re f ";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(visible
            + "/OC /Hidden BDC /Over gs 1 0 0 0 k 0 0 1 1 re f EMC", true, 1,
            resources: "/Properties << /Hidden 8 0 R >>", extraObject: "<< /Type /OCG /Name (Hidden) >>",
            catalogEntries: "/OCProperties << /OCGs [8 0 R] /D << /OFF [8 0 R] >> >>")));
        var options = new PdfRenderOptions(2, 1, maximumPixelBytes: 72);
        var expected = new PdfPageRenderer(PdfDocument.Open(Page(visible, true, 1))).RenderFrame(0, options);
        Assert.Equal(expected.Pixels.ToArray(), renderer.RenderFrame(0, options).Pixels.ToArray());
        Assert.Equal(expected.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 36), 0, 0, 1, 1).Pixels.ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultAndOutputIntentSourcesUseTheirExplicitBinding(bool outputIntent)
    {
        // Existing synthetic affine XYZ CMYK source fixture with a colorimetric forward table.
        byte[] profile = Convert.FromBase64String("AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=");
        string resources = outputIntent ? "" : "/ColorSpace << /DefaultCMYK [/ICCBased 8 0 R] >>";
        string catalog = outputIntent ? "/OutputIntents [<< /Type /OutputIntent /S /GTS_PDFX /OutputConditionIdentifier (Synthetic) /DestOutputProfile 8 0 R >>]" : "";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k 0 0 1 1 re f", true, 1,
            resources: resources, extraObject: TestIccProfiles.Stream("/N 4", profile), catalogEntries: catalog)));
        var options = new PdfRenderOptions(2, 1);
        var frame = renderer.RenderFrame(0, options);
        Assert.Null(frame.AssumedCmykProfileSha256);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0, options, 0, 0, 1, 1).Pixels.ToArray());
    }

    [Fact]
    public void AntialiasedEdgesCompositeTheirFractionalNativeCoverage()
    {
        const string content = "1 0 0 0 k 0 0 1.5 1 re f /Over gs 0 1 0 0 k 0 0 1.5 1 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, true, 1)));
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);
        var frame = renderer.RenderFrame(0, options);
        // Half-edge raster coverage is 128/255. Ao=1-(127/255)^2 and straight C=M=255/382.
        var control = new PdfPageRenderer(PdfDocument.Open(Page(
            "/Over gs .6675392670157068 .6675392670157068 0 0 k 0 0 2 1 re f", false, 1,
            "/ca .7519569396386005"))).RenderFrame(0, options);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[4 + channel], control.Pixels[channel] - 1e-7f, control.Pixels[channel] + 1e-7f);
    }

    [Theory]
    [InlineData("/Spot cs .5 scn 0 0 1 1 re f", "/ColorSpace << /Spot [/Separation /Ink /DeviceCMYK << /FunctionType 2 /Domain [0 1] /C0 [0 0 0 0] /C1 [1 0 0 0] /N 1 >>] >>", "")]
    [InlineData("/Im Do", "/XObject << /Im 8 0 R >>", "<< /Subtype /Image /Width 1 /Height 1 /ColorSpace /DeviceCMYK /BitsPerComponent 8 /Length 4 >>\nstream\n1234\nendstream")]
    [InlineData("/Form Do", "/XObject << /Form 8 0 R >>", "<< /Subtype /Form /BBox [0 0 1 1] /Group << /S /Transparency /CS /DeviceRGB >> /Length 0 >>\nstream\n\nendstream")]
    public void NamedPaintImagesAndGroupsRemainExplicitlyRefused(string paint, string resources, string extra)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k " + paint, true, 1,
            resources: resources, extraObject: extra)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void PageGroupRemainsExplicitlyRefused()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k 0 0 1 1 re f", true, 1,
            pageEntries: "/Group << /S /Transparency /CS /DeviceRGB >>")));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Theory]
    [InlineData(false, 0, ".00001", ".00001 0 .37 0")]
    [InlineData(false, 1, "0", "0 0 .37 0")]
    [InlineData(true, 0, ".00001", ".00001 0 .37 0")]
    [InlineData(true, 0, "0", "0 0 .37 0")]
    [InlineData(true, 1, "0", "1 .6 .37 .5")]
    [InlineData(true, 1, ".00001", ".00001 .6 .37 .5")]
    [InlineData(true, 1, ".00000000000000000000000000000000000000000000000001", ".00000000000000000000000000000000000000000000000001 .6 .37 .5")]
    public void ProcessModesMatchIndependentLiteralPlateResults(bool enabled, int mode,
        string cyan, string expected)
    {
        string content = "1 .6 .25 .5 k 0 0 1 1 re f "
            + $"q /Over gs {cyan} 0 .37 0 k 0 0 1 1 re f Q {expected} k 1 0 1 1 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, enabled, mode)));
        var options = new PdfRenderOptions(2, 1);
        var frame = renderer.RenderFrame(0, options);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f,
                frame.Pixels[4 + channel] + 1e-7f);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), renderer.RenderFrameRegion(0, options, 0, 0, 1, 1).Pixels.ToArray());
    }

    [Fact]
    public void ProcessStorageIsChargedBeforeAllocationAndUsesTheCroppedPixelCount()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k 0 0 2 1 re f", true, 1)));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 103)));
        Assert.Equal(8, renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 104)).Pixels.Length);
        Assert.Equal(4, renderer.RenderFrameRegion(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 52), 0, 0, 1, 1).Pixels.Length);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 51), 0, 0, 1, 1));
    }

    [Fact]
    public void PageWithoutActualOverprintRetainsItsOriginalStorageBoundary()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("1 0 0 0 k 0 0 2 1 re f", true, 1)));
        Assert.Equal(8, renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 72)).Pixels.Length);
    }

    [Fact]
    public void CancellationDoesNotLeaveTheNextRenderWithAReservation()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k 0 0 2 1 re f", true, 1)));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 104), canceled.Token));
        Assert.Equal(8, renderer.RenderFrame(0, new PdfRenderOptions(2, 1, maximumPixelBytes: 104)).Pixels.Length);
    }

    [Theory]
    [InlineData("1 0 0 rg 0 0 1 1 re f ", "")]
    [InlineData("", " 1 0 0 rg 1 0 1 1 re f")]
    public void MixedRgbPaintIsRefusedIndependentlyOfTheCrop(string prefix, string suffix)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(prefix
            + "/Over gs 1 0 0 0 k 0 0 1 1 re f" + suffix, true, 1)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1), 0, 0, 1, 1));
    }

    [Theory]
    [InlineData("/BM /Multiply")]
    [InlineData("/AIS true")]
    [InlineData("/TK false")]
    public void UnsupportedGraphicsSemanticsRemainExplicitlyRefused(string settings)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs 1 0 0 0 k 0 0 1 1 re f", true, 1, settings)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(2, 1)));
    }

    [Fact]
    public void TransparentOverlapCompositesPreservedPlatesInsteadOfSkippingTheirWrites()
    {
        const string content = "q /Half gs 1 0 0 0 k 0 0 1 1 re f "
            + "/Over gs 0 1 0 0 k 0 0 1 1 re f Q "
            + "q /Three gs .6666666666666666 .6666666666666666 0 0 k 1 0 1 1 re f Q";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, true, 1)));
        var options = new PdfRenderOptions(2, 1, transparentBackground: true);
        var frame = renderer.RenderFrame(0, options);
        Assert.Equal(.75f, frame.Pixels[3]);
        Assert.Equal(.75f, frame.Pixels[7]);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f,
                frame.Pixels[4 + channel] + 1e-7f);
    }

    [Fact]
    public void OpmOnePreservesExactZeroComponentsButReplacesTinyPositiveCyan()
    {
        const string content = "1 .6 .25 .5 k 0 0 1 1 re f "
            + "q /Over gs .00001 0 .37 0 k 0 0 1 1 re f Q "
            + ".00001 .6 .37 .5 k 1 0 1 1 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, true, 1)));
        var options = new PdfRenderOptions(2, 1);

        var frame = renderer.RenderFrame(0, options);

        // ISO32000-1 8.6.7: only exact zero CMYK current-color components are
        // unspecified in OPM1. The right strip is the independent literal plate result.
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f,
                frame.Pixels[4 + channel] + 1e-7f);
        var crop = renderer.RenderFrameRegion(0, options, 0, 0, 1, 1);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), crop.Pixels.ToArray());
    }

    private static byte[] Page(string content, bool enabled, int mode, string settings = "",
        string resources = "", string pageEntries = "", string extraObject = "<< >>",
        string catalogEntries = "") => TestIccProfiles.Pdf(
        $"<< /Type /Catalog /Pages 2 0 R {catalogEntries} >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 1] {pageEntries} /Resources << {resources} /ExtGState << /Over 4 0 R /Half 6 0 R /Three 7 0 R >> >> /Contents 5 0 R >>",
        $"<< /Type /ExtGState /op {(enabled ? "true" : "false")} /OPM {mode} {settings} >>",
        TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content)),
        "<< /Type /ExtGState /ca .5 >>", "<< /Type /ExtGState /ca .75 >>", extraObject);
}
