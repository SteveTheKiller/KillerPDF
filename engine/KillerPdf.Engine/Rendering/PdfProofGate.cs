using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

// One printer-proof validity rule for PDF and native (EPUB, DjVu) frames, so the same
// settings are either available or refused, with the same reason, on every document kind.
internal static class PdfProofGate
{
    internal static void Validate(PdfDocumentProofSettings proof, bool forPrinting)
    {
        if (forPrinting || proof.Options.RenderingIntent is not (PdfRenderingIntent.RelativeColorimetric
                or PdfRenderingIntent.Perceptual or PdfRenderingIntent.Saturation or PdfRenderingIntent.AbsoluteColorimetric))
            throw new NotSupportedException("The requested printer proof controls are not implemented for this render path.");
        if (proof.PrinterProfile is not { ProfileClassSignature: "prtr",
            HasColorimetricForward: true, HasColorimetricReverse: true } printer)
            throw new NotSupportedException("Printer proof rendering requires an output profile with actual colorimetric transforms in both directions.");
        if ((proof.Options.SimulatePaper || proof.Options.RenderingIntent == PdfRenderingIntent.AbsoluteColorimetric)
            && !printer.HasExplicitValidMediaWhite)
            throw new NotSupportedException(printer.HasInvalidMediaWhite
                ? "Printer proof absolute colorimetric and paper simulation are unavailable because the profile's media-white point is invalid."
                : "Printer proof absolute colorimetric and paper simulation are unavailable because the profile has no media-white point.");
        var inverse = printer.GetTransformSelection(proof.Options.RenderingIntent, PdfColorTransformDirection.ConnectionToDevice);
        if (!inverse.IsAvailable || inverse.UsesIntentFallback)
            throw new NotSupportedException("Printer proof rendering requires an actual inverse for the selected intent without fallback.");
    }

    // Reads the paper endpoints every paper simulation depends on. Paper-only viewing also
    // needs distinct black, and returns the per-channel scale that maps it to reference zero.
    internal static void PreparePaper(PdfColorProfile printer, PdfProofOptions options,
        Span<double> paperBlack, Span<double> paperScale)
    {
        paperBlack.Clear();
        paperScale.Fill(1);
        if (!options.SimulatePaper) return;
        Span<double> paper = stackalloc double[3];
        printer.ReadPaperWhite(paper);
        if (options.SimulateBlack) return;
        printer.ReadPaperBlack(paperBlack);
        for (int channel = 0; channel < 3; channel++)
        {
            double range = paper[channel] - paperBlack[channel];
            if (!double.IsFinite(range) || range <= 1e-9 || !double.IsFinite(paper[channel]) || paper[channel] <= 0)
                throw new NotSupportedException("Printer proof paper-only viewing requires distinct finite paper and black endpoints.");
            paperScale[channel] = paper[channel] / range;
        }
    }
}
