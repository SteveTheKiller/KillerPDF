using System.Text.Json;
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
