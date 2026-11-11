using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfPrecisionOverprintProofTests
{
    private const string OpaqueContent = "1 .6 .25 .5 k 0 0 1 1 re f q /Over gs .00001 0 .37 0 k 0 0 1 1 re f Q "
        + ".00001 .6 .37 .5 k 1 0 1 1 re f";
    private const string AlphaContent = "q /Half gs 1 0 0 0 k 0 0 1 1 re f /Over gs 0 1 0 0 k 0 0 1 1 re f Q "
        + "q /Three gs .6666666666666666 .6666666666666666 0 0 k 1 0 1 1 re f Q";
    private static readonly PdfColorProfile AllIntentPrinter = Printer();

    public static IEnumerable<object[]> ProofControls()
    {
        foreach (var intent in new[] { PdfRenderingIntent.Perceptual, PdfRenderingIntent.RelativeColorimetric,
            PdfRenderingIntent.Saturation, PdfRenderingIntent.AbsoluteColorimetric })
            foreach (bool paper in new[] { false, true })
                foreach (bool black in new[] { false, true })
                    foreach (bool compensation in new[] { false, true })
                        foreach (bool alpha in new[] { false, true })
                            yield return [intent, paper, black, compensation, alpha];
    }

    [Theory]
    [MemberData(nameof(ProofControls))]
    public void AllActualIntentsAndProofControlsUseTheComposedSourceAppearance(PdfRenderingIntent intent,
        bool paper, bool black, bool compensation, bool alpha)
    {
        Assert.False(AllIntentPrinter.GetTransformSelection(intent,
            PdfColorTransformDirection.ConnectionToDevice).UsesIntentFallback);
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(alpha ? AlphaContent : OpaqueContent)));
        var options = new PdfRenderOptions(2, 1, transparentBackground: alpha)
        {
            Proof = new PdfDocumentProofSettings(true, AllIntentPrinter,
                new PdfProofOptions { RenderingIntent = intent, SimulatePaper = paper, SimulateBlack = black,
                    BlackPointCompensation = compensation }, gamutWarnings: true)
        };
        var frame = renderer.RenderFrame(0, options);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f,
                frame.Pixels[4 + channel] + 1e-7f);
        Assert.Equal(alpha ? .75f : 1f, frame.Pixels[3]);
        Assert.NotNull(frame.PrinterGamutWarnings);
        Assert.Equal(AllIntentPrinter.ContentSha256, frame.PrinterGamutWarnings.PrinterProfileSha256);
        Assert.Equal(frame.PrinterGamutWarnings.GetClassification(0, 0), frame.PrinterGamutWarnings.GetClassification(1, 0));
        Assert.Equal(PdfCmykViewingDefault.LoadProfile().ContentSha256, frame.AssumedCmykProfileSha256);
        Assert.NotEqual(AllIntentPrinter.ContentSha256, frame.AssumedCmykProfileSha256);
        var crop = renderer.RenderFrameRegion(0, options, 0, 0, 1, 1);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), crop.Pixels.ToArray());
        Assert.Equal(frame.PrinterGamutWarnings.GetClassification(0, 0), crop.PrinterGamutWarnings!.GetClassification(0, 0));
    }

    [Fact]
    public void NativeAndGamutStorageUseTheirExactFullAndCropBoundaries()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(OpaqueContent)));
        var proof = new PdfDocumentProofSettings(true, AllIntentPrinter, gamutWarnings: true);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 107) { Proof = proof }));
        Assert.NotNull(renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 108) { Proof = proof }).PrinterGamutWarnings);
        Assert.Throws<ArgumentException>(() => renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 53) { Proof = proof }, 0, 0, 1, 1));
        Assert.NotNull(renderer.RenderFrameRegion(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 54) { Proof = proof }, 0, 0, 1, 1).PrinterGamutWarnings);
        var noWarnings = new PdfDocumentProofSettings(true, AllIntentPrinter);
        Assert.NotNull(renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 104) { Proof = noWarnings }));
        Assert.Throws<ArgumentException>(() => renderer.RenderFrame(0,
            new PdfRenderOptions(2, 1, maximumPixelBytes: 103) { Proof = noWarnings }));
    }

    [Fact]
    public void DisabledProofAndLaterPlainRenderingPreserveTheOriginalNativeOutput()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(OpaqueContent)));
        var plain = new PdfRenderOptions(2, 1);
        var before = renderer.RenderFrame(0, plain);
        renderer.RenderFrame(0, plain with { Proof = new PdfDocumentProofSettings(true, AllIntentPrinter) });
        var after = renderer.RenderFrame(0, plain);
        var disabled = renderer.RenderFrame(0, plain with { Proof = new PdfDocumentProofSettings(false, AllIntentPrinter,
            gamutWarnings: true) });
        Assert.Equal(before.Pixels.ToArray(), after.Pixels.ToArray());
        Assert.Equal(before.Pixels.ToArray(), disabled.Pixels.ToArray());
        Assert.Null(disabled.PrinterGamutWarnings);
    }

    [Fact]
    public void CancellationPreservesTheNextProofRenderAndPrintingRemainsRefused()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(OpaqueContent)));
        var options = new PdfRenderOptions(2, 1, maximumPixelBytes: 108)
            { Proof = new PdfDocumentProofSettings(true, AllIntentPrinter, gamutWarnings: true) };
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => renderer.RenderFrame(0, options, canceled.Token));
        Assert.NotNull(renderer.RenderFrame(0, options).PrinterGamutWarnings);
        Assert.Throws<NotSupportedException>(() => renderer.RenderFrame(0, options with { ForPrinting = true }));
    }

    [Fact]
    public void ZeroAlphaIsNotGamutEvaluated()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page("/Zero gs /Over gs 1 0 0 0 k 0 0 1 1 re f")));
        var frame = renderer.RenderFrame(0, new PdfRenderOptions(2, 1, transparentBackground: true)
            { Proof = new PdfDocumentProofSettings(true, AllIntentPrinter, gamutWarnings: true) });
        Assert.Equal(0f, frame.Pixels[3]);
        Assert.Equal(PdfGamutClassification.NotEvaluated, frame.PrinterGamutWarnings!.GetClassification(0, 0));
        Assert.Equal(PdfGamutClassification.NotEvaluated, frame.PrinterGamutWarnings.GetClassification(1, 0));
    }

    [Fact]
    public void OrdinaryCmykWithoutOverprintAndMixedRgbSourcesRemainRefusedForProof()
    {
        var options = new PdfRenderOptions(2, 1) { Proof = new PdfDocumentProofSettings(true, AllIntentPrinter) };
        var ordinary = new PdfPageRenderer(PdfDocument.Open(Page(".2 .3 .4 .5 k 0 0 2 1 re f")));
        Assert.Throws<NotSupportedException>(() => ordinary.RenderFrame(0, options));
        var mixed = new PdfPageRenderer(PdfDocument.Open(Page("1 0 0 rg 1 0 1 1 re f /Over gs 1 0 0 0 k 0 0 1 1 re f")));
        Assert.Throws<NotSupportedException>(() => mixed.RenderFrame(0, options));
        Assert.Throws<NotSupportedException>(() => mixed.RenderFrameRegion(0, options, 0, 0, 1, 1));
    }

    [Fact]
    public void ProofConvertsComposedNativePlatesAndMatchesTheLiteralControlAndCrop()
    {
        var renderer = new PdfPageRenderer(PdfDocument.Open(Page(
            "1 .6 .25 .5 k 0 0 1 1 re f q /Over gs .00001 0 .37 0 k 0 0 1 1 re f Q "
            + ".00001 .6 .37 .5 k 1 0 1 1 re f")));
        var options = new PdfRenderOptions(2, 1)
        {
            Proof = new PdfDocumentProofSettings(true, PdfProofPaperWhiteTests.MisalignedPrinter(false),
                new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric,
                    BlackPointCompensation = false, SimulatePaper = false, SimulateBlack = true })
        };
        var frame = renderer.RenderFrame(0, options);
        for (int channel = 0; channel < 4; channel++)
            Assert.InRange(frame.Pixels[channel], frame.Pixels[4 + channel] - 1e-7f,
                frame.Pixels[4 + channel] + 1e-7f);
        var crop = renderer.RenderFrameRegion(0, options, 0, 0, 1, 1);
        Assert.Equal(frame.Pixels.Slice(0, 4).ToArray(), crop.Pixels.ToArray());
    }

    private static byte[] Page(string content) => TestIccProfiles.Pdf(
        "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 2 1] /Resources << /ExtGState << /Over 4 0 R /Half 6 0 R /Three 7 0 R /Zero 8 0 R >> >> /Contents 5 0 R >>",
        "<< /op true /OPM 1 >>", TestIccProfiles.Stream("", Encoding.ASCII.GetBytes(content)),
        "<< /ca .5 >>", "<< /ca .75 >>", "<< /ca 0 >>");

    // The existing paper-white fixture's aligned endpoint, with an actual pair for every
    // intent. Repeating identical tables tests selection without introducing fallback.
    private static PdfColorProfile Printer()
    {
        byte[] forward = Table(4, 3, cell => (cell % 3) switch
            { 0 => [31595, 32768, 27030], 1 => [15798, 16384, 13515], _ => [3160, 3277, 2703] });
        byte[] reverse = Table(3, 4, cell => [0, 0, 0,
            ((cell / 3) % 3) switch { 0 => (ushort)65535, 1 => (ushort)1311, _ => (ushort)0 }]);
        return PdfColorProfile.Load(TestIccProfiles.Profile("prtr", "CMYK", "XYZ ",
            [("A2B0", forward), ("B2A0", reverse), ("A2B1", forward), ("B2A1", reverse),
                ("A2B2", forward), ("B2A2", reverse), ("wtpt", TestIccProfiles.Xyz([50551, 52429, 43248]))]));
    }

    private static byte[] Table(int inputs, int outputs, Func<int, ushort[]> vertex)
    {
        int cells = (int)Math.Pow(3, inputs);
        byte[] data = new byte[52 + 4 * inputs + 2 * cells * outputs + 4 * outputs];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs; data[9] = (byte)outputs; data[10] = 3;
        for (int axis = 0; axis < 3; axis++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + axis * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), 2);
        int offset = 52;
        void Append(ushort value) { BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value); offset += 2; }
        for (int channel = 0; channel < inputs; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < cells; cell++) foreach (ushort value in vertex(cell)) Append(value);
        for (int channel = 0; channel < outputs; channel++) { Append(0); Append(65535); }
        return data;
    }
}
