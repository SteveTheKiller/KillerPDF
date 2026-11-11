using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionOverprintStrokeTests
{
    public static IEnumerable<object[]> CombinedControls()
    {
        foreach (string operation in new[] { "B", "B*", "b", "b*" })
            foreach (bool stroke in new[] { false, true })
                foreach (bool fill in new[] { false, true })
                    foreach (int mode in new[] { 0, 1 })
                        foreach (bool proof in new[] { false, true })
                            if (!proof || stroke || fill) yield return [operation, stroke, fill, mode, proof];
    }

    [Theory]
    [MemberData(nameof(CombinedControls))]
    public void CombinedOperatorsHonorIndependentFlagsAndLiteralInnerOuterControls(string operation,
        bool stroke, bool fill, int mode, bool proof)
    {
        string center = fill && mode == 1 ? "1 .2 .25 .1" : "0 .2 0 .1";
        string inner = stroke && mode == 1 ? ".00001 .2 .37 .1" : ".00001 0 .37 0";
        string outer = stroke && mode == 1 ? ".00001 .6 .37 .5" : ".00001 0 .37 0";
        string content = $"1 .6 .25 .5 k 0 0 12 6 re f q /Over gs 0 .2 0 .1 k .00001 0 .37 0 K 2 w 1 1 4 4 re {operation} Q "
            + $"{center} k 9 3 1 1 re f {inner} k 7 3 1 1 re f {outer} k 6 3 1 1 re f";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, 12, 6,
            $"/OP {Flag(stroke)} /op {Flag(fill)} /OPM {mode}")));
        var options = Options(12, 6, proof);
        var frame = renderer.RenderFrame(0, options);
        foreach (int x in new[] { 3, 1, 0 }) Compare(frame, x, 2, x + 6, 2);
        var crop = renderer.RenderFrameRegion(0, options, 0, 2, 4, 1);
        for (int x = 0; x < 4; x++)
            Assert.Equal(frame.Pixels.Slice((2 * 12 + x) * 4, 4).ToArray(), crop.Pixels.Slice(x * 4, 4).ToArray());
        if (proof) Assert.Equal(frame.PrinterGamutWarnings!.GetClassification(1, 2),
            crop.PrinterGamutWarnings!.GetClassification(1, 0));
    }

    [Theory]
    [InlineData(false, true, 1, ".00001", ".00001 0 .37 0")]
    [InlineData(true, false, 0, ".00001", ".00001 0 .37 0")]
    [InlineData(true, false, 1, "0", "1 .6 .37 .5")]
    [InlineData(true, false, 1, ".00001", ".00001 .6 .37 .5")]
    [InlineData(true, true, 1, ".00001", ".00001 .6 .37 .5")]
    [InlineData(true, false, 1, ".00000000000000000000000000000000000000000000000001", ".00000000000000000000000000000000000000000000000001 .6 .37 .5")]
    public void StrokeFlagsModesAndOriginalPositiveComponentsMatchLiteralStrips(bool stroke, bool fill,
        int mode, string cyan, string literal)
    {
        string content = "1 .6 .25 .5 k 0 0 2 3 re f q /Over gs "
            + $"1 .6 .25 .5 k 0 0 2 3 re f {cyan} 0 .37 0 K 1 w 0 1.5 m 2 1.5 l S Q "
            + $"{literal} K 1 w 2 1.5 m 4 1.5 l S";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content, settings:
            $"/OP {Flag(stroke)} /op {Flag(fill)} /OPM {mode}")));
        Compare(renderer.RenderFrame(0, new PdfRenderOptions(4, 3)), 0, 1, 2, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrokeOpacityCompositesNativeAlphaAndProofAfterOverlap(bool proof)
    {
        const string content = "q /Half gs 1 0 0 0 K 1 w 0 1.5 m 2 1.5 l S /Over gs 0 1 0 0 K 0 1.5 m 2 1.5 l S Q "
            + "q /Three gs .6666666666666666 .6666666666666666 0 0 K 1 w 2 1.5 m 4 1.5 l S Q";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content)));
        var frame = renderer.RenderFrame(0, Options(4, 3, proof, transparent: true));
        Compare(frame, 0, 1, 2, 1);
        Assert.Equal(.75f, frame.Pixels[(1 * 4) * 4 + 3]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StrokeEdgeCoverageCompositesNativePlatesWithoutByteRounding(bool proof)
    {
        const string content = "1 0 0 0 K 1.5 w 0 1.5 m 2 1.5 l S q /Over gs 0 1 0 0 K 0 1.5 m 2 1.5 l S Q "
            + "q /Edge gs .5717488789237668 .5717488789237668 0 0 K 1 w 2 .5 m 4 .5 l S Q";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(content,
            extraStates: "/Edge 9 0 R", extraObject: "<< /CA .4389696270665129 >>")));
        var frame = renderer.RenderFrame(0, Options(4, 3, proof, transparent: true));
        // A quarter-pixel stroke edge has coverage64/255. Ao=1-(191/255)^2 and C=M=255/446.
        Compare(frame, 0, 0, 2, 2);
    }

    [Theory]
    [InlineData("B")]
    [InlineData("B*")]
    [InlineData("s")]
    public void ClosingFormsMatchAnExplicitlyClosedPath(string operation)
    {
        string close = operation == "s" ? "s" : operation == "B" ? "b" : "b*";
        string content = "1 .6 .25 .5 k 0 0 8 4 re f /Over gs .2 .3 .4 .5 k .00001 0 .37 0 K 1 w "
            + $"1 1 m 3 1 l 3 3 l 1 3 l {close} 5 1 m 7 1 l 7 3 l 5 3 l h {operation}";
        var frame = new PdfPageRenderer(PdfDocument.Open(Page(content, 8, 4))).RenderFrame(0, new PdfRenderOptions(8, 4));
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) Compare(frame, x, y, x + 4, y);
    }

    [Theory]
    [InlineData("1 0 0 rg .2 .3 .4 .5 K 1 1 2 1 re B")]
    [InlineData(".2 .3 .4 .5 k 1 0 0 RG 1 1 2 1 re B")]
    [InlineData("1 0 0 RG 1 w 0 1.5 m 4 1.5 l S")]
    public void ActualMixedStrokeOrFillSourceRemainsRefused(string paint)
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs " + paint)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 3)));
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrameRegion(0, new PdfRenderOptions(4, 3), 0, 0, 1, 1));
    }

    [Fact]
    public void UnpaintedOppositeColorAndSavedStateDoNotContaminateQualification()
    {
        const string stroke = ".2 .3 .4 .5 K q 1 0 0 RG Q /Over gs 1 w 0 1.5 m 4 1.5 l S";
        var expected = new PdfPageRenderer(PdfDocument.Open(Page(stroke))).RenderFrame(0, new PdfRenderOptions(4, 3));
        var actual = new PdfPageRenderer(PdfDocument.Open(Page("1 0 0 rg " + stroke))).RenderFrame(0, new PdfRenderOptions(4, 3));
        Assert.Equal(expected.Pixels.ToArray(), actual.Pixels.ToArray());
        const string fill = "/Over gs .2 .3 .4 .5 k 0 0 4 3 re f";
        var fillExpected = new PdfPageRenderer(PdfDocument.Open(Page(fill, settings: "/OP false /op true /OPM 1")))
            .RenderFrame(0, new PdfRenderOptions(4, 3));
        var fillActual = new PdfPageRenderer(PdfDocument.Open(Page("1 0 0 RG " + fill, settings: "/OP false /op true /OPM 1")))
            .RenderFrame(0, new PdfRenderOptions(4, 3));
        Assert.Equal(fillExpected.Pixels.ToArray(), fillActual.Pixels.ToArray());
    }

    [Fact]
    public void HiddenStrokeAndCombinedPaintKeepVisibleRgbAndOriginalStorage()
    {
        const string visible = "1 0 0 rg 0 0 4 3 re f ";
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(visible
            + "/OC /Hidden BDC /Over gs .2 .3 .4 .5 K 1 w 0 1.5 m 4 1.5 l S 1 1 2 1 re B EMC",
            resources: "/Properties << /Hidden 9 0 R >>", extraObject: "<< /Type /OCG /Name (Hidden) >>",
            catalog: "/OCProperties << /OCGs [9 0 R] /D << /OFF [9 0 R] >> >>")));
        var options = new PdfRenderOptions(4, 3, maximumPixelBytes: 432);
        var expected = new PdfPageRenderer(PdfDocument.Open(Page(visible))).RenderFrame(0, options);
        Assert.Equal(expected.Pixels.ToArray(), renderer.RenderFrame(0, options).Pixels.ToArray());
    }

    [Fact]
    public void RestoringNormalBlendBeforeStrokePreservesTheReviewedBehavior()
    {
        const string paint = ".2 .3 .4 .5 K 1 w 0 1.5 m 4 1.5 l S";
        var expected = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs " + paint))).RenderFrame(0, new PdfRenderOptions(4, 3));
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs /Reset gs " + paint,
            settings: "/OP true /op false /OPM 1 /BM /Multiply")));
        Assert.Equal(expected.Pixels.ToArray(), renderer.RenderFrame(0, new PdfRenderOptions(4, 3)).Pixels.ToArray());
        var unsupported = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs " + paint,
            settings: "/OP true /op false /OPM 1 /BM /Multiply")));
        Assert.Throws<NotSupportedException>(() => unsupported.RenderFrame(0, new PdfRenderOptions(4, 3)));
    }

    [Fact]
    public void NativeStrokeStorageAndCancellationKeepTheExistingBudget()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Over gs .2 .3 .4 .5 K 1 w 0 1.5 m 4 1.5 l S")));
        var plain = new PdfRenderOptions(4, 3, maximumPixelBytes: 624);
        Assert.Equal(48, renderer.RenderFrame(0, plain).Pixels.Length);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, new PdfRenderOptions(4, 3, maximumPixelBytes: 623)));
        var proof = Options(4, 3, true, maximum: 648);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0, plain, canceled.Token));
        Assert.NotNull(renderer.RenderFrame(0, proof).PrinterGamutWarnings);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0, Options(4, 3, true, maximum: 647)));
        Assert.NotNull(renderer.RenderFrameRegion(0, Options(4, 3, true, maximum: 108), 0, 1, 2, 1).PrinterGamutWarnings);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            Options(4, 3, true, maximum: 107), 0, 1, 2, 1));
    }

    [Fact]
    public void CombinedPaintKeepsIndependentFillAndStrokePlateSemantics()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(
            "1 .6 .25 .5 k 0 0 12 6 re f q /Over gs 0 .2 0 .1 k .00001 0 .37 0 K 2 w 1 1 4 4 re B Q "
            + "0 .2 0 .1 k 9 3 1 1 re f .00001 .2 .37 .1 k 7 3 1 1 re f .00001 .6 .37 .5 k 6 3 1 1 re f", 12, 6)));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(12, 6));
        // Center fill, inner stroke over the new fill, outer stroke over the old backdrop.
        foreach (int x in new[] { 3, 1, 0 })
            for (int channel = 0; channel < 4; channel++)
                Assert.InRange(frame.Pixels[(2 * 12 + x) * 4 + channel],
                    frame.Pixels[(2 * 12 + x + 6) * 4 + channel] - 1e-7f,
                    frame.Pixels[(2 * 12 + x + 6) * 4 + channel] + 1e-7f);
    }

    [Fact]
    public void StrokeOpmOnePreservesExactZeroButReplacesTinyPositiveCyan()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(
            "1 .6 .25 .5 k 0 0 2 3 re f q /Over gs .00001 0 .37 0 K 1 w 0 1.5 m 2 1.5 l S Q "
            + ".00001 .6 .37 .5 K 1 w 2 1.5 m 4 1.5 l S")));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(4, 3));
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[4 * 4 + channel], frame.Pixels[6 * 4 + channel] - 1e-7f,
                frame.Pixels[6 * 4 + channel] + 1e-7f);
    }

    private static string Flag(bool value) => value ? "true" : "false";
    private static PdfRenderOptions Options(int width, int height, bool proof, bool transparent = false,
        long maximum = PdfRenderOptions.MaximumPixelBytes) => new(width, height, transparentBackground: transparent,
            maximumPixelBytes: maximum)
    {
        Proof = proof ? new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(true),
            new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric, BlackPointCompensation = false,
                SimulateBlack = true }, gamutWarnings: true) : null
    };
    private static void Compare(PdfRenderedFrame frame, int x, int y, int expectedX, int expectedY)
    {
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[(y * frame.Width + x) * 4 + channel],
                frame.Pixels[(expectedY * frame.Width + expectedX) * 4 + channel] - 1e-7f,
                frame.Pixels[(expectedY * frame.Width + expectedX) * 4 + channel] + 1e-7f);
    }
    private static byte[] Page(string content, int width = 4, int height = 3,
        string settings = "/OP true /op false /OPM 1", string resources = "", string extraStates = "",
        string extraObject = "<< >>", string catalog = "") => TestIccProfiles.Pdf(
        $"<< /Type /Catalog /Pages 2 0 R {catalog} >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {width} {height}] /Resources << {resources} /ExtGState << /Over 4 0 R /Half 6 0 R /Three 7 0 R /Reset 8 0 R {extraStates} >> >> /Contents 5 0 R >>",
        $"<< {settings} >>", TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content)),
        "<< /CA .5 /ca .5 >>", "<< /CA .75 /ca .75 >>", "<< /BM /Normal >>", extraObject);
}
