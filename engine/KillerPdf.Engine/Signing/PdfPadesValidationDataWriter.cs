using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Signing;

/// <summary>Validation evidence to embed for one existing PDF signature.</summary>
public sealed record PdfPadesValidationData
{
    /// <summary>Gets DER certificates used to validate the signer.</summary>
    public required IReadOnlyList<ReadOnlyMemory<byte>> Certificates { get; init; }
    /// <summary>Gets DER OCSP responses collected by the caller.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> OcspResponses { get; init; } = [];
    /// <summary>Gets DER certificate-revocation lists collected by the caller.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> CertificateRevocationLists { get; init; } = [];
    /// <summary>Gets the caller-confirmed UTC time at which the evidence was collected.</summary>
    public DateTimeOffset? ValidationTime { get; init; }
}

/// <summary>Appends PAdES DSS and VRI evidence without changing signed bytes.</summary>
public static class PdfPadesValidationDataWriter
{
    /// <summary>Embeds validation evidence for an existing signature.</summary>
    public static byte[] Embed(PdfDocument document, PdfSignatureInfo signature,
        PdfPadesValidationData data)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(data.Certificates);
        if (!signature.IsSigned || signature.Contents.IsEmpty
            || !PdfSignatureReader.Read(document).Any(candidate =>
                candidate.FieldName == signature.FieldName
                && candidate.Contents.Span.SequenceEqual(signature.Contents.Span)))
            throw new ArgumentException(
                "The selected signature is not present in the document.", nameof(signature));
        if (data.Certificates.Count == 0)
            throw new ArgumentException(
                "PAdES validation data requires at least one certificate.", nameof(data));
        if (data.OcspResponses.Count == 0 && data.CertificateRevocationLists.Count == 0)
            throw new ArgumentException(
                "PAdES validation data requires an OCSP response or revocation list.",
                nameof(data));
        foreach (ReadOnlyMemory<byte> certificate in data.Certificates)
            using (X509CertificateLoader.LoadCertificate(certificate.Span)) { }
        foreach (ReadOnlyMemory<byte> response in data.OcspResponses)
            ValidateDer(response, "OCSP response");
        foreach (ReadOnlyMemory<byte> list in data.CertificateRevocationLists)
            ValidateDer(list, "certificate-revocation list");

        PdfIndirectReference rootReference = document.CrossReferences.TryGetTrailerValue(
            Name("Root"), out PdfObject? rootValue) && rootValue is PdfIndirectReference root
                ? root : throw new InvalidOperationException(
                    "The PDF trailer /Root value is not indirect.");
        PdfDictionary catalog = Resolve(document, rootReference) as PdfDictionary
            ?? throw new InvalidOperationException("The PDF catalog is not a dictionary.");
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfDictionary? existingDss = catalog.TryGetValue(Name("DSS"), out PdfObject? dssValue)
            ? Resolve(document, dssValue) as PdfDictionary
                ?? throw new InvalidOperationException("The catalog /DSS value is not a dictionary.")
            : null;

        List<PdfObject> certificates = ExistingArray(existingDss, "Certs");
        List<PdfObject> responses = ExistingArray(existingDss, "OCSPs");
        List<PdfObject> revocationLists = ExistingArray(existingDss, "CRLs");
        PdfIndirectReference[] addedCertificates = [.. data.Certificates.Select(bytes =>
            update.AddObject(new PdfStream(new PdfDictionary([]), bytes.Span)))];
        PdfIndirectReference[] addedResponses = [.. data.OcspResponses.Select(bytes =>
            update.AddObject(new PdfStream(new PdfDictionary([]), bytes.Span)))];
        PdfIndirectReference[] addedLists = [.. data.CertificateRevocationLists.Select(bytes =>
            update.AddObject(new PdfStream(new PdfDictionary([]), bytes.Span)))];
        certificates.AddRange(addedCertificates);
        responses.AddRange(addedResponses);
        revocationLists.AddRange(addedLists);

        string digest = Convert.ToHexString(SHA1.HashData(signature.Contents.Span));
        PdfDictionary vriEntries = ExistingDictionary(existingDss, "VRI");
        if (vriEntries.ContainsKey(Name(digest)))
            throw new InvalidOperationException(
                "The document already contains validation data for the selected signature.");
        var validationEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            Pair("Type", Name("VRI")),
            Pair("Cert", new PdfArray(addedCertificates))
        };
        if (addedResponses.Length > 0)
            validationEntries.Add(Pair("OCSP", new PdfArray(addedResponses)));
        if (addedLists.Length > 0)
            validationEntries.Add(Pair("CRL", new PdfArray(addedLists)));
        if (data.ValidationTime is DateTimeOffset validationTime)
            validationEntries.Add(Pair("TU", new PdfString(Encoding.ASCII.GetBytes(
                validationTime.ToUniversalTime().ToString(
                    "'D:'yyyyMMddHHmmss'Z'", CultureInfo.InvariantCulture)),
                PdfStringForm.Literal)));
        PdfDictionary validation = new(validationEntries);
        PdfDictionary vri = new(vriEntries.Append(
            new KeyValuePair<PdfName, PdfObject>(Name(digest), validation)));
        var replacedDssKeys = new HashSet<PdfName>
        {
            Name("Type"), Name("Certs"), Name("OCSPs"), Name("CRLs"), Name("VRI")
        };
        var dssEntries = existingDss?.Where(entry => !replacedDssKeys.Contains(entry.Key))
            .ToList() ?? [];
        dssEntries.Add(Pair("Type", Name("DSS")));
        dssEntries.Add(Pair("Certs", new PdfArray(certificates)));
        dssEntries.Add(Pair("VRI", vri));
        if (responses.Count > 0) dssEntries.Add(Pair("OCSPs", new PdfArray(responses)));
        if (revocationLists.Count > 0) dssEntries.Add(Pair("CRLs", new PdfArray(revocationLists)));
        PdfIndirectReference dss = update.AddObject(new PdfDictionary(dssEntries));
        PdfDictionary replacementCatalog = new(catalog
            .Where(entry => !entry.Key.Equals(Name("DSS")))
            .Append(Pair("DSS", dss)));
        return update.ReplaceObject(rootReference.ObjectNumber, replacementCatalog).Build();

        List<PdfObject> ExistingArray(PdfDictionary? dictionary, string key)
        {
            if (dictionary is null || !dictionary.TryGetValue(Name(key), out PdfObject? value))
                return [];
            return Resolve(document, value) is PdfArray array ? [.. array]
                : throw new InvalidOperationException($"The DSS /{key} value is not an array.");
        }

        PdfDictionary ExistingDictionary(PdfDictionary? dictionary, string key)
        {
            if (dictionary is null || !dictionary.TryGetValue(Name(key), out PdfObject? value))
                return new PdfDictionary([]);
            return Resolve(document, value) as PdfDictionary
                ?? throw new InvalidOperationException($"The DSS /{key} value is not a dictionary.");
        }
    }

    private static void ValidateDer(ReadOnlyMemory<byte> data, string description)
    {
        if (data.IsEmpty) throw new ArgumentException($"An empty {description} is not valid.");
        try
        {
            var reader = new AsnReader(data, AsnEncodingRules.DER);
            reader.ReadEncodedValue();
            if (reader.HasData) throw new AsnContentException();
        }
        catch (AsnContentException error)
        {
            throw new ArgumentException($"The {description} is not valid DER.", error);
        }
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A DSS reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static KeyValuePair<PdfName, PdfObject> Pair(string key, PdfObject value) =>
        new(Name(key), value);

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
