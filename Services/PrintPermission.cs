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

/// <summary>What the window says after a print was decided: nothing, that the password was wrong,
/// that printing is refused in a dialog, or the refusal only in the status bar after a cancel.</summary>
internal enum PrintNotice { None, WrongPassword, Refused, RefusedCertificate, RefusedQuietly }

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

    /// <summary>What the window says once a print was answered. A wrong owner password is told it was
    /// wrong, whether the file then prints at low quality or not at all, and a canceled prompt on a
    /// limit an owner password could lift only notes the refusal in the status bar.</summary>
    internal static PrintNotice NoticeFor(PrintAnswer answer, bool recipient, bool canLift) =>
        answer.WrongPassword ? PrintNotice.WrongPassword
        : answer.MayPrint ? PrintNotice.None
        : canLift ? PrintNotice.RefusedQuietly
        : recipient ? PrintNotice.RefusedCertificate : PrintNotice.Refused;

    /// <summary>The session a print job renders through, from the app or the command line, so annotation
    /// flags, layers and redaction marks come out as they print.</summary>
    internal static PdfPageRenderSession OpenRenderSession(string path, PrintAllowance allowance, OpenedProtection? protection) =>
        PdfPageRenderSession.OpenEngineFirst(path, Dpi(allowance) / 72.0, protection, forPrinting: true);
}
