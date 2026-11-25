using System.Formats.Asn1;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace KillerPdf.Engine.Signing;

public static partial class PdfSignatureVerifier
{
    private const string TimestampUsage = "1.3.6.1.5.5.7.3.8";

    private sealed record ChainEvaluation(bool Built, X509ChainStatusFlags Flags,
        PdfCertificateTrustStatus Trust, bool TimeValid, PdfCertificateTimeStatus TimeStatus,
        PdfCertificateRevocationStatus Revocation, string[] Errors, PdfCertificateChainElement[] Elements);

    private sealed record TimestampEvaluation(PdfSignatureTimestampStatus Status,
        DateTimeOffset? Time = null, DateTimeOffset Earliest = default, DateTimeOffset Latest = default,
        PdfCertificateRevocationStatus Revocation = PdfCertificateRevocationStatus.NotChecked,
        string? Error = null, string? AuthoritySubject = null, string? AuthorityCertificateSha256 = null);

    // The usages Acrobat accepts on a signing certificate, plus the IETF document-signing purpose.
    private static readonly HashSet<string> DocumentSigningUsages =
    [
        "2.5.29.37.0",                 // any extended key usage
        "1.3.6.1.5.5.7.3.4",           // email protection
        "1.3.6.1.5.5.7.3.3",           // code signing
        "1.3.6.1.5.5.7.3.36",          // document signing
        "1.3.6.1.4.1.311.10.3.12",     // Microsoft document signing
        "1.2.840.113583.1.1.5",        // Adobe Authentic Documents Trust
    ];

    private const string SignerUsageError =
        "The signing certificate's extended key usage does not allow document signing.";

    /// <summary>A certificate without an extended key usage extension may be used for anything.</summary>
    private static bool SignerUsageAllowsDocumentSigning(X509Certificate2 certificate)
    {
        var usage = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().FirstOrDefault();
        return usage is null || usage.EnhancedKeyUsages.Cast<Oid>()
            .Any(oid => oid.Value is { } value && DocumentSigningUsages.Contains(value));
    }

    private static ChainEvaluation RequireSignerUsage(ChainEvaluation evaluation, X509Certificate2 signer) =>
        SignerUsageAllowsDocumentSigning(signer) ? evaluation : evaluation with
        {
            Trust = PdfCertificateTrustStatus.Untrusted,
            Errors = [.. evaluation.Errors, SignerUsageError]
        };

    private static bool HasDefiniteTrustFailure(X509ChainStatusFlags flags) =>
        (flags & ~(X509ChainStatusFlags.NotTimeValid | X509ChainStatusFlags.RevocationStatusUnknown
            | X509ChainStatusFlags.OfflineRevocation)) != X509ChainStatusFlags.NoError;

    private static ChainEvaluation EvaluateChain(X509Certificate2 certificate,
        X509Certificate2Collection embedded, PdfSignatureTrustOptions policy, DateTime time,
        string? applicationUsage = null)
    {
        ChainEvaluation original = BuildChain(certificate, embedded, policy, time,
            policy.CustomTrustRoots, applicationUsage);
        X509ChainStatusFlags nonDiscoveryFailures = original.Flags & ~(X509ChainStatusFlags.UntrustedRoot
            | X509ChainStatusFlags.PartialChain | X509ChainStatusFlags.NotTimeValid
            | X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation);
        if (policy.CustomTrustRoots.Count > 0 || policy.AdditionalTrustRoots.Count == 0
            || original.Trust == PdfCertificateTrustStatus.Trusted || nonDiscoveryFailures != 0)
            return original;
        ChainEvaluation alternative = BuildChain(certificate, embedded, policy, time,
            policy.AdditionalTrustRoots, applicationUsage);
        return alternative.Trust == PdfCertificateTrustStatus.Trusted
            || alternative.Revocation == PdfCertificateRevocationStatus.Revoked ? alternative : original;
    }

    private static ChainEvaluation BuildChain(X509Certificate2 certificate,
        X509Certificate2Collection embedded, PdfSignatureTrustOptions policy, DateTime time,
        IReadOnlyList<X509Certificate2> trustRoots, string? applicationUsage)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = policy.RevocationMode;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        chain.ChainPolicy.VerificationTime = time;
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
        chain.ChainPolicy.UrlRetrievalTimeout = policy.UrlRetrievalTimeout;
        chain.ChainPolicy.DisableCertificateDownloads = policy.DisableCertificateDownloads;
        if (applicationUsage is not null) chain.ChainPolicy.ApplicationPolicy.Add(new Oid(applicationUsage));
        // Embedded certificates are candidates, never trust anchors.
        chain.ChainPolicy.ExtraStore.AddRange(embedded);
        foreach (X509Certificate2 extra in policy.ExtraCertificates) chain.ChainPolicy.ExtraStore.Add(extra);
        if (trustRoots.Count > 0)
        {
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            foreach (X509Certificate2 root in trustRoots) chain.ChainPolicy.CustomTrustStore.Add(root);
        }
        bool built = chain.Build(certificate);
        X509ChainStatusFlags flags = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError,
            (current, status) => current | status.Status);
        bool timeValid = (flags & X509ChainStatusFlags.NotTimeValid) == 0;
        PdfCertificateTrustStatus trust = built ? PdfCertificateTrustStatus.Trusted
            : HasDefiniteTrustFailure(flags) ? PdfCertificateTrustStatus.Untrusted
            : flags != X509ChainStatusFlags.NoError ? PdfCertificateTrustStatus.Trusted
            : PdfCertificateTrustStatus.Indeterminate;
        PdfCertificateRevocationStatus revocation = policy.RevocationMode == X509RevocationMode.NoCheck
            ? PdfCertificateRevocationStatus.NotChecked
            : (flags & X509ChainStatusFlags.Revoked) != 0 ? PdfCertificateRevocationStatus.Revoked
            : (flags & (X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation)) != 0
                ? PdfCertificateRevocationStatus.Indeterminate : PdfCertificateRevocationStatus.Good;
        return new ChainEvaluation(built, flags, trust, timeValid, ClassifyCertificateTime(chain, time, timeValid),
            revocation, [.. chain.ChainStatus.Select(status => status.StatusInformation.Trim())
                .Where(message => message.Length > 0)], [.. chain.ChainElements.Cast<X509ChainElement>().Select(ChainElement)]);
    }

    private static TimestampEvaluation EvaluateTimestamp(SignerInfo signerInfo, SignedCms cms,
        X509Certificate2 signer, PdfSignatureTrustOptions policy)
    {
        X509Certificate2? authority = null;
        string? authoritySubject = null, authorityFingerprint = null;
        try
        {
            CryptographicAttributeObject[] attributes = [.. signerInfo.UnsignedAttributes
                .Cast<CryptographicAttributeObject>()
                .Where(attribute => attribute.Oid.Value == PdfTimestampAuthority.SignatureTimestampTokenOid)];
            if (attributes.Length == 0) return new(PdfSignatureTimestampStatus.Missing);
            if (attributes.Length != 1 || attributes[0].Values.Count != 1)
                return Rejected("The signature timestamp is ambiguous.");
            byte[] encoded = attributes[0].Values[0].RawData;
            if (!Rfc3161TimestampToken.TryDecode(encoded, out Rfc3161TimestampToken? token, out int consumed)
                || token is null || consumed != encoded.Length)
                return Rejected("The signature timestamp token is malformed or has trailing data.");
            var candidates = new X509Certificate2Collection();
            candidates.AddRange(cms.Certificates);
            foreach (X509Certificate2 extra in policy.ExtraCertificates) candidates.Add(extra);
            foreach (X509Certificate2 root in policy.CustomTrustRoots) candidates.Add(root);
            foreach (X509Certificate2 root in policy.AdditionalTrustRoots) candidates.Add(root);
            if (!token.VerifySignatureForSignerInfo(signerInfo, out authority, candidates) || authority is null)
                return Rejected("The timestamp does not verify against this CMS signature.");
            authoritySubject = authority.Subject;
            authorityFingerprint = authority.GetCertHashString(HashAlgorithmName.SHA256);
            X509EnhancedKeyUsageExtension[] usages = [.. authority.Extensions.OfType<X509EnhancedKeyUsageExtension>()];
            if (usages.Length != 1 || !usages[0].Critical || usages[0].EnhancedKeyUsages.Count != 1
                || usages[0].EnhancedKeyUsages[0].Value != TimestampUsage)
                return Rejected("The timestamp authority lacks the required exclusive critical timestamp usage.");
            DateTimeOffset time = token.TokenInfo.Timestamp;
            long accuracy = token.TokenInfo.AccuracyInMicroseconds ?? 0;
            if (accuracy < 0 || accuracy > long.MaxValue / 10)
                return Rejected("The timestamp accuracy is outside the supported range.");
            long ticks = accuracy * 10;
            DateTimeOffset earliest = time.AddTicks(-ticks), latest = time.AddTicks(ticks);
            if (latest > new DateTimeOffset(policy.VerificationTime.ToUniversalTime()))
                return Rejected("The timestamp may be later than the requested verification time.");
            if (earliest.UtcDateTime < signer.NotBefore.ToUniversalTime()
                || latest.UtcDateTime > signer.NotAfter.ToUniversalTime())
                return Rejected("The timestamp uncertainty falls outside the signing certificate validity period.");
            ChainEvaluation tsa = EvaluateChain(authority, token.AsSignedCms().Certificates, policy,
                time.UtcDateTime, TimestampUsage);
            if (tsa.Trust != PdfCertificateTrustStatus.Trusted || !tsa.TimeValid
                || !CoversTimestamp(tsa.Elements, earliest, latest))
                return Rejected("The timestamp authority chain is not trusted and valid for the complete timestamp interval.",
                    tsa.Revocation);
            if (tsa.Revocation == PdfCertificateRevocationStatus.Indeterminate)
                return new(PdfSignatureTimestampStatus.Indeterminate, Revocation: tsa.Revocation,
                    Error: "The requested policy cannot establish timestamp-authority revocation status.",
                    AuthoritySubject: authoritySubject, AuthorityCertificateSha256: authorityFingerprint);
            if (tsa.Revocation == PdfCertificateRevocationStatus.Revoked)
                return Rejected("The timestamp authority is revoked.", tsa.Revocation);
            return new(PdfSignatureTimestampStatus.Trusted, time, earliest, latest, tsa.Revocation,
                AuthoritySubject: authoritySubject, AuthorityCertificateSha256: authorityFingerprint);
        }
        catch (Exception error) when (error is CryptographicException or AsnContentException
            or ArgumentException or OverflowException)
        {
            return Rejected("The optional signature timestamp could not be verified: " + error.Message);
        }
        finally { authority?.Dispose(); }

        TimestampEvaluation Rejected(string error,
            PdfCertificateRevocationStatus revocation = PdfCertificateRevocationStatus.NotChecked) =>
            new(PdfSignatureTimestampStatus.Rejected, Revocation: revocation, Error: error,
                AuthoritySubject: authoritySubject, AuthorityCertificateSha256: authorityFingerprint);
    }

    private static bool CoversTimestamp(PdfCertificateChainElement[] elements,
        DateTimeOffset earliest, DateTimeOffset latest) => elements.Length > 0
        && elements.All(certificate => certificate.NotBefore <= earliest && certificate.NotAfter >= latest);
}
