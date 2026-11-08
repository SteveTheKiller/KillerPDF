using KillerPdf.Engine.ColorManagement;
using KillerPDF.Services;
using PdfRenderedFrame = KillerPdf.Engine.Rendering.PdfRenderedFrame;

namespace KillerPDF.Presentation;

/// <summary>BGRA viewing pixels and every fallback taken to produce them.</summary>
/// <param name="ProofUnavailable">The requested printer proof could not be applied to this render.</param>
/// <param name="DisplayFallbackOperation">Why monitor conversion was not applied, or null when it was or was not requested.</param>
/// <param name="Compatibility">The compatibility render used when no float frame was available.</param>
internal sealed record PdfViewingPixels(int Width, int Height, byte[] Pixels, bool ProofUnavailable,
    string? DisplayFallbackOperation, PdfRenderedPage? Compatibility);

/// <summary>Applies requested proof and monitor conversion to one viewing render.</summary>
/// <remarks>
/// A proof the page cannot take keeps monitor conversion, and a page the float renderer cannot
/// draw keeps a visible display fallback, so neither request is ever dropped silently.
/// </remarks>
internal static class PdfViewingFrameConversion
{
    /// <summary>The fallback reason when the float renderer cannot draw the page.</summary>
    internal const string PreciseRenderUnavailable = "PreciseRenderUnavailable";

    internal static PdfViewingPixels Render(Func<PdfDocumentProofSettings?, PdfRenderedFrame> renderFrame,
        Func<PdfRenderedPage> renderCompatible, PdfDocumentProofSettings? proof,
        PdfSdrDisplayConverter? monitor, CancellationToken token)
    {
        bool proofRequested = proof?.Enabled == true;
        if (!proofRequested && monitor is null) return Compatible(renderCompatible(), false, null);
        PdfRenderedFrame? frame = null;
        bool proofUnavailable = false;
        try { frame = renderFrame(proofRequested ? proof : null); }
        catch (NotSupportedException) when (proofRequested)
        {
            // The proof is refused, but monitor conversion of the unproofed page still applies.
            proofUnavailable = true;
            if (monitor is not null)
                try { frame = renderFrame(null); }
                catch (NotSupportedException) { }
        }
        catch (NotSupportedException) { }
        if (frame is not null) return Convert(frame, monitor, proofUnavailable, token);
        return Compatible(renderCompatible(), proofUnavailable, monitor is null ? null : PreciseRenderUnavailable);
    }

    private static PdfViewingPixels Convert(PdfRenderedFrame frame, PdfSdrDisplayConverter? monitor,
        bool proofUnavailable, CancellationToken token)
    {
        var conversion = monitor?.Convert(frame, token);
        if (conversion?.Outcome == PdfSdrConversionOutcome.Stale) throw new OperationCanceledException(token);
        byte[] pixels = conversion?.Outcome == PdfSdrConversionOutcome.Converted
            ? conversion.BgraPixels.ToArray() : frame.ToSrgbBgra32(token);
        return new(frame.Width, frame.Height, pixels, proofUnavailable, conversion?.FailureOperation, null);
    }

    private static PdfViewingPixels Compatible(PdfRenderedPage page, bool proofUnavailable, string? fallback) =>
        new(page.Width, page.Height, page.Pixels, proofUnavailable, fallback, page);
}
