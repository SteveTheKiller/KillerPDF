using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>How far a PDF's declared permissions let it be printed.</summary>
internal enum PrintAllowance
{
    Allowed,
    /// <summary>Printing is allowed only at low quality, which Acrobat honors by printing at 150 DPI.</summary>
    LowQualityOnly,
    Forbidden
}

/// <summary>What a print allowance means for the job. Whether a reader has one is answered by
/// <see cref="PdfRestrictions"/>.</summary>
internal static class PrintPermission
{
    internal const double FullDpi = 300;
    internal const double LowQualityDpi = 150;

    internal static PrintAllowance Of(PdfDocumentPermissions permissions) =>
        permissions.AllowHighQualityPrinting ? PrintAllowance.Allowed
        : permissions.AllowLowQualityPrinting ? PrintAllowance.LowQualityOnly
        : PrintAllowance.Forbidden;

    internal static double Dpi(PrintAllowance allowance) =>
        allowance == PrintAllowance.LowQualityOnly ? LowQualityDpi : FullDpi;

    /// <summary>The session a print job renders through, from the app or the command line, so annotation
    /// flags, layers and redaction marks come out as they print.</summary>
    internal static PdfPageRenderSession OpenRenderSession(string path, PrintAllowance allowance) =>
        PdfPageRenderSession.OpenEngineFirst(path, Dpi(allowance) / 72.0, forPrinting: true);
}
