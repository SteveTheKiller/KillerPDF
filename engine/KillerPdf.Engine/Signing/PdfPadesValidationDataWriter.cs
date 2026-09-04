using System.Formats.Asn1;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
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

/// <summary>Validation evidence embedded for one existing PDF signature.</summary>
public sealed record PdfPadesValidationEvidence(
    IReadOnlyList<ReadOnlyMemory<byte>> Certificates,
    IReadOnlyList<ReadOnlyMemory<byte>> OcspResponses,
    IReadOnlyList<ReadOnlyMemory<byte>> CertificateRevocationLists,
    DateTimeOffset? ValidationTime);

/// <summary>Reads signature-specific PAdES validation evidence without network access.</summary>
public static class PdfPadesValidationDataReader
{
    private const int MaximumEvidenceBytes = 32 * 1024 * 1024;

    /// <summary>Returns embedded evidence for the selected signature, or null when none exists.</summary>
    public static PdfPadesValidationEvidence? Read(
        PdfDocument document, PdfSignatureInfo signature)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(signature);
        if (!signature.IsSigned || signature.Contents.IsEmpty
            || !PdfSignatureReader.Read(document).Any(candidate =>
                candidate.FieldName == signature.FieldName
                && candidate.Contents.Span.SequenceEqual(signature.Contents.Span)))
            throw new ArgumentException(
                "The selected signature is not present in the document.", nameof(signature));

        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!catalog.TryGetValue(Name("DSS"), out PdfObject? dssValue)) return null;
        PdfDictionary dss = Resolve(document, dssValue) as PdfDictionary
            ?? throw new InvalidOperationException("The catalog /DSS value is not a dictionary.");
        if (!dss.TryGetValue(Name("VRI"), out PdfObject? vriValue)) return null;
        PdfDictionary vri = Resolve(document, vriValue) as PdfDictionary
            ?? throw new InvalidOperationException("The DSS /VRI value is not a dictionary.");
        PdfName key = Name(Convert.ToHexString(SHA1.HashData(signature.Contents.Span)));
        if (!vri.TryGetValue(key, out PdfObject? evidenceValue)) return null;
        PdfDictionary evidence = Resolve(document, evidenceValue) as PdfDictionary
            ?? throw new InvalidOperationException("The signature /VRI value is not a dictionary.");

        return new PdfPadesValidationEvidence(
            ReadStreams("Cert"), ReadStreams("OCSP"), ReadStreams("CRL"), ReadTime());

        IReadOnlyList<ReadOnlyMemory<byte>> ReadStreams(string name)
        {
            if (!evidence.TryGetValue(Name(name), out PdfObject? value)) return [];
            PdfArray array = Resolve(document, value) as PdfArray
                ?? throw new InvalidOperationException($"The VRI /{name} value is not an array.");
            return Array.AsReadOnly(array.Select(item =>
            {
                PdfStream stream = Resolve(document, item) as PdfStream
                    ?? throw new InvalidOperationException(
                        $"A VRI /{name} entry is not a stream.");
                return (ReadOnlyMemory<byte>)PdfStreamDecoder.Decode(
                    stream, document.Resolve, MaximumEvidenceBytes);
            }).ToArray());
        }

        DateTimeOffset? ReadTime()
        {
            if (!evidence.TryGetValue(Name("TU"), out PdfObject? value)) return null;
            PdfString text = Resolve(document, value) as PdfString
                ?? throw new InvalidOperationException("The VRI /TU value is not a string.");
            string source = PdfUnicodeEncoding.DecodeTextString(
                text.Bytes.Span, "The VRI validation time");
            if (!DateTimeOffset.TryParseExact(source, "'D:'yyyyMMddHHmmss'Z'",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                    out DateTimeOffset result))
                throw new InvalidOperationException("The VRI /TU value is not a valid UTC PDF date.");
            return result;
        }
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
