using System.Globalization;
using System.Security.Cryptography;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Security;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Writing;

/// <summary>
/// Encrypts an existing document as a full rewrite, either with Standard Security passwords or
/// for certificate recipients. Any source encryption is removed first, so the output carries only
/// the new protection. A rewrite invalidates existing signatures, so signed documents are refused
/// unless the caller allows that explicitly.
/// </summary>
public static class PdfDocumentEncryptionWriter
{
    private static readonly PdfName SizeName = new("Size"u8);
    private static readonly PdfName RootName = new("Root"u8);
    private static readonly PdfName InfoName = new("Info"u8);
    private static readonly PdfName IdName = new("ID"u8);
    private static readonly PdfName EncryptName = new("Encrypt"u8);

    /// <summary>Returns the document rewritten under Standard Security password encryption.</summary>
    public static byte[] EncryptWithPasswords(PdfDocument document, PdfPasswordEncryptionOptions options,
        bool allowSignatureInvalidation = false)
        => EncryptWithPasswordsWithReport(document, options, allowSignatureInvalidation).Bytes;

    /// <summary>Returns password-encrypted bytes and reference repairs from the plaintext rewrite.</summary>
    public static PdfDocumentWriteResult EncryptWithPasswordsWithReport(PdfDocument document,
        PdfPasswordEncryptionOptions options, bool allowSignatureInvalidation = false,
        bool replaceDanglingReferences = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.UserPassword) && string.IsNullOrEmpty(options.OwnerPassword))
            throw new ArgumentException("Password encryption needs a user or an owner password.", nameof(options));
        // Readers try the empty password first, and an empty owner password would grant owner
        // access to it, opening the document without the user password.
        if (!string.IsNullOrEmpty(options.UserPassword) && string.IsNullOrEmpty(options.OwnerPassword))
            throw new ArgumentException("A user password needs a non-empty owner password.", nameof(options));
        PdfVersion minimum = options.Algorithm == PdfPasswordEncryptionAlgorithm.Aes256Gcm
            ? PdfVersion.Pdf20 : new PdfVersion(1, 7);
        return Encrypt(document, minimum, allowSignatureInvalidation, replaceDanglingReferences,
            () => PdfStandardSecurityHandler.CreateModernPassword(options));
    }

    /// <summary>Returns the document rewritten so that only the given certificate holders can open it.</summary>
    public static byte[] EncryptForRecipients(PdfDocument document, PdfCertificateEncryptionOptions options,
        bool allowSignatureInvalidation = false)
        => EncryptForRecipientsWithReport(document, options, allowSignatureInvalidation).Bytes;

    /// <summary>Returns recipient-encrypted bytes and reference repairs from the plaintext rewrite.</summary>
    public static PdfDocumentWriteResult EncryptForRecipientsWithReport(PdfDocument document,
        PdfCertificateEncryptionOptions options, bool allowSignatureInvalidation = false,
        bool replaceDanglingReferences = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Recipients is null || options.Recipients.Count == 0)
            throw new ArgumentException("Certificate encryption needs at least one recipient.", nameof(options));
        return Encrypt(document, new PdfVersion(1, 7), allowSignatureInvalidation, replaceDanglingReferences,
            () => PdfStandardSecurityHandler.CreateCertificate(options));
    }

    /// <summary>
    /// Returns a document with no encryption of its own rewritten under the encryption of the file it
    /// was made from, so the output needs the same passwords and declares the same permissions. A
    /// rewrite ends any signature or usage rights the document still carries.
    /// </summary>
    public static byte[] EncryptUnder(PdfDocument document, PdfEncryptionSnapshot encryption)
    {
        ArgumentNullException.ThrowIfNull(encryption);
        return Encrypt(document, encryption.MinimumVersion, allowSignatureInvalidation: true,
            replaceDanglingReferences: false, () => (encryption.Handler, encryption.Dictionary), encryption).Bytes;
    }

    private static PdfDocumentWriteResult Encrypt(PdfDocument document, PdfVersion minimumVersion,
        bool allowSignatureInvalidation, bool replaceDanglingReferences,
        Func<(PdfStandardSecurityHandler Handler, PdfDictionary Dictionary)> createSecurity,
        PdfEncryptionSnapshot? held = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfVersion sourceVersion = document.Header.Version;
        // Normalize first: a decrypted rewrite with a classic table and every object at the top
        // level, so each object can be encrypted under its own number and generation.
        PdfDocumentWriteResult plainResult = PdfDocumentWriter.WriteWithReport(document, new PdfDocumentWriteOptions
        {
            RemoveEncryption = document.IsEncrypted,
            AllowSignatureInvalidation = allowSignatureInvalidation,
            ReplaceDanglingReferences = replaceDanglingReferences,
            CrossReferenceFormat = PdfCrossReferenceFormat.Table,
            UseObjectStreams = false,
            // A held key belongs with the original identifier, whatever a working copy carries.
            PreserveDocumentIdentifiers = held is null,
            TargetVersion = sourceVersion.CompareTo(minimumVersion) < 0 ? minimumVersion : (PdfVersion?)null,
        });
        byte[] plain = plainResult.Bytes;
        PdfDocument source = PdfDocument.Open(plain);
        (PdfStandardSecurityHandler security, PdfDictionary encryption) = createSecurity();

        List<(int Number, int Generation)> objects = [.. source.CrossReferences
            .Where(entry => entry.Key > 0 && entry.Value.Type == PdfCrossReferenceEntryType.InUse)
            .Select(entry => (entry.Key, entry.Value.Field2))
            .OrderBy(entry => entry.Key)];
        int encryptionNumber = checked((objects.Count == 0 ? 0 : objects[^1].Number) + 1);

        using var output = new MemoryStream();
        Ascii(output, $"%PDF-{source.Header.Version}\n");
        output.Write([(byte)'%', 0xE2, 0xE3, 0xCF, 0xD3, (byte)'\n']);
        var offsets = new List<(int Number, int Generation, int Offset)>(objects.Count + 1);
        foreach ((int number, int generation) in objects)
        {
            int offset = checked((int)output.Position);
            offsets.Add((number, generation, offset));
            PdfObject value = security.Encrypt(source.Resolve(number), number, generation, source.Resolve);
            PdfObjectWriter.Write(output, new PdfIndirectObject(number, generation, value, offset));
        }
        int encryptionOffset = checked((int)output.Position);
        offsets.Add((encryptionNumber, 0, encryptionOffset));
        PdfObjectWriter.Write(output, new PdfIndirectObject(encryptionNumber, 0, encryption, encryptionOffset));

        int xrefOffset = checked((int)output.Position);
        output.Write("xref\n0 1\n0000000000 65535 f \n"u8);
        foreach ((int number, int generation, int offset) in offsets)
            Ascii(output, string.Create(CultureInfo.InvariantCulture,
                $"{number} 1\n{offset:0000000000} {generation:00000} n \n"));

        var trailer = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(SizeName, new PdfInteger(encryptionNumber + 1)),
        };
        if (!source.CrossReferences.TryGetTrailerValue(RootName, out PdfObject root))
            throw new InvalidOperationException("The rewritten document has no trailer /Root reference.");
        trailer.Add(new(RootName, root));
        if (source.CrossReferences.TryGetTrailerValue(InfoName, out PdfObject info)
            && info is PdfIndirectReference)
            trailer.Add(new(InfoName, info));
        // A legacy file key is derived from the first identifier, so a held key takes its own /ID,
        // and a file that had none takes none.
        if (held is null) trailer.Add(new(IdName, Identifiers(source, plain)));
        else if (held.Identifiers is PdfArray identifiers) trailer.Add(new(IdName, identifiers));
        trailer.Add(new(EncryptName, new PdfIndirectReference(encryptionNumber, 0)));
        output.Write("trailer\n"u8);
        PdfObjectWriter.Write(output, new PdfDictionary(trailer));
        output.Write("\nstartxref\n"u8);
        Ascii(output, xrefOffset.ToString(CultureInfo.InvariantCulture));
        output.Write("\n%%EOF\n"u8);
        return new PdfDocumentWriteResult(output.ToArray(), plainResult.ReferenceRepairs);
    }

    // Encrypted documents need a trailer /ID. The source pair is kept so links to the document
    // still resolve; a document without one gets a fresh identifier from its content.
    private static PdfArray Identifiers(PdfDocument source, byte[] content)
    {
        if (source.CrossReferences.TryGetTrailerValue(IdName, out PdfObject value)
            && value is PdfArray { Count: 2 } pair
            && pair[0] is PdfString && pair[1] is PdfString)
            return pair;
        byte[] identifier = SHA256.HashData(content)[..16];
        return new PdfArray([
            new PdfString(identifier, PdfStringForm.Hexadecimal),
            new PdfString(identifier, PdfStringForm.Hexadecimal)]);
    }

    private static void Ascii(Stream output, string text) =>
        output.Write(System.Text.Encoding.ASCII.GetBytes(text));
}
