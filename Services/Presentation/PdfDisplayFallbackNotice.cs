using KillerPdf.Engine.Rendering;

namespace KillerPDF.Presentation;

/// <summary>Decides whether a presented page must say that managed display output is unavailable.</summary>
public static class PdfDisplayFallbackNotice
{
    /// <summary>Returns the fallback reason for the current display, or null when no notice applies.</summary>
    /// <remarks>
    /// Inverted viewing turns monitor conversion off deliberately, and a viewport that is not yet
    /// attached has not been observed; neither is a fallback. Any other display for which the
    /// presentation policy cannot establish managed output is.
    /// </remarks>
    public static string? Reason(PdfDisplayDiscoverySnapshot? display, bool inverted)
    {
        if (display is null || inverted || display.Status == PdfDisplayDiscoveryStatus.Uninitialized
            || display is { Status: PdfDisplayDiscoveryStatus.Unavailable, FailureOperation: "ViewportWindowNotAttached" })
            return null;
        var decision = PdfDisplayPresentationPolicy.Resolve(display);
        return decision.Mode == PdfPresentationMode.Unavailable ? decision.FailureOperation : null;
    }
}
