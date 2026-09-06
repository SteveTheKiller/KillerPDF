using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Signing;

/// <summary>Combined structural, cryptographic, trust, and revision details for one signature.</summary>
public sealed record PdfSignatureInspectionEntry(
    PdfSignatureInfo Signature,
    PdfSignatureVerificationResult Verification,
    PdfSignedRevisionAnalysis? Revision,
    PdfPadesProfile PadesProfile);

/// <summary>A reusable report for every signature in a document.</summary>
public sealed partial record PdfSignatureInspectionReport(IReadOnlyList<PdfSignatureInspectionEntry> Entries)
{
    private static readonly PdfSignatureInspectionJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfSignatureInspectionJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Formats signature integrity, trust, evidence, and revision details for review.</summary>
    public string ToText()
    {
        var output = new StringBuilder()
            .Append("Signatures: ").AppendLine(Entries.Count.ToString());
        foreach (PdfSignatureInspectionEntry entry in Entries)
        {
            output.AppendLine(entry.Signature.FieldName)
                .Append("  Signed: ").AppendLine(entry.Signature.IsSigned ? "Yes" : "No")
                .Append("  Document timestamp: ")
                .AppendLine(entry.Signature.IsDocumentTimestamp ? "Yes" : "No")
                .Append("  Cryptographic integrity: ")
                .AppendLine(entry.Verification.IsCryptographicallyValid ? "Valid" : "Invalid")
                .Append("  Overall validation: ")
                .AppendLine(entry.Verification.ValidationStatus.ToString())
                .Append("  Certificate trust: ")
                .AppendLine(entry.Verification.CertificateTrustStatus.ToString())
                .Append("  Revocation: ")
                .AppendLine(entry.Verification.RevocationStatus.ToString())
                .Append("  PAdES evidence: ").AppendLine(entry.PadesProfile.ToString())
                .Append("  Covers whole document: ")
                .AppendLine(entry.Signature.CoversWholeDocument ? "Yes" : "No");
            if (entry.Verification.RequestedRevocationMode is { } revocationMode)
                output.Append("  Requested revocation mode: ")
                    .AppendLine(revocationMode.ToString());
            if (entry.Verification.RequestedVerificationTime is DateTime verificationTime)
                output.Append("  Requested verification time: ")
                    .AppendLine(verificationTime.ToString("O"));
            if (entry.Verification.CertificateDownloadsDisabled is bool downloadsDisabled)
                output.Append("  Network certificate retrieval: ")
                    .AppendLine(downloadsDisabled ? "Disabled" : "Allowed");
            Add("  Signer subject: ", entry.Verification.SignerSubject);
            Add("  Signer issuer: ", entry.Verification.SignerIssuer);
            Add("  Certificate SHA-256: ", entry.Verification.SignerCertificateSha256);
            Add("  Digest algorithm: ", entry.Verification.DigestAlgorithmOid);
            Add("  Signature algorithm: ", entry.Verification.SignatureAlgorithmOid);
            foreach (string policy in entry.Verification.SignaturePolicyOids)
                output.Append("  Signature policy: ").AppendLine(policy);
            if (entry.Verification.CertificateNotBefore is DateTimeOffset notBefore)
                output.Append("  Certificate valid from: ")
                    .AppendLine(notBefore.ToString("O"));
            if (entry.Verification.CertificateNotAfter is DateTimeOffset notAfter)
                output.Append("  Certificate valid until: ")
                    .AppendLine(notAfter.ToString("O"));
            if (entry.Verification.IsCertificateTimeValid is bool timeValid)
                output.Append("  Certificate time validity: ")
                    .AppendLine(timeValid ? "Valid" : "Invalid");
            foreach (string error in entry.Verification.CertificateChainErrors)
                output.Append("  Certificate chain: ").AppendLine(error);
            foreach ((PdfCertificateChainElement certificate, int index) in
                entry.Verification.CertificateChain.Select((certificate, index) =>
                    (certificate, index)))
            {
                output.Append("  Chain certificate ").Append(index + 1).Append(": ")
                    .AppendLine(certificate.Subject)
                    .Append("    Issuer: ").AppendLine(certificate.Issuer)
                    .Append("    SHA-256: ").AppendLine(certificate.Sha256Fingerprint);
                foreach (string status in certificate.StatusMessages)
                    output.Append("    Status: ").AppendLine(status);
            }
            if (entry.Revision is not null)
                output.Append("  Later changes: ")
                    .AppendLine(entry.Revision.HasLaterChanges ? "Yes" : "No");
            if (!string.IsNullOrWhiteSpace(entry.Verification.Error))
                output.Append("  Verification error: ")
                    .AppendLine(entry.Verification.Error);
        }
        return output.ToString();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                output.Append(label).AppendLine(value);
        }
    }

    /// <summary>Exports inspection details without embedding CMS or signed document bytes.</summary>
    public string ToJson(bool indented = false)
    {
        return JsonSerializer.Serialize(new ReportFile(1,
            [.. Entries.Select(entry => new ReportSignature(
                entry.Signature.FieldName,
                entry.Signature.IsSigned,
                entry.Signature.IsCertificationSignature,
                entry.Signature.IsDocumentTimestamp,
                entry.Signature.CertificationPermission,
                entry.Signature.FieldLockAction,
                entry.Signature.FieldLockPermission,
                entry.Signature.LockedFields,
                entry.Signature.SignerName,
                entry.Signature.Reason,
                entry.Signature.Location,
                entry.Signature.ContactInformation,
                entry.Signature.SigningTime,
                entry.Signature.ByteRange,
                entry.Signature.HasValidByteRange,
                entry.Signature.CoversWholeDocument,
                entry.PadesProfile,
                entry.Verification,
                entry.Revision))]),
            indented ? IndentedJson.ReportFile : CompactJson.ReportFile);
    }

    private sealed record ReportFile(int Version, ReportSignature[] Signatures);

    private sealed record ReportSignature(
        string FieldName,
        bool IsSigned,
        bool IsCertificationSignature,
        bool IsDocumentTimestamp,
        PdfSignatureCertificationPermission? CertificationPermission,
        PdfSignatureLockAction? FieldLockAction,
        PdfSignatureLockPermission? FieldLockPermission,
        IReadOnlyList<string>? LockedFields,
        string? SignerName,
        string? Reason,
        string? Location,
        string? ContactInformation,
        DateTimeOffset? SigningTime,
        IReadOnlyList<long>? ByteRange,
        bool HasValidByteRange,
        bool CoversWholeDocument,
        PdfPadesProfile PadesProfile,
        PdfSignatureVerificationResult Verification,
        PdfSignedRevisionAnalysis? Revision);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfSignatureInspectionJsonContext : JsonSerializerContext;
}

/// <summary>Inspects all document signatures through one reusable API.</summary>
public static class PdfSignatureInspection
{
    /// <summary>Checks integrity and revision coverage without evaluating certificate trust.</summary>
    public static PdfSignatureInspectionReport Inspect(PdfDocument document) =>
        Inspect(document, null);

    /// <summary>Checks integrity, configured certificate trust, and revision coverage.</summary>
    public static PdfSignatureInspectionReport Inspect(
        PdfDocument document, PdfSignatureTrustOptions? trustOptions)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfSignatureInspectionEntry[] entries = [.. PdfSignatureReader.Read(document)
            .Select(signature => new PdfSignatureInspectionEntry(
                signature,
                trustOptions is null
                    ? PdfSignatureVerifier.VerifyIntegrity(document, signature)
                    : PdfSignatureVerifier.VerifyTrust(document, signature, trustOptions),
                signature.HasValidByteRange
                    ? PdfSignedRevisionAnalyzer.Analyze(document, signature)
                    : null,
                PdfPadesProfileInspector.Inspect(document, signature)))];
        return new PdfSignatureInspectionReport(Array.AsReadOnly(entries));
    }
}
