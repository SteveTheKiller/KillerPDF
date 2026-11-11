using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfNativeFrameProofTests
{
    [Fact]
    public void RunsOfOneColorRunThePrinterRoundTripOnceAndKeepTheirOutput()
    {
        // Colors by run: white x5, ink x9 (alpha varies, one transparent pixel inside), white x3, ink x2.
        // The transparent pixel is skipped, so the ink run continues across it: four runs of color in all.
        float[][] colors = [[1, 1, 1], [.2f, .3f, .4f], [.2f, .3f, .4f], [1, 1, 1], [.2f, .3f, .4f]];
        int[] lengths = [5, 7, 2, 3, 2];
        var pixels = new List<float>();
        for (int run = 0; run < colors.Length; run++)
            for (int count = 0; count < lengths[run]; count++)
            {
                pixels.AddRange(colors[run]);
                pixels.Add(count % 2 == 0 ? 1f : .5f);
                if (run == 1 && count == 3) pixels.AddRange([0, 0, 0, 0]);
            }
        var render = new PdfRenderOptions(pixels.Count / 4, 1) { Proof = Proof() };
        var frame = new PdfRenderedFrame(pixels.Count / 4, 1, [.. pixels], PdfFrameColorEncoding.LinearSrgbD65);

        var proofed = PdfNativeFrameProof.Apply(frame, render, CancellationToken.None, out long applications);

        Assert.Equal(4, applications);
        for (int pixel = 0; pixel < frame.Width; pixel++)
        {
            if (frame.Pixels[pixel * 4 + 3] == 0) continue;
            var single = new PdfRenderedFrame(1, 1, [.. frame.Pixels.ToArray()[(pixel * 4)..(pixel * 4 + 4)]],
                PdfFrameColorEncoding.LinearSrgbD65);
            var reference = PdfNativeFrameProof.Apply(single, new PdfRenderOptions(1, 1) { Proof = Proof() },
                CancellationToken.None, out long referenceCount);
            Assert.Equal(1, referenceCount);
            Assert.Equal(reference.Pixels.ToArray(), proofed.Pixels.ToArray()[(pixel * 4)..(pixel * 4 + 4)]);
        }
    }

    private static PdfDocumentProofSettings Proof() => new(enabled: true,
        printerProfile: PdfColorProfile.Load(SyntheticCmykPrinter.Create()), gamutWarnings: true,
        options: new PdfProofOptions { RenderingIntent = PdfRenderingIntent.RelativeColorimetric });
}
