using System.Text.Json;
using System.Text;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Signing;

/// <summary>Combined structural, cryptographic, trust, and revision details for one signature.</summary>
public sealed record PdfSignatureInspectionEntry(
    PdfSignatureInfo Signature,
    PdfSignatureVerificationResult Verification,
    PdfSignedRevisionAnalysis? Revision,
    PdfPadesProfile PadesProfile);

/// <summary>A reusable report for every signature in a document.</summary>
public sealed record PdfSignatureInspectionReport(IReadOnlyList<PdfSignatureInspectionEntry> Entries)
{
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
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        return JsonSerializer.Serialize(new
        {
            Version = 1,
            Signatures = Entries.Select(entry => new
            {
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
                entry.Revision
            })
        }, options);
    }
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
