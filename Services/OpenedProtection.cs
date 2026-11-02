using System.IO;
using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>Whether a print may go ahead, at what resolution, and whether a password was tried and failed.</summary>
internal sealed record PrintAnswer(bool MayPrint, double Dpi, bool WrongPassword);

/// <summary>What a protected file allowed when this tab opened it. It is read once at open and kept on
/// the tab like the fill permission, so printing never reads the original again and a file moved or
/// replaced since changes nothing. A limited file keeps its encrypted bytes here, because the owner
/// password can only be checked against them.</summary>
internal sealed class OpenedProtection
{
    internal static readonly OpenedProtection None = new(PdfAccess.Unrestricted, null);

    private byte[]? _encrypted;

    private OpenedProtection(PdfAccess access, byte[]? encrypted)
    {
        Access = access;
        _encrypted = encrypted;
    }

    internal PdfAccess Access { get; private set; }

    /// <summary>The tab of a document the owner password has already opened.</summary>
    internal static OpenedProtection Unlocked() => new(PdfAccess.FullOwner, null);

    /// <summary>Reads what the encrypted file allows the password that opened it. A file that cannot
    /// be read is refused, unless it is damaged and its bytes name no encryption at all: a repaired
    /// plain file has no limit to hold.</summary>
    internal static OpenedProtection Capture(string encryptedPath, string? password)
    {
        byte[] source;
        try { source = File.ReadAllBytes(encryptedPath); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new OpenedProtection(PdfAccess.Refused(error.Message), null);
        }
        PdfAccess access = PdfRestrictions.Read(source, password);
        if (access.Role == PdfAccessRole.Refused && !PdfSecurityIndicators.NamesEncryption(source)) return None;
        return new OpenedProtection(access, access.DeclaresLimits ? source : null);
    }

    /// <summary>A certificate recipient keeps the permissions sealed for it.</summary>
    internal static OpenedProtection ForRecipient(PdfAccess access) => new(access, null);

    /// <summary>True when an owner password could lift the limits. Certificate encryption has none.</summary>
    internal bool CanLift => Access.Role == PdfAccessRole.User && _encrypted is not null;

    /// <summary>Lifts every limit when the password is the file's owner password.</summary>
    private bool TryLift(string password)
    {
        if (_encrypted is null || PdfRestrictions.Read(_encrypted, password).Role != PdfAccessRole.Owner) return false;
        Access = PdfAccess.FullOwner;
        _encrypted = null;
        return true;
    }

    /// <summary>Decides a print. A limit an owner password could lift asks for it. A wrong password or
    /// a cancel leaves a low quality limit printing at the resolution the owner allows, and leaves a
    /// forbidden file refused.</summary>
    internal PrintAnswer AskToPrint(Func<PrintAllowance, string?> askOwnerPassword)
    {
        PrintAllowance allowance = Access.Print;
        bool wrongPassword = false;
        if (allowance != PrintAllowance.Allowed && CanLift)
        {
            string? password = askOwnerPassword(allowance);
            if (password is not null && TryLift(password)) allowance = PrintAllowance.Allowed;
            else wrongPassword = password is not null;
        }
        return new PrintAnswer(allowance != PrintAllowance.Forbidden, PrintPermission.Dpi(allowance), wrongPassword);
    }

    /// <summary>Decides a save that writes the document with no encryption, which would drop every
    /// limit the file declares. The saved copy cannot keep this file's encryption, so only the
    /// password that lifts those limits lets it through.</summary>
    internal bool AskToSaveUnprotected(Func<string?> askOwnerPassword)
    {
        if (!Access.DeclaresLimits) return true;
        return CanLift && askOwnerPassword() is string password && TryLift(password);
    }
}
