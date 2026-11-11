using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

/// <summary>Prepares display color tables ahead of the first render that needs them.</summary>
public static class PdfCmykWarmUp
{
    /// <summary>
    /// Builds the table that turns RGB paint into ink for a working CMYK profile, so the first RGB paint into a
    /// CMYK surface does not pay for the fit (about a second, once per profile per process). Null prepares the
    /// built-in profile. Safe to call from any thread and any number of times: the table is the one renders
    /// use, so a second call and a following render do no further work.
    /// </summary>
    /// <param name="workingCmykProfile">The working CMYK profile, or null for the built-in one.</param>
    public static void Prepare(PdfColorProfile? workingCmykProfile) =>
        PdfCmykDisplayTable.For(workingCmykProfile ?? PdfCmykViewingDefault.SharedProfile).PrepareRgbInverse();
}
