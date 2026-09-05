using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Security;

/// <summary>PDF public-key recipient blocks and their derived file key.</summary>
public sealed record PdfCertificateRecipientMaterial
{
    internal PdfCertificateRecipientMaterial(byte[] fileKey,
        IReadOnlyList<ReadOnlyMemory<byte>> recipientBlocks, int permissionFlags)
    {
        FileKey = fileKey;
        RecipientBlocks = recipientBlocks;
        PermissionFlags = permissionFlags;
    }

    /// <summary>Gets the file-encryption key.</summary>
    public ReadOnlyMemory<byte> FileKey { get; }
    /// <summary>Gets one DER-encoded CMS envelope per certificate recipient.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> RecipientBlocks { get; }
    /// <summary>Gets the signed PDF permission flags carried by every recipient block.</summary>
    public int PermissionFlags { get; }
}

/// <summary>Creates and opens certificate recipient material for PDF public-key security.</summary>
public static class PdfCertificateRecipientEncryption
{
    private const string Aes256CbcOid = "2.16.840.1.101.3.4.1.42";

    /// <summary>Creates CMS recipient blocks and their shared AES-256 PDF file key.</summary>
    public static PdfCertificateRecipientMaterial Create(
        IEnumerable<X509Certificate2> recipients, int permissionFlags,
        bool encryptMetadata = true)
    {
        ArgumentNullException.ThrowIfNull(recipients);
        X509Certificate2[] certificates = recipients.ToArray();
        if (certificates.Length == 0)
            throw new ArgumentException(
                "At least one certificate recipient is required.", nameof(recipients));
        if (certificates.Any(certificate => certificate is null
            || certificate.GetRSAPublicKey() is null))
            throw new ArgumentException(
                "Every recipient must have an RSA public key.", nameof(recipients));
        if (certificates.Select(certificate => certificate.Thumbprint)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != certificates.Length)
            throw new ArgumentException(
                "Certificate recipients must be unique.", nameof(recipients));

        byte[] seed = RandomNumberGenerator.GetBytes(20);
        byte[] payload = new byte[24];
        seed.CopyTo(payload, 0);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(20), permissionFlags);
        var blocks = new List<ReadOnlyMemory<byte>>(certificates.Length);
        foreach (X509Certificate2 certificate in certificates)
        {
            var envelope = new EnvelopedCms(new ContentInfo(payload),
                new AlgorithmIdentifier(new Oid(Aes256CbcOid)));
            envelope.Encrypt(new CmsRecipient(
                SubjectIdentifierType.IssuerAndSerialNumber, certificate));
            blocks.Add(envelope.Encode());
        }
        return new PdfCertificateRecipientMaterial(
            DeriveFileKey(seed, blocks, 32, encryptMetadata),
            Array.AsReadOnly(blocks.ToArray()), permissionFlags);
    }

    /// <summary>Recovers the shared file key and permissions for one authorized recipient.</summary>
    public static PdfCertificateRecipientMaterial Open(
        IEnumerable<ReadOnlyMemory<byte>> recipientBlocks,
        X509Certificate2 recipient, bool encryptMetadata = true)
        => Open(recipientBlocks, recipient, 32, encryptMetadata);

    /// <summary>
    /// Recovers the shared file key and permissions using the declared PDF key length.
    /// </summary>
    public static PdfCertificateRecipientMaterial Open(
        IEnumerable<ReadOnlyMemory<byte>> recipientBlocks,
        X509Certificate2 recipient, int fileKeyLength, bool encryptMetadata = true)
    {
        ArgumentNullException.ThrowIfNull(recipientBlocks);
        ArgumentNullException.ThrowIfNull(recipient);
        if (!recipient.HasPrivateKey)
            throw new ArgumentException(
                "The recipient certificate must include its private key.", nameof(recipient));
        ReadOnlyMemory<byte>[] blocks = recipientBlocks.Select(block =>
        {
            if (block.IsEmpty)
                throw new ArgumentException(
                    "Recipient blocks cannot be empty.", nameof(recipientBlocks));
            return (ReadOnlyMemory<byte>)block.ToArray();
        }).ToArray();
        if (blocks.Length == 0)
            throw new ArgumentException(
                "At least one recipient block is required.", nameof(recipientBlocks));

        byte[]? payload = null;
        foreach (ReadOnlyMemory<byte> block in blocks)
        {
            try
            {
                var envelope = new EnvelopedCms();
                envelope.Decode(block.Span);
                envelope.Decrypt(new X509Certificate2Collection(recipient));
                payload = envelope.ContentInfo.Content;
                break;
            }
            catch (CryptographicException)
            {
            }
        }
        if (payload is null)
            throw new CryptographicException(
                "The certificate cannot decrypt any PDF recipient block.");
        if (payload.Length != 24)
            throw new CryptographicException(
                "The PDF recipient payload must contain a 20-byte seed and permission flags.");
        if (fileKeyLength is < 5 or > 32)
            throw new ArgumentOutOfRangeException(nameof(fileKeyLength));
        int permissions = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(20));
        byte[] seed = payload.AsSpan(0, 20).ToArray();
        return new PdfCertificateRecipientMaterial(
            DeriveFileKey(seed, blocks, fileKeyLength, encryptMetadata),
            Array.AsReadOnly(blocks), permissions);
    }

    private static byte[] DeriveFileKey(byte[] seed,
        IEnumerable<ReadOnlyMemory<byte>> recipientBlocks, int fileKeyLength,
        bool encryptMetadata)
    {
        HashAlgorithmName algorithm = fileKeyLength == 32
            ? HashAlgorithmName.SHA256 : HashAlgorithmName.SHA1;
        using IncrementalHash hash = IncrementalHash.CreateHash(algorithm);
        hash.AppendData(seed);
        foreach (ReadOnlyMemory<byte> block in recipientBlocks)
            hash.AppendData(block.Span);
        if (!encryptMetadata)
            hash.AppendData([0xFF, 0xFF, 0xFF, 0xFF]);
        return hash.GetHashAndReset()[..fileKeyLength];
    }
}
