using System.IO;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>Whether a print may go ahead, at what resolution, and whether a password was tried and failed.</summary>
internal sealed record PrintAnswer(bool MayPrint, double Dpi, bool WrongPassword);

/// <summary>How a request to write past a limit ended: allowed, canceled at the prompt, or refused with
/// no password that could answer it.</summary>
internal enum ProtectionAnswer { Allowed, Canceled, Refused }

/// <summary>How an owner password prompt on a read-only tab ended.</summary>
internal enum UnlockOutcome { Unlocked, WrongPassword, Failed }

/// <summary>The end of an unlock, with the reason when the password was right and the unlock was not.</summary>
internal sealed record UnlockAnswer(UnlockOutcome Outcome, string? Reason = null);

/// <summary>What a protected file allowed when this tab opened it. It is read once at open and kept on
/// the tab like the fill permission, so printing never reads the original again and a file moved or
/// replaced since changes nothing. A tab holds the file's encryption, never its bytes: the dictionary,
/// the identifier and the key are all a save or an owner password check needs.</summary>
internal sealed class OpenedProtection
{
    internal static readonly OpenedProtection None = new(PdfAccess.Unrestricted, null);

    private PdfEncryptionSnapshot? _encryption;
    private (string Path, string? Password)? _unread;

    private OpenedProtection(PdfAccess access, PdfEncryptionSnapshot? encryption)
    {
        Access = access;
        _encryption = encryption;
    }

    internal PdfAccess Access { get; private set; }

    /// <summary>The tab after the owner password unlocked it. Every limit is lifted, and the encryption
    /// stays held so recovery can still write the document under it.</summary>
    internal OpenedProtection AfterUnlock() => new(PdfAccess.FullOwner, _encryption);

    /// <summary>Reads what the encrypted file allows the password that opened it. A file that cannot
    /// be read is refused and read again the next time it matters, unless it is damaged and its
    /// bytes name no encryption at all: a repaired plain file has no limit to hold.</summary>
    internal static OpenedProtection Capture(string encryptedPath, string? password)
    {
        PdfAccess access = PdfRestrictions.Read(encryptedPath, password, out PdfEncryptionSnapshot? encryption);
        if (access.Role != PdfAccessRole.Refused) return new OpenedProtection(access, encryption);
        if (PdfRestrictions.ReadEncryptionState(encryptedPath) == PdfEncryptionState.Plain) return None;
        return new OpenedProtection(access, null) { _unread = (encryptedPath, password) };
    }

    /// <summary>A certificate recipient keeps the permissions sealed for it.</summary>
    internal static OpenedProtection ForRecipient(PdfAccess access, PdfEncryptionSnapshot? encryption = null) =>
        new(access, encryption);

    /// <summary>True when an owner password could lift the limits. Certificate encryption has none.</summary>
    internal bool CanLift => Access.Role == PdfAccessRole.User && _encryption is not null;

    /// <summary>True when a save writes this file under its own encryption, so a save needs no owner
    /// password and every limit survives it. A file that forbids editing is read-only until its owner
    /// password unlocks it, and that unlock has always saved it plain.</summary>
    internal bool KeepsEncryption => _encryption?.Permissions.AllowDocumentModification == true;

    /// <summary>True when the tab holds the encryption, so a copy can be written under it.</summary>
    internal bool CanSeal => _encryption is not null;

    /// <summary>True when the protection could not be read, so nothing can be saved under it. A read
    /// that failed for a passing reason is tried again each time this is asked.</summary>
    internal bool IsUnreadable
    {
        get
        {
            ReadAgain();
            return Access.Role == PdfAccessRole.Refused;
        }
    }

    /// <summary>True for a form whose author restricts editing while allowing filling, directly or
    /// through the annotation permission, as Acrobat reads it.</summary>
    internal bool AllowsRestrictedFill => Access.Role == PdfAccessRole.User
        && _encryption?.Permissions is { AllowDocumentModification: false } permissions
        && (permissions.AllowFormFilling || permissions.AllowAnnotationModification);

    /// <summary>Writes a finished plain document under the encryption this tab holds.</summary>
    internal void Seal(string plainPath, string destinationPath) =>
        PdfEngineIntegration.WriteUnderEncryption(plainPath, destinationPath,
            _encryption ?? throw new InvalidOperationException("This tab holds no encryption."));

    /// <summary>Opens the bytes of this tab's file under the encryption it holds, or null when it
    /// holds none.</summary>
    internal PdfDocument? OpenHeld(ReadOnlyMemory<byte> source) =>
        _encryption is null ? null : PdfDocument.OpenWithEncryption(source, _encryption);

    /// <summary>Writes a decrypted read-only copy from this tab's file with no password in hand.</summary>
    internal void WriteViewingCopy(string sourcePath, string destinationPath) =>
        PdfEngineIntegration.WriteViewingCopy(sourcePath, destinationPath,
            _encryption ?? throw new InvalidOperationException("This tab holds no encryption."));

    // A file that was encrypted when it opened and reads plain now is a file put in its place, which
    // says nothing about the one this tab read.
    private void ReadAgain()
    {
        if (Access.Role != PdfAccessRole.Refused || _unread is null) return;
        PdfAccess access = PdfRestrictions.Read(_unread.Value.Path, _unread.Value.Password, out PdfEncryptionSnapshot? encryption);
        if (access.Role is PdfAccessRole.Refused or PdfAccessRole.Unencrypted) return;
        Access = access;
        _encryption = encryption;
        _unread = null;
    }

    /// <summary>Lifts every limit for this tab when the password is the file's owner password. The
    /// encryption stays, because lifting a limit here does not take it out of the file.</summary>
    private bool TryLift(string password)
    {
        if (_encryption is null || _encryption.RoleFor(password) != PdfPasswordAuthenticationRole.Owner) return false;
        Access = PdfAccess.FullOwner;
        return true;
    }

    /// <summary>Decides a print. A limit an owner password could lift asks for it. A wrong password or
    /// a cancel leaves a low quality limit printing at the resolution the owner allows, and leaves a
    /// forbidden file refused.</summary>
    internal PrintAnswer AskToPrint(Func<PrintAllowance, string?> askOwnerPassword)
    {
        ReadAgain();
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

    /// <summary>Decides an output that cannot keep this file's encryption. The prompt is told when the
    /// last password was wrong, and only a cancel ends it.</summary>
    internal ProtectionAnswer AskToWrite(PdfOutput output, Func<bool, string?> askOwnerPassword) =>
        AskOwner(() => Access.Allows(output), askOwnerPassword);

    /// <summary>Decides taking the encryption off. Only a file that really declares a limit asks.</summary>
    internal ProtectionAnswer AskToRemove(Func<bool, string?> askOwnerPassword) =>
        AskOwner(() => Access.MayRemoveProtection, askOwnerPassword);

    private ProtectionAnswer AskOwner(Func<bool> allowed, Func<bool, string?> askOwnerPassword)
    {
        ReadAgain();
        if (allowed()) return ProtectionAnswer.Allowed;
        if (!CanLift) return ProtectionAnswer.Refused;
        for (bool wrong = false; ; wrong = true)
        {
            if (askOwnerPassword(wrong) is not string password) return ProtectionAnswer.Canceled;
            if (TryLift(password)) return ProtectionAnswer.Allowed;
        }
    }

    /// <summary>Writes the unlocked copy of a read-only tab. The password is checked first, so a right
    /// password is never reported as a wrong one when the copy cannot be written.</summary>
    internal UnlockAnswer Unlock(string encryptedPath, string unlockedPath, string password)
    {
        if (_encryption is not null && _encryption.RoleFor(password) != PdfPasswordAuthenticationRole.Owner)
            return new UnlockAnswer(UnlockOutcome.WrongPassword);
        try
        {
            PdfEngineIntegration.UnlockWithPassword(encryptedPath, unlockedPath, password);
            return new UnlockAnswer(UnlockOutcome.Unlocked);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return new UnlockAnswer(UnlockOutcome.Failed, error.Message);
        }
    }
}
