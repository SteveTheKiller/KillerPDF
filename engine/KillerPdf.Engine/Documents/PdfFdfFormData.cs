using System.Globalization;
using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads and writes field-only Forms Data Format files without executing actions.</summary>
public static class PdfFdfFormData
{
    private const int MaximumBytes = 64 * 1024 * 1024;

    /// <summary>Reads form field values and the optional source PDF reference from FDF bytes.</summary>
    public static PdfFormDataSet Read(ReadOnlyMemory<byte> source)
    {
        if (source.IsEmpty || source.Length > MaximumBytes)
            throw new ArgumentException("FDF data must be between 1 byte and 64 MB.", nameof(source));
        if (!source.Span.StartsWith("%FDF-"u8))
            throw new InvalidOperationException("The data has no FDF header.");
        PdfStartXref startXref = PdfStartXref.Find(source.Span);
        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, startXref.Offset);
        var objects = new Dictionary<(int, int), PdfObject>();
        foreach (PdfCrossReferenceEntry entry in section.Values)
        {
            if (entry.Type != PdfCrossReferenceEntryType.InUse) continue;
            PdfIndirectObject parsed = new PdfObjectParser(source, checked((int)entry.Field1)).ParseIndirectObject();
            if (parsed.ObjectNumber != entry.ObjectNumber || parsed.Generation != entry.Field2)
                throw new InvalidOperationException("An FDF cross-reference entry does not match its object header.");
            if (!objects.TryAdd((parsed.ObjectNumber, parsed.Generation), parsed.Value))
                throw new InvalidOperationException("The FDF contains a duplicate indirect object.");
        }
        if (!section.Trailer.TryGetValue(Name("Root"), out PdfObject? rootValue))
            throw new InvalidOperationException("The FDF trailer has no root reference.");
        PdfDictionary root = Resolve(rootValue) as PdfDictionary
            ?? throw new InvalidOperationException("The FDF root is not a dictionary.");
        PdfDictionary fdf = root.TryGetValue(Name("FDF"), out PdfObject? fdfValue)
            ? Resolve(fdfValue) as PdfDictionary
                ?? throw new InvalidOperationException("The FDF root /FDF value is not a dictionary.")
            : throw new InvalidOperationException("The FDF root has no /FDF dictionary.");
        var fields = new List<PdfFormDataField>();
        if (fdf.TryGetValue(Name("Fields"), out PdfObject? fieldsValue))
        {
            PdfArray array = Resolve(fieldsValue) as PdfArray
                ?? throw new InvalidOperationException("The FDF /Fields value is not an array.");
            foreach (PdfObject field in array) ReadField(field, null, fields, 0);
        }
        return new PdfFormDataSet
        {
            SourcePdfPath = fdf.TryGetValue(Name("F"), out PdfObject? fileValue)
                ? Text(Resolve(fileValue), "The FDF /F value") : null,
            Fields = Array.AsReadOnly(fields.ToArray()),
            ContainsJavaScript = ContainsScript(root, new HashSet<(int, int)>(), 0)
        };

        PdfObject Resolve(PdfObject value)
        {
            var visited = new HashSet<(int, int)>();
            while (value is PdfIndirectReference reference)
            {
                var key = (reference.ObjectNumber, reference.Generation);
                if (!visited.Add(key) || !objects.TryGetValue(key, out value!))
                    throw new InvalidOperationException("The FDF contains an invalid reference chain.");
            }
            return value;
        }

        void ReadField(PdfObject value, string? parent, ICollection<PdfFormDataField> output, int depth)
        {
            if (depth >= 256) throw new InvalidOperationException("The FDF field hierarchy is too deep.");
            PdfDictionary field = Resolve(value) as PdfDictionary
                ?? throw new InvalidOperationException("An FDF field is not a dictionary.");
            string local = field.TryGetValue(Name("T"), out PdfObject? nameValue)
                ? Text(Resolve(nameValue), "An FDF /T value")
                : throw new InvalidOperationException("An FDF field has no /T value.");
            string full = parent is null ? local : parent + "." + local;
            if (field.TryGetValue(Name("V"), out PdfObject? fieldValue))
                output.Add(new PdfFormDataField { Name = full, Values = Values(Resolve(fieldValue)) });
            if (field.TryGetValue(Name("Kids"), out PdfObject? kidsValue))
            {
                PdfArray kids = Resolve(kidsValue) as PdfArray
                    ?? throw new InvalidOperationException("An FDF field /Kids value is not an array.");
                foreach (PdfObject child in kids) ReadField(child, full, output, depth + 1);
            }
        }

        IReadOnlyList<string> Values(PdfObject value) => value is PdfArray array
            ? Array.AsReadOnly(array.Select(item => Text(Resolve(item), "An FDF field value")).ToArray())
            : Array.AsReadOnly([Text(value, "An FDF field value")]);

        bool ContainsScript(PdfObject value, HashSet<(int, int)> visited, int depth)
        {
            if (depth >= 256) throw new InvalidOperationException("The FDF object graph is too deep.");
            if (value is PdfIndirectReference reference)
            {
                if (!visited.Add((reference.ObjectNumber, reference.Generation))) return false;
                value = Resolve(reference);
            }
            if (value is PdfArray array) return array.Any(item => ContainsScript(item, visited, depth + 1));
            if (value is not PdfDictionary dictionary) return false;
            if (dictionary.Keys.Any(key => key.ValueAsLatin1() is "JavaScript" or "JS")) return true;
            return dictionary.Any(entry => ContainsScript(entry.Value, visited, depth + 1));
        }
    }

    /// <summary>Writes field values and an optional source PDF reference as deterministic FDF.</summary>
    public static byte[] Write(PdfFormDataSet data, IEnumerable<string> fieldNames)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(data.SelectFields(fieldNames));
    }

    /// <summary>Writes all field values and an optional source PDF reference as deterministic FDF.</summary>
    public static byte[] Write(PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<PdfObject>();
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                throw new ArgumentException("FDF field names must be nonempty and unique.", nameof(data));
            PdfObject value = field.Values.Count == 1 ? String(field.Values[0])
                : new PdfArray(field.Values.Select(item => (PdfObject)String(item)));
            fields.Add(new PdfDictionary([
                new(Name("T"), String(field.Name)), new(Name("V"), value)]));
        }
        var fdfEntries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(Name("Fields"), new PdfArray(fields))
        };
        if (data.SourcePdfPath is not null) fdfEntries.Add(new(Name("F"), String(data.SourcePdfPath)));
        var root = new PdfDictionary([
            new(Name("Type"), Name("Catalog")),
            new(Name("FDF"), new PdfDictionary(fdfEntries))]);
        using var output = new MemoryStream();
        output.Write("%FDF-1.2\n%\xE2\xE3\xCF\xD3\n"u8);
        long objectOffset = output.Position;
        PdfObjectWriter.Write(output, new PdfIndirectObject(1, 0, root, (int)objectOffset));
        long xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 2\n0000000000 65535 f \n{objectOffset:0000000000} 00000 n \n");
        output.Write("trailer\n"u8);
        PdfObjectWriter.Write(output, new PdfDictionary([
            new(Name("Root"), new PdfIndirectReference(1, 0)), new(Name("Size"), new PdfInteger(2))]));
        WriteAscii(output, $"\nstartxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    private static string Text(PdfObject value, string description) => value switch
    {
        PdfString text => PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, description),
        PdfName name => "/" + name.ValueAsLatin1(),
        _ => throw new InvalidOperationException($"{description} is not a string or name.")
    };
    private static PdfString String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] bytes = Encoding.BigEndianUnicode.GetBytes(value);
        return new PdfString(new byte[] { 0xFE, 0xFF }.Concat(bytes).ToArray(), PdfStringForm.Hexadecimal);
    }
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private static void WriteAscii(Stream output, string value) => output.Write(Encoding.ASCII.GetBytes(value));
}
