namespace KillerPdf.Engine.Signing;

using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Writing;

/// <summary>Descriptive values written into an approval-signature dictionary.</summary>
public sealed record PdfSignatureOptions
{
    /// <summary>Gets the page containing the target signature widget.</summary>
    public int PageIndex { get; init; }
    /// <summary>Gets the qualified name of the target unsigned signature field.</summary>
    public string FieldName { get; init; } = "Signature1";
    /// <summary>Gets the optional signer name written to the signature dictionary.</summary>
    public string? SignerName { get; init; }
    /// <summary>Gets the optional signing reason.</summary>
    public string? Reason { get; init; }
    /// <summary>Gets the optional signing location.</summary>
    public string? Location { get; init; }
    /// <summary>Gets optional signer contact information.</summary>
    public string? ContactInformation { get; init; }
    /// <summary>Gets the signing time, or null to use the current time.</summary>
    public DateTimeOffset? SigningTime { get; init; }
    /// <summary>Gets the number of bytes reserved for the hexadecimal CMS placeholder.</summary>
    public int ReservedSignatureSize { get; init; } = 32_768;
    /// <summary>The digest algorithm the detached CMS callback commits to using.</summary>
    public PdfSignatureDigestMethod DigestMethod { get; init; } =
        PdfSignatureDigestMethod.Sha256;
    /// <summary>Whether the detached CMS callback commits to embedding revocation information.</summary>
    public bool IncludesRevocationInformation { get; init; }
    /// <summary>Gets the selected legal attestation.</summary>
    public string? LegalAttestation { get; init; }
    /// <summary>Gets the declared intent to lock the document.</summary>
    public PdfSignatureDocumentLockIntent? DocumentLockIntent { get; init; }
    /// <summary>Gets the selected named signature appearance.</summary>
    public string? AppearanceName { get; init; }
    /// <summary>Gets the optional visible appearance for the target signature widgets.</summary>
    public PdfSignatureAppearance? VisibleAppearance { get; init; }
    /// <summary>DER-encoded end-entity certificate used by the detached CMS callback.</summary>
    public ReadOnlyMemory<byte> SignerCertificate { get; init; }
    /// <summary>DER-encoded issuer certificates supplied with the signer certificate.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>>? CertificateChain { get; init; }
    /// <summary>Gets the URL from which the signer certificate may be acquired.</summary>
    public string? CertificateAcquisitionUrl { get; init; }
    /// <summary>Gets the RFC 3161 timestamp-server URL.</summary>
    public string? TimestampServerUrl { get; init; }
    /// <summary>Gets the certification change level, or null for an approval signature.</summary>
    public PdfSignatureCertificationPermission? CertificationPermission { get; init; }
    /// <summary>
    /// Optional structural policy for the signature revision. The signature dictionary remains
    /// direct and patchable even when other eligible revision objects are packed.
    /// When omitted, the signature preserves the latest source cross-reference format.
    /// </summary>
    public PdfIncrementalUpdateWriteOptions? IncrementalWriteOptions { get; init; }
}

/// <summary>
/// Describes a visible text appearance. Existing widgets retain their page rectangles and
/// scale the appearance to fit; position and dimensions place newly created widgets.
/// </summary>
public sealed record PdfSignatureAppearance
{
    /// <summary>Gets the left edge in unrotated PDF page coordinates.</summary>
    public double Left { get; init; } = 36;
    /// <summary>Gets the bottom edge in unrotated PDF page coordinates.</summary>
    public double Bottom { get; init; } = 36;
    /// <summary>Gets the appearance width in PDF points.</summary>
    public double Width { get; init; } = 220;
    /// <summary>Gets the appearance height in PDF points.</summary>
    public double Height { get; init; } = 72;
    /// <summary>Gets the text rendered inside the appearance.</summary>
    public string Text { get; init; } = "Digitally signed";
    /// <summary>Gets the text size in PDF points.</summary>
    public double FontSize { get; init; } = 10;
}
