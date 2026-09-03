namespace KillerPdf.Engine.Signing;

using KillerPdf.Engine.Authoring;

/// <summary>Structural information about an AcroForm signature field.</summary>
public sealed record PdfSignatureInfo
{
    /// <summary>Gets the qualified AcroForm field name.</summary>
    public required string FieldName { get; init; }
    /// <summary>Gets whether the field contains a signature dictionary.</summary>
    public bool IsSigned { get; init; }
    /// <summary>Gets whether the signature is registered as the document certification signature.</summary>
    public bool IsCertificationSignature { get; init; }
    /// <summary>Gets the certification change level when applicable.</summary>
    public PdfSignatureCertificationPermission? CertificationPermission { get; init; }
    /// <summary>Gets the field-lock selection action.</summary>
    public PdfSignatureLockAction? FieldLockAction { get; init; }
    /// <summary>Gets the field-lock document-change permission.</summary>
    public PdfSignatureLockPermission? FieldLockPermission { get; init; }
    /// <summary>Gets the field names used by an Include or Exclude lock action.</summary>
    public IReadOnlyList<string>? LockedFields { get; init; }
    /// <summary>Gets the signature handler filter name.</summary>
    public string? Filter { get; init; }
    /// <summary>Gets the signature encoding subfilter name.</summary>
    public string? SubFilter { get; init; }
    /// <summary>Gets the signer name stored in the signature dictionary.</summary>
    public string? SignerName { get; init; }
    /// <summary>Gets the stated reason for signing.</summary>
    public string? Reason { get; init; }
    /// <summary>Gets the stated signing location.</summary>
    public string? Location { get; init; }
    /// <summary>Gets the signer contact information.</summary>
    public string? ContactInformation { get; init; }
    /// <summary>Gets the signing time stored in the signature dictionary.</summary>
    public DateTimeOffset? SigningTime { get; init; }
    /// <summary>Gets the declared signed byte ranges.</summary>
    public IReadOnlyList<long>? ByteRange { get; init; }
    /// <summary>The complete PDF /Contents string, including hexadecimal placeholder padding.</summary>
    public ReadOnlyMemory<byte> Contents { get; init; }
    /// <summary>The single bounded ASN.1 CMS value with PDF placeholder padding removed.</summary>
    public ReadOnlyMemory<byte> Cms { get; init; }
    /// <summary>Gets whether the unpadded contents contain one bounded DER CMS value.</summary>
    public bool HasValidCmsEncoding { get; init; }
    /// <summary>Gets whether the byte range is ordered, bounded, and excludes only contents.</summary>
    public bool HasValidByteRange { get; init; }
    /// <summary>Gets whether the signature covers the complete revision containing it.</summary>
    public bool CoversWholeDocument { get; init; }
}
