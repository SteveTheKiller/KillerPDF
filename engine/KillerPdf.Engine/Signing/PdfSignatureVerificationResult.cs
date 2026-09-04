namespace KillerPdf.Engine.Signing;

/// <summary>Cryptographic verification outcome for one structurally inspected signature.</summary>
public sealed record PdfSignatureVerificationResult
{
    /// <summary>Gets whether the signature dictionary and byte ranges are structurally valid.</summary>
    public bool IsStructurallyValid { get; init; }
    /// <summary>Gets whether the cryptographic signature matches the signed bytes.</summary>
    public bool IsCryptographicallyValid { get; init; }
    /// <summary>Gets whether certificate-chain trust was evaluated.</summary>
    public bool CertificateTrustWasChecked { get; init; }
    /// <summary>Gets whether the signing certificate chains to a trusted root.</summary>
    public bool IsCertificateTrusted => CertificateTrustStatus == PdfCertificateTrustStatus.Trusted;
    /// <summary>Gets the independently evaluated certificate-chain trust outcome.</summary>
    public PdfCertificateTrustStatus CertificateTrustStatus { get; init; }
    /// <summary>Gets whether the signing certificate was within its validity period.</summary>
    public bool? IsCertificateTimeValid { get; init; }
    /// <summary>Gets the independently evaluated certificate revocation outcome.</summary>
    public PdfCertificateRevocationStatus RevocationStatus { get; init; }
    /// <summary>Gets certificate-chain status details supplied by the platform.</summary>
    public IReadOnlyList<string> CertificateChainErrors { get; init; } = [];
    /// <summary>Gets the CMS digest algorithm object identifier.</summary>
    public string? DigestAlgorithmOid { get; init; }
    /// <summary>Gets the CMS signature algorithm object identifier.</summary>
    public string? SignatureAlgorithmOid { get; init; }
    /// <summary>Gets the signing certificate subject.</summary>
    public string? SignerSubject { get; init; }
    /// <summary>Gets the signing certificate issuer.</summary>
    public string? SignerIssuer { get; init; }
    /// <summary>Gets the signing certificate serial number.</summary>
    public string? SignerSerialNumber { get; init; }
    /// <summary>Gets the uppercase SHA-256 fingerprint of the signing certificate.</summary>
    public string? SignerCertificateSha256 { get; init; }
    /// <summary>Gets the beginning of the signing certificate validity period.</summary>
    public DateTimeOffset? CertificateNotBefore { get; init; }
    /// <summary>Gets the end of the signing certificate validity period.</summary>
    public DateTimeOffset? CertificateNotAfter { get; init; }
    /// <summary>Gets the verification failure message, if any.</summary>
    public string? Error { get; init; }
}

/// <summary>Trust result for the signing certificate identity.</summary>
public enum PdfCertificateTrustStatus
{
    /// <summary>Certificate trust was not requested.</summary>
    NotChecked,
    /// <summary>The certificate chains to an accepted trust anchor.</summary>
    Trusted,
    /// <summary>The certificate chain was evaluated and rejected.</summary>
    Untrusted,
    /// <summary>The certificate chain could not be evaluated conclusively.</summary>
    Indeterminate
}

/// <summary>Revocation result for the signing certificate chain.</summary>
public enum PdfCertificateRevocationStatus
{
    /// <summary>Revocation was not requested.</summary>
    NotChecked,
    /// <summary>The checked chain contains no revoked certificate.</summary>
    Good,
    /// <summary>A certificate in the chain is revoked.</summary>
    Revoked,
    /// <summary>Revocation evidence was unavailable or inconclusive.</summary>
    Indeterminate
}
