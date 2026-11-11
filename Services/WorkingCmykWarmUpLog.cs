namespace KillerPDF.Services;

internal static partial class WorkingCmykProfiles
{
    static partial void LogWarmUpFailure(Exception error)
    {
        try { CrashReporter.Capture(error, "Working CMYK warm-up (non-fatal)"); }
        catch { /* the warm-up is only a head start */ }
    }
}
