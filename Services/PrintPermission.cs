using System.IO;
using KillerPdf.Engine.Documents;
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

/// <summary>Reads the print permission a PDF declares, and whether a password lifts it. The permission
/// is read straight from the file, since a PDF whose owner forbids only printing still opens for
/// editing and so never counts as a restricted viewing copy.</summary>
internal static class PrintPermission
{
    internal const double FullDpi = 300;
    internal const double LowQualityDpi = 150;

    internal static PrintAllowance Of(PdfDocumentPermissions? permissions) =>
        permissions is null || permissions.AllowHighQualityPrinting ? PrintAllowance.Allowed
        : permissions.AllowLowQualityPrinting ? PrintAllowance.LowQualityOnly
        : PrintAllowance.Forbidden;

    internal static double Dpi(PrintAllowance allowance) =>
        allowance == PrintAllowance.LowQualityOnly ? LowQualityDpi : FullDpi;

    /// <summary>What the file at the path allows. A file with no password encryption, or one whose
    /// permissions cannot be read, allows printing.</summary>
    internal static PrintAllowance ReadFor(string path)
    {
        try { return Of(PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(path)).ReadDeclaredPermissionsWithoutPassword()); }
        catch (Exception error) when (error is not OutOfMemoryException) { return PrintAllowance.Allowed; }
    }

    /// <summary>True when the password is the file's owner password, which lifts every permission.</summary>
    internal static bool IsOwnerPassword(string path, string password)
    {
        try
        {
            return PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(path), password)
                .PasswordAuthenticationRole == PdfPasswordAuthenticationRole.Owner;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>The allowance that stands for a command line print: the file's own, unless the
    /// password given is the owner password.</summary>
    internal static PrintAllowance ForPassword(string path, string? password)
    {
        PrintAllowance allowance = ReadFor(path);
        return allowance != PrintAllowance.Allowed && !string.IsNullOrEmpty(password) && IsOwnerPassword(path, password)
            ? PrintAllowance.Allowed : allowance;
    }
}
