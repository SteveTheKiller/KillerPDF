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

/// <summary>What a command writes out of a protected PDF, which decides the permission it answers to.</summary>
internal enum PdfOutput
{
    /// <summary>A new PDF or page images, which could be printed freely.</summary>
    Printable,
    /// <summary>Text, JSON, a table, form data or an attachment: content copied out of the file.</summary>
    Data
}

/// <summary>What a look at a file's bytes found. Unreadable is its own answer, not a kind of encrypted.</summary>
internal enum PdfEncryptionState { Plain, Encrypted, Unreadable }

/// <summary>What a reader may do with a PDF: the role it authenticated as and the print, copy and
/// modify permissions the file declares for that role.</summary>
internal sealed record PdfAccess(PdfAccessRole Role, PrintAllowance Print, bool MayCopy, bool MayModify,
    bool DeclaresLimits, string? Refusal = null)
{
    /// <summary>Whether this reader may write an output that cannot keep the file's encryption. An
    /// edit-forbidden file gives up nothing. Pages of a copy-forbidden file stay in it, as the engine's
    /// page import says, and a print limit stops anything that could be printed freely. Text and
    /// data answer to the copy bit alone. Reading on screen, the clipboard and screen readers are
    /// not outputs, so nothing here stops them.</summary>
    internal bool Allows(PdfOutput output) => MayModify && MayCopy
        && (output == PdfOutput.Data || Print == PrintAllowance.Allowed);

    /// <summary>Whether this reader may take the encryption off. Only a limit makes that the owner's call.</summary>
    internal bool MayRemoveProtection => Role != PdfAccessRole.Refused && !DeclaresLimits;

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
            permissions.AllowContentCopying, permissions.AllowDocumentModification, permissions.DeclaresLimits);
    }

    internal const string UnreadablePermissions = "The permissions of this PDF could not be read, so it is refused.";
}

/// <summary>The one answer to what a reader may do with a PDF, for the app and the command line alike.
/// Every failure refuses: a password that opens nothing, a file that cannot be read and a handler
/// that cannot be authenticated all allow nothing, so no error can read as "not restricted".</summary>
internal static class PdfRestrictions
{
    internal const string LockedMessage = "File is encrypted and could not be unlocked - pass --password if it needs one.";
    internal const string WrongPasswordMessage = "The password does not open this PDF.";

    internal static PdfAccess Read(string path, string? password) => Read(path, password, out _);

    internal static PdfAccess Read(ReadOnlyMemory<byte> source, string? password) => Open(source, password, out _);

    /// <summary>Reads the file once and hands back what a save needs to keep its encryption.</summary>
    internal static PdfAccess Read(string path, string? password, out PdfEncryptionSnapshot? encryption)
    {
        encryption = null;
        PdfDocument parsed;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            parsed = PdfDocument.OpenWithCompatibilityRecovery(file);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return PdfAccess.Refused(error.Message); }
        PdfAccess access = Authenticate(parsed, password, out PdfDocument? document);
        encryption = document?.CaptureEncryption();
        return access;
    }

    /// <summary>Authenticates with the given password, or the empty user password when none was given,
    /// and hands back the document that authenticated so its content can be read as that role.</summary>
    internal static PdfAccess Open(ReadOnlyMemory<byte> source, string? password, out PdfDocument? document)
    {
        document = null;
        PdfDocument parsed;
        try { parsed = PdfDocument.OpenWithCompatibilityRecovery(source); }
        catch (Exception error) when (error is not OutOfMemoryException) { return PdfAccess.Refused(error.Message); }
        return Authenticate(parsed, password, out document);
    }

    // One parse serves the check and the read after it. A password that was given and opens nothing
    // is refused, even on a file an empty password would open.
    private static PdfAccess Authenticate(PdfDocument parsed, string? password, out PdfDocument? document)
    {
        document = null;
        if (!parsed.IsEncrypted)
        {
            document = parsed;
            return PdfAccess.Unrestricted;
        }
        try { parsed.Authenticate(password ?? string.Empty); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return PdfAccess.Refused(string.IsNullOrEmpty(password) ? LockedMessage : WrongPasswordMessage);
        }
        document = parsed;
        return parsed.PasswordAuthenticationRole switch
        {
            PdfPasswordAuthenticationRole.Owner => PdfAccess.FullOwner,
            PdfPasswordAuthenticationRole.User => PdfAccess.Declared(PdfAccessRole.User, parsed.DeclaredPermissions),
            _ => PdfAccess.Refused(LockedMessage)
        };
    }

    /// <summary>Whether a file is encrypted, plain, or could not be read to say. The bytes are scanned a
    /// piece at a time for the name wherever it sits, which also finds what a look at the end of the file
    /// would miss: a linearized file and a large cross-reference stream both keep it far from the end.
    /// Only a file that names encryption is parsed, because a plain one can hold that name as text.</summary>
    internal static PdfEncryptionState ReadEncryptionState(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!PdfSecurityIndicators.NamesEncryption(file)) return PdfEncryptionState.Plain;
            file.Position = 0;
            try
            {
                return PdfDocument.OpenWithCompatibilityRecovery(file).IsEncrypted
                    ? PdfEncryptionState.Encrypted : PdfEncryptionState.Plain;
            }
            catch (Exception error) when (error is not (OutOfMemoryException or IOException or UnauthorizedAccessException))
            {
                return PdfEncryptionState.Encrypted;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return PdfEncryptionState.Unreadable;
        }
    }

    /// <summary>True for an encrypted file, and for one that cannot be read, which is not called plain.</summary>
    internal static bool HasEncryption(string path) => ReadEncryptionState(path) != PdfEncryptionState.Plain;
}
