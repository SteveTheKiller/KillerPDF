using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Persists XFA dataset changes while preserving other XFA packets.</summary>
public static class PdfXfaDocumentEditor
{
    /// <summary>Replaces the XFA datasets and appends one PDF revision.</summary>
    public static byte[] ReplaceDatasets(PdfDocument document, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(data);
        PdfXfaInfo current = PdfXfaReader.Read(document)
            ?? throw new InvalidOperationException("The document has no XFA form.");
        PdfXfaInfo updated = PdfXfaDatasets.Replace(current, data);

        PdfObject rootValue = document.Trailer.TryGetValue(Name("Root"), out PdfObject? root)
            ? root : throw new InvalidOperationException("The PDF trailer has no /Root value.");
        PdfIndirectReference rootReference = rootValue as PdfIndirectReference
            ?? throw new NotSupportedException("A direct document catalog cannot be updated incrementally.");
        PdfDictionary catalog = Dictionary(document, rootValue, "document catalog");
        PdfObject formValue = catalog.TryGetValue(Name("AcroForm"), out PdfObject? form)
            ? form : throw new InvalidOperationException("The document catalog has no /AcroForm value.");
        PdfDictionary acroForm = Dictionary(document, formValue, "AcroForm");
        PdfObject xfaValue = acroForm.TryGetValue(Name("XFA"), out PdfObject? xfa)
            ? xfa : throw new InvalidOperationException("The AcroForm has no /XFA value.");

        var revision = new PdfIncrementalUpdateBuilder(document);
        if (current.IsPacketArray)
        {
            PdfArray array = Resolve(document, xfaValue) as PdfArray
                ?? throw new InvalidOperationException("The /XFA value is not a packet array.");
            int packetIndex = current.Packets.Select((packet, index) => (packet, index))
                .Where(item => item.packet.Name.Equals("datasets", StringComparison.OrdinalIgnoreCase))
                .Select(item => item.index).Single();
            int valueIndex = checked(packetIndex * 2 + 1);
            PdfObject packetValue = array[valueIndex];
            PdfStream packetStream = Resolve(document, packetValue) as PdfStream
                ?? throw new InvalidOperationException("The XFA datasets packet is not a stream.");
            PdfStream replacement = Stream(packetStream, updated.Packets[packetIndex].Data);
            if (packetValue is PdfIndirectReference packetReference)
                revision.ReplaceObject(packetReference.ObjectNumber, replacement);
            else
            {
                PdfObject[] items = [.. array];
                items[valueIndex] = replacement;
                ReplaceXfaContainer(revision, rootReference, catalog, formValue, acroForm,
                    xfaValue, new PdfArray(items));
            }
        }
        else
        {
            PdfStream xdpStream = Resolve(document, xfaValue) as PdfStream
                ?? throw new InvalidOperationException("The combined /XFA value is not a stream.");
            PdfStream replacement = Stream(xdpStream, updated.Packets.Single().Data);
            if (xfaValue is PdfIndirectReference xfaReference)
                revision.ReplaceObject(xfaReference.ObjectNumber, replacement);
            else
                ReplaceXfaContainer(revision, rootReference, catalog, formValue, acroForm,
                    xfaValue, replacement);
        }

        byte[] output = revision.Build();
        if (!document.IsEncrypted)
            Verify(output, data);
        return output;
    }

    private static void ReplaceXfaContainer(PdfIncrementalUpdateBuilder revision,
        PdfIndirectReference rootReference, PdfDictionary catalog, PdfObject formValue,
        PdfDictionary acroForm, PdfObject xfaValue, PdfObject replacement)
    {
        if (xfaValue is PdfIndirectReference xfaReference)
        {
            revision.ReplaceObject(xfaReference.ObjectNumber, replacement);
            return;
        }
        PdfDictionary changedForm = With(acroForm, Name("XFA"), replacement);
        if (formValue is PdfIndirectReference formReference)
            revision.ReplaceObject(formReference.ObjectNumber, changedForm);
        else
            revision.ReplaceObject(rootReference.ObjectNumber,
                With(catalog, Name("AcroForm"), changedForm));
    }

    private static PdfStream Stream(PdfStream source, ReadOnlyMemory<byte> data)
    {
        HashSet<PdfName> removed = [Name("Length"), Name("Filter"), Name("DecodeParms"), Name("DL")];
        return new PdfStream(new PdfDictionary(source.Dictionary.Where(entry =>
            !removed.Contains(entry.Key))), data.Span);
    }

    private static PdfDictionary With(PdfDictionary source, PdfName key, PdfObject value) =>
        new(source.Where(entry => !entry.Key.Equals(key)).Append(
            new KeyValuePair<PdfName, PdfObject>(key, value)));

    private static void Verify(ReadOnlyMemory<byte> output, PdfFormDataSet expected)
    {
        PdfXfaInfo reopened = PdfXfaReader.Read(PdfDocument.Open(output))
            ?? throw new InvalidOperationException("The saved PDF no longer contains XFA data.");
        PdfFormDataSet actual = PdfXfaDatasets.Read(reopened);
        if (actual.Fields.Count != expected.Fields.Count
            || actual.Fields.Where((field, index) => field.Name != expected.Fields[index].Name
                || !field.Values.SequenceEqual(expected.Fields[index].Values)).Any())
            throw new InvalidOperationException("The saved XFA dataset did not pass verification.");
    }

    private static PdfDictionary Dictionary(PdfDocument document,
        PdfObject value, string description) => Resolve(document, value) as PdfDictionary
        ?? throw new InvalidOperationException($"The {description} is not a dictionary.");

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An XFA reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
