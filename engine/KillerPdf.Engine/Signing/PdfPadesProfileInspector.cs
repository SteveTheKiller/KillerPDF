using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Signing;

/// <summary>The highest conservatively detected PAdES baseline profile evidence.</summary>
public enum PdfPadesProfile
{
    /// <summary>The signature does not provide a valid PAdES baseline foundation.</summary>
    None,
    /// <summary>The signature is a valid detached CAdES signature.</summary>
    BaselineB,
    /// <summary>The signature also contains an RFC 3161 signature timestamp token.</summary>
    BaselineT,
    /// <summary>The document also contains validation and revocation evidence.</summary>
    BaselineLt
}

/// <summary>Classifies inspectable PAdES baseline evidence without network access.</summary>
public static class PdfPadesProfileInspector
{
    private const string SignatureTimestampTokenOid = "1.2.840.113549.1.9.16.2.14";

    /// <summary>Returns the highest profile supported by evidence embedded in the document.</summary>
    public static PdfPadesProfile Inspect(PdfDocument document, PdfSignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        if (!signature.IsSigned || !signature.HasValidCmsEncoding
            || signature.SubFilter != "ETSI.CAdES.detached"
            || !PdfSignatureVerifier.VerifyIntegrity(document, signature)
                .IsCryptographicallyValid) return PdfPadesProfile.None;
        try
        {
            var cms = new SignedCms();
            cms.Decode(signature.Cms.ToArray());
            bool timestamped = cms.SignerInfos.Cast<SignerInfo>().Any(signer =>
                signer.UnsignedAttributes.Cast<CryptographicAttributeObject>()
                    .Any(attribute => attribute.Oid?.Value == SignatureTimestampTokenOid));
            if (!timestamped) return PdfPadesProfile.BaselineB;
            return HasLongTermValidationEvidence(document)
                ? PdfPadesProfile.BaselineLt : PdfPadesProfile.BaselineT;
        }
        catch (CryptographicException)
        {
            return PdfPadesProfile.None;
        }
    }

    private static bool HasLongTermValidationEvidence(PdfDocument document)
    {
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(Name("DSS"), out PdfObject? dssValue)
            || Resolve(document, dssValue) is not PdfDictionary dss
            || !dss.ContainsKey(Name("VRI"))
            || !dss.ContainsKey(Name("Certs"))) return false;
        return dss.ContainsKey(Name("OCSPs")) || dss.ContainsKey(Name("CRLs"));
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A PAdES validation reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
