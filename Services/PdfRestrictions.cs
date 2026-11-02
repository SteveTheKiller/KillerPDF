using System.IO;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>Who a PDF took the reader for, which decides what its permissions mean.</summary>
internal enum PdfAccessRole
{
    /// <summary>The file carries no encryption, so it limits nothing.</summary>
    Unencrypted,
    /// <summary>The owner password opened it, which lifts every limit.</summary>
    Owner,
    /// <summary>A user password, usually the empty one, opened it under its declared permissions.</summary>
    User,
    /// <summary>A certificate opened it under the permissions sealed for that recipient.</summary>
    Recipient,
    /// <summary>Nothing could be established, so nothing is allowed.</summary>
    Refused
}

/// <summary>What a reader may do with a PDF: the role it authenticated as and the print, copy and
/// modify permissions the file declares for that role.</summary>
internal sealed record PdfAccess(PdfAccessRole Role, PrintAllowance Print, bool MayCopy, bool MayModify,
    bool DeclaresLimits, string? Refusal = null)
{
    internal static readonly PdfAccess Unrestricted =
        new(PdfAccessRole.Unencrypted, PrintAllowance.Allowed, true, true, false);
    internal static readonly PdfAccess FullOwner =
        new(PdfAccessRole.Owner, PrintAllowance.Allowed, true, true, false);

    internal static PdfAccess Refused(string reason) =>
        new(PdfAccessRole.Refused, PrintAllowance.Forbidden, false, false, true, reason);

    /// <summary>The access a user or recipient has under the permissions the file declares for it.</summary>
    internal static PdfAccess Declared(PdfAccessRole role, PdfDocumentPermissions? permissions)
    {
        if (permissions is null) return Refused(UnreadablePermissions);
        return new PdfAccess(role, PrintPermission.Of(permissions),
            permissions.AllowContentCopying, permissions.AllowDocumentModification,
            !(permissions.AllowLowQualityPrinting && permissions.AllowHighQualityPrinting
                && permissions.AllowContentCopying && permissions.AllowDocumentModification
                && permissions.AllowAnnotationModification && permissions.AllowFormFilling
                && permissions.AllowAccessibilityExtraction && permissions.AllowDocumentAssembly));
    }

    internal const string UnreadablePermissions = "The permissions of this PDF could not be read, so it is refused.";
}

/// <summary>The one answer to what a reader may do with a PDF, for the app and the command line alike.
/// Every failure refuses: a password that opens nothing, a file that cannot be read and a handler
/// that cannot be authenticated all allow nothing, so no error can read as "not restricted".</summary>
internal static class PdfRestrictions
{
    internal const string LockedMessage = "File is encrypted and could not be unlocked - pass --password if it needs one.";

    internal static PdfAccess Read(string path, string? password)
    {
        byte[] source;
        try { source = File.ReadAllBytes(path); }
        catch (Exception error) when (error is not OutOfMemoryException) { return PdfAccess.Refused(error.Message); }
        return Open(source, password, out _);
    }

    internal static PdfAccess Read(ReadOnlyMemory<byte> source, string? password) => Open(source, password, out _);

    /// <summary>Authenticates with the given password, falling back to the empty user password, and
    /// hands back the document that authenticated so its content can be read as that role.</summary>
    internal static PdfAccess Open(ReadOnlyMemory<byte> source, string? password, out PdfDocument? document)
    {
        document = null;
        try
        {
            PdfDocument plain = PdfDocument.OpenWithCompatibilityRecovery(source);
            if (!plain.IsEncrypted)
            {
                document = plain;
                return PdfAccess.Unrestricted;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return PdfAccess.Refused(error.Message);
        }
        document = (string.IsNullOrEmpty(password) ? null : Authenticate(source, password))
            ?? Authenticate(source, string.Empty);
        return document?.PasswordAuthenticationRole switch
        {
            PdfPasswordAuthenticationRole.Owner => PdfAccess.FullOwner,
            PdfPasswordAuthenticationRole.User => PdfAccess.Declared(PdfAccessRole.User, document.DeclaredPermissions),
            _ => PdfAccess.Refused(LockedMessage)
        };
    }

    private static PdfDocument? Authenticate(ReadOnlyMemory<byte> source, string password)
    {
        try { return PdfDocument.OpenWithCompatibilityRecovery(source, password); }
        catch (Exception error) when (error is not OutOfMemoryException) { return null; }
    }

    /// <summary>The engine's own answer to whether a file is encrypted, wherever its /Encrypt entry sits:
    /// a linearized file and a large cross-reference stream both keep it far from the end. A file the
    /// engine cannot parse answers from its bytes, and one that cannot be read at all is not called plain.</summary>
    internal static bool HasEncryption(string path)
    {
        byte[] source;
        try { source = File.ReadAllBytes(path); }
        catch (Exception error) when (error is not OutOfMemoryException) { return true; }
        try { return PdfDocument.OpenWithCompatibilityRecovery(source).IsEncrypted; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return PdfSecurityIndicators.NamesEncryption(source);
        }
    }
}
