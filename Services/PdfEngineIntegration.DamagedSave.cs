using System;
using System.IO;
using System.Linq;
using KillerPdf.Engine.Syntax;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Signing;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Security;

namespace KillerPDF.Services;

/// <summary>What a damaged file carries that a rewrite would silently destroy.</summary>
internal enum DamagedSaveRefusal { Encrypted, Signed, Certified, UsageRights }

/// <summary>
/// Raised instead of rewriting a damaged file that is encrypted, signed, certified or carries
/// usage rights. The text comes from the string table when the app is running and falls back to
/// English for the command line, so the Save failed dialog shows one plain sentence.
/// </summary>
internal sealed class DamagedSaveRefusedException : InvalidOperationException
{
    internal DamagedSaveRefusedException(DamagedSaveRefusal reason) : base(TextFor(reason))
        => Reason = reason;

    internal DamagedSaveRefusal Reason { get; }

    private static string TextFor(DamagedSaveRefusal reason)
    {
        (string key, string english) = reason switch
        {
            DamagedSaveRefusal.Encrypted => ("Str_Save_DamagedEncrypted",
                "This PDF is damaged and password protected, so KillerPDF cannot repair or save it. The original file has not been changed."),
            DamagedSaveRefusal.Signed => ("Str_Save_DamagedSigned",
                "This PDF is damaged and carries a digital signature. Saving would break the signature, so the file was not changed."),
            DamagedSaveRefusal.Certified => ("Str_Save_DamagedCertified",
                "This PDF is damaged and certified by its author. Saving would break the certification, so the file was not changed."),
            _ => ("Str_Save_DamagedUsageRights",
                "This PDF is damaged and carries Reader usage rights. Saving would break them, so the file was not changed.")
        };
        return System.Windows.Application.Current?.TryFindResource(key) as string ?? english;
    }
}

internal static partial class PdfEngineIntegration
{
    /// <summary>
    /// Reports whether a file carries a signed signature field or catalog permissions such as
    /// Reader usage rights, which saving clears.
    /// </summary>
    internal static bool HasSignedSignatures(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        // A damaged signed file still carries its signature, so it is read the way the viewer reads it.
        PdfDocument document = OpenForReading(source);
        return ReadsAsSigned(document, source) || FindCatalogPermissions(document, source) is not null;
    }

    /// <summary>
    /// Refuses a damaged file a rewrite would strip, for the routes that write a new file without
    /// passing through <see cref="RepairHarmlessSaveArtifacts"/>. A healthy file passes, and so
    /// does one that cannot be read at all, which the caller's own open reports.
    /// </summary>
    internal static void ThrowIfDamagedProtected(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] source = File.ReadAllBytes(path);
        PdfDocument recovered;
        try
        {
            _ = PdfDocument.Open(source);
            return;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try { recovered = PdfDocument.OpenWithCompatibilityRecovery(source); }
        catch (Exception error) when (error is not OutOfMemoryException) { return; }
        if (FindProtectedContent(recovered, source) is DamagedSaveRefusal refusal)
            throw new DamagedSaveRefusedException(refusal);
    }

    /// <summary>Repairs empty outlines and invalid direct crop boxes after serialization.</summary>
    internal static void RepairHarmlessSaveArtifacts(string path) => RepairSerializedCopy(path, false);

    /// <summary>
    /// Readies a temporary copy that feeds a print job or an export and is then deleted. Its output
    /// never stands in for the document, so a damaged signed, certified or usage-rights copy is
    /// normalized instead of refused. An encrypted one is still refused, as its pages cannot be read.
    /// </summary>
    internal static void PrepareThrowawayCopy(string path) => RepairSerializedCopy(path, true);

    private static void RepairSerializedCopy(string path, bool throwaway)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        byte[] original = File.ReadAllBytes(path);
        PdfDocument document;
        try { document = PdfDocument.Open(original); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!TryNormalizeDamagedCopy(path, original, throwaway)) throw;
            original = File.ReadAllBytes(path);
            document = PdfDocument.Open(original);
        }
        byte[] result = PdfSaveSanitizer.RepairHarmlessArtifacts(document);
        if (result.AsSpan().SequenceEqual(original)) return;
        ReplaceWithBuiltResult(path, result);
    }

    // The viewer opens damaged files through recovery and keeps their bytes, so every way out of
    // the app meets them here. Only this working copy changes: the file is rewritten at save time,
    // never at open. A damaged file with usage rights, a certification, a signature or encryption
    // is refused before anything is written, so the save fails and the original file stays.
    private static bool TryNormalizeDamagedCopy(string path, byte[] source, bool throwaway)
        => TryNormalizeDamagedCopy(path, source,
            recovered => PdfDocumentWriter.Write(recovered, new PdfDocumentWriteOptions
            {
                ReplaceDanglingReferences = true,
                AllowSignatureInvalidation = throwaway
            }),
            copy => RebuildDocument(copy, copy), throwaway);

    // The writer and the rebuild are parameters so a test can make the writer refuse.
    internal static bool TryNormalizeDamagedCopy(string path, byte[] source,
        Func<PdfDocument, byte[]> write, Action<string> rebuild, bool throwaway = false)
    {
        try
        {
            PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
            if (FindProtectedContent(recovered, source) is DamagedSaveRefusal refusal
                && (!throwaway || refusal == DamagedSaveRefusal.Encrypted))
                throw new DamagedSaveRefusedException(refusal);
            try
            {
                byte[] rewritten = write(recovered);
                // The repair step after this reads the page tree strictly and the rewrite keeps a
                // wrong /Count, so a rewrite that step cannot read goes to the rebuild, which fixes it.
                _ = PdfSaveSanitizer.RepairHarmlessArtifacts(PdfDocument.Open(rewritten));
                ReplaceWithBuiltResult(path, rewritten);
                return true;
            }
            catch (Exception error) when (error is not (OutOfMemoryException or DamagedSaveRefusedException))
            {
                // The rebuild drops signatures and encryption exactly as the writer refused to, so a
                // refusal for either is final. Only an ordinary rewrite failure reaches the rebuild.
                if (WriterProtectedContentRefusal(error) is DamagedSaveRefusal writerRefusal)
                    throw new DamagedSaveRefusedException(writerRefusal);
                rebuild(path);
                _ = PdfDocument.Open(File.ReadAllBytes(path));
                return true;
            }
        }
        catch (DamagedSaveRefusedException) { throw; }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    // What a rewrite of the recovered file would silently destroy, or null when there is none.
    // Recovery can lose the very entries that say so, so the raw bytes count as evidence too: the
    // encryption dictionary survives a lost trailer, and so does a signature dictionary. They come
    // before the reader because the reader throws on the malformed forms damaged files have.
    private static DamagedSaveRefusal? FindProtectedContent(PdfDocument recovered, ReadOnlyMemory<byte> source)
    {
        ReadOnlySpan<byte> bytes = source.Span;
        if (recovered.IsEncrypted || (PdfSecurityIndicators.NamesEncryption(source) && (HidesEncryption(source) || !ReadsAsPlainText(recovered))))
            return DamagedSaveRefusal.Encrypted;
        if (bytes.IndexOf("/DocMDP"u8) >= 0) return DamagedSaveRefusal.Certified;
        if (bytes.IndexOf("/UR3"u8) >= 0) return DamagedSaveRefusal.UsageRights;
        if (bytes.IndexOf("/ByteRange"u8) >= 0) return DamagedSaveRefusal.Signed;
        if (ReadsAsSigned(recovered, bytes)) return DamagedSaveRefusal.Signed;
        return FindCatalogPermissions(recovered, bytes);
    }

    // A reader failure is no answer about a file whose bytes name a signature: the form around the
    // signature can be malformed while the signature itself is sound.
    private static bool ReadsAsSigned(PdfDocument document, ReadOnlySpan<byte> bytes)
    {
        try { return PdfSignatureReader.Read(document).Any(signature => signature.IsSigned); }
        catch (Exception error) when (error is not OutOfMemoryException) { return NamesSignature(bytes); }
    }

    // A signature dictionary is never inside an object stream, because /ByteRange has to point at
    // its /Contents in the file itself, so what it names can be found in the raw bytes. The other
    // adbe.pkcs7 subfilters, s3 to s5, belong to public-key encryption and say nothing here.
    private static bool NamesSignature(ReadOnlySpan<byte> bytes)
    {
        return bytes.IndexOf("/ByteRange"u8) >= 0 || bytes.IndexOf("/SigFlags"u8) >= 0
            || bytes.IndexOf("/Adobe.PPK"u8) >= 0 || bytes.IndexOf("/adbe.pkcs7.detached"u8) >= 0
            || bytes.IndexOf("/adbe.pkcs7.sha1"u8) >= 0 || bytes.IndexOf("/ETSI."u8) >= 0;
    }

    // Streams and strings are encrypted separately, so a file can lose the one and keep the other.
    private static bool ReadsAsPlainText(PdfDocument recovered) =>
        HasReadablePages(recovered) && !HasEncryptedDates(recovered);

    // A PDF date is plain text, so the document's own dates show whether its strings still are
    // ciphertext. A file without them gives no evidence either way.
    private static bool HasEncryptedDates(PdfDocument recovered)
    {
        try
        {
            if (!recovered.CrossReferences.TryGetTrailerValue(new PdfName("Info"u8), out PdfObject info)
                || ResolveObject(recovered, info) is not PdfDictionary information)
                return false;
            return IsEncryptedDate(recovered, information, new PdfName("CreationDate"u8))
                || IsEncryptedDate(recovered, information, new PdfName("ModDate"u8));
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static bool IsEncryptedDate(PdfDocument recovered, PdfDictionary information, PdfName key)
    {
        if (!information.TryGetValue(key, out PdfObject value)
            || ResolveObject(recovered, value) is not PdfString date)
            return false;
        ReadOnlySpan<byte> bytes = date.Bytes.Span;
        return !bytes.StartsWith<byte>([0xFE, 0xFF]) && bytes.ContainsAnyExceptInRange((byte)0x20, (byte)0x7E);
    }

    // Whether the first page with any content reads as page operators. Ciphertext either fails to
    // decode or reads as long runs of high bytes, and a file with nothing to test proves nothing.
    private static bool HasReadablePages(PdfDocument recovered)
    {
        try
        {
            var reader = new PdfPageContentReader(recovered);
            for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
            {
                IReadOnlyList<PdfContentInstruction> instructions = reader.ReadInstructions(pageIndex);
                if (instructions.Count == 0) continue;
                return instructions.Count(instruction => IsOperatorShaped(instruction.Operator)) * 2
                    > instructions.Count;
            }
            return false;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    private static bool IsOperatorShaped(string name) =>
        name.Length <= 3 && name.All(letter => char.IsAsciiLetterOrDigit(letter) || letter is '*' or '\'' or '"');

    // Any catalog /Perms entry is a certification (DocMDP) or Reader usage rights (UR, UR3), and
    // either one stops being valid the moment the file is rewritten.
    private static DamagedSaveRefusal? FindCatalogPermissions(PdfDocument recovered, ReadOnlySpan<byte> bytes)
    {
        try
        {
            if (!recovered.CrossReferences.TryGetTrailerValue(new PdfName("Root"u8), out PdfObject root)
                || ResolveObject(recovered, root) is not PdfDictionary catalog
                || !catalog.TryGetValue(new PdfName("Perms"u8), out PdfObject permissionsValue))
                return null;
            if (ResolveObject(recovered, permissionsValue) is not PdfDictionary permissions)
                return DamagedSaveRefusal.UsageRights;
            if (permissions.ContainsKey(new PdfName("DocMDP"u8))) return DamagedSaveRefusal.Certified;
            return permissions.Count > 0 ? DamagedSaveRefusal.UsageRights : null;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return NamesSignature(bytes) ? DamagedSaveRefusal.Signed : null;
        }
    }

    private static PdfObject ResolveObject(PdfDocument document, PdfObject value) =>
        value is PdfIndirectReference reference ? document.Resolve(reference) : value;

    // The writer's own refusals to rewrite a signed or protected document, by their wording.
    private static DamagedSaveRefusal? WriterProtectedContentRefusal(Exception error)
    {
        if (error is not InvalidOperationException) return null;
        if (error.Message.StartsWith("A full rewrite invalidates existing signatures", StringComparison.Ordinal))
            return DamagedSaveRefusal.Signed;
        if (error.Message.StartsWith("An encrypted PDF must be opened with a password", StringComparison.Ordinal)
            || error.Message.StartsWith("The PDF user password does not permit", StringComparison.Ordinal))
            return DamagedSaveRefusal.Encrypted;
        return null;
    }

    /// <summary>
    /// True when the bytes name an /Encrypt entry but even recovery reads the file as unprotected
    /// and it does not read as plain text, because the trailer that held the entry is gone and the
    /// pages or strings are ciphertext. A file that reads plainly only has a leftover entry.
    /// </summary>
    internal static bool HidesEncryption(ReadOnlyMemory<byte> source)
    {
        if (!PdfSecurityIndicators.NamesEncryption(source)) return false;
        try
        {
            _ = PdfDocument.Open(source);
            return false;
        }
        // The strict read already judged the pages, and more carefully than recovery can.
        catch (PdfLostEncryptionException) { return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try
        {
            PdfDocument recovered = PdfDocument.OpenWithCompatibilityRecovery(source);
            return !recovered.IsEncrypted && !ReadsAsPlainText(recovered);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { return false; }
    }

    /// <summary>Authenticates and fully rewrites a PDF without password encryption.</summary>
    /// <summary>Lifts an owner restriction. A source with no encryption cannot prove the password,
    /// so unlocking refuses it rather than accepting anything.</summary>
    internal static void UnlockWithPassword(string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(File.ReadAllBytes(sourcePath));
        if (!document.IsEncrypted)
            throw new InvalidOperationException("The unlock source carries no encryption.");
        RemoveEncryption(sourcePath, destinationPath, password);
    }

    /// <summary>Writes a decrypted copy for reading a PDF its user password may not edit. The caller
    /// opens it read-only; only the owner password makes the document editable.</summary>
    internal static void WriteViewingCopy(string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(password);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(
            File.ReadAllBytes(sourcePath), password);
        ReplaceWithBuiltResult(destinationPath, PdfDocumentWriter.Write(document,
            new PdfDocumentWriteOptions { RemoveEncryption = true, ForRestrictedViewing = true }));
    }

    internal static void RemoveEncryption(
        string sourcePath, string destinationPath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        ArgumentNullException.ThrowIfNull(password);
        byte[] source = File.ReadAllBytes(sourcePath);
        PdfDocument document = PdfDocument.OpenWithCompatibilityRecovery(source, password);
        // Without the trailer's /Encrypt the pages are ciphertext, and writing them out would
        // produce a file that has no encryption and cannot be decrypted.
        if (!document.IsEncrypted && HidesEncryption(source))
            throw new DamagedSaveRefusedException(DamagedSaveRefusal.Encrypted);
        byte[] result = PdfDocumentWriter.Write(document,
            new PdfDocumentWriteOptions { RemoveEncryption = true });
        ReplaceWithBuiltResult(destinationPath, result);
    }

    /// <summary>Attempts owner-only encryption removal through the engine.</summary>
    internal static bool TryRemoveEncryptionWithoutPassword(
        string sourcePath, string destinationPath)
    {
        try
        {
            RemoveEncryption(sourcePath, destinationPath, string.Empty);
            return true;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return false;
        }
    }
}
