using System.Globalization;
using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads and writes Forms Data Format fields and annotations without executing actions.</summary>
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
        var annotations = new List<PdfFormDataAnnotation>();
        if (fdf.TryGetValue(Name("Annots"), out PdfObject? annotationsValue))
        {
            PdfArray array = Resolve(annotationsValue) as PdfArray
                ?? throw new InvalidOperationException("The FDF /Annots value is not an array.");
            foreach (PdfObject annotation in array) annotations.Add(ReadAnnotation(annotation));
        }
        string? sourcePdfPath = null;
        ReadOnlyMemory<byte>? embeddedSourcePdf = null;
        if (fdf.TryGetValue(Name("F"), out PdfObject? fileValue))
        {
            PdfObject file = Resolve(fileValue);
            if (file is PdfString or PdfName)
                sourcePdfPath = Text(file, "The FDF /F value");
            else if (file is PdfDictionary specification)
            {
                sourcePdfPath = specification.TryGetValue(Name("UF"), out PdfObject? unicodeName)
                    ? Text(Resolve(unicodeName), "The FDF source /UF value")
                    : specification.TryGetValue(Name("F"), out PdfObject? nameValue)
                        ? Text(Resolve(nameValue), "The FDF source /F value") : null;
                if (specification.TryGetValue(Name("EF"), out PdfObject? embeddedValue))
                {
                    PdfDictionary embedded = Resolve(embeddedValue) as PdfDictionary
                        ?? throw new InvalidOperationException(
                            "The FDF source /EF value is not a dictionary.");
                    PdfObject? streamValue = embedded.TryGetValue(Name("UF"), out PdfObject? uf)
                        ? uf : embedded.TryGetValue(Name("F"), out PdfObject? ordinary) ? ordinary : null;
                    if (streamValue is null || Resolve(streamValue) is not PdfStream stream)
                        throw new InvalidOperationException(
                            "The FDF source /EF dictionary has no embedded file stream.");
                    byte[] decoded = PdfStreamDecoder.Decode(stream, reference => Resolve(reference),
                        256 * 1024 * 1024);
                    if (!decoded.AsSpan().StartsWith("%PDF-"u8))
                        throw new InvalidOperationException(
                            "The FDF embedded source is not a PDF document.");
                    embeddedSourcePdf = decoded;
                }
            }
            else throw new InvalidOperationException(
                "The FDF /F value is not a string, name, or file specification.");
        }
        return new PdfFormDataSet
        {
            SourcePdfPath = sourcePdfPath,
            EmbeddedSourcePdf = embeddedSourcePdf,
            Fields = Array.AsReadOnly(fields.ToArray()),
            Annotations = Array.AsReadOnly(annotations.ToArray()),
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

        PdfFormDataAnnotation ReadAnnotation(PdfObject value)
        {
            PdfDictionary annotation = Resolve(value) as PdfDictionary
                ?? throw new InvalidOperationException("An FDF annotation is not a dictionary.");
            string subtype = annotation.TryGetValue(Name("Subtype"), out PdfObject? subtypeValue)
                && Resolve(subtypeValue) is PdfName subtypeName
                ? subtypeName.ValueAsLatin1()
                : throw new InvalidOperationException("An FDF annotation has no subtype name.");
            int page = annotation.TryGetValue(Name("Page"), out PdfObject? pageValue)
                && Resolve(pageValue) is PdfInteger pageNumber && pageNumber.Value >= 0
                ? checked((int)pageNumber.Value)
                : throw new InvalidOperationException("An FDF annotation has an invalid page index.");
            PdfArray rectangle = annotation.TryGetValue(Name("Rect"), out PdfObject? rectangleValue)
                ? Resolve(rectangleValue) as PdfArray
                    ?? throw new InvalidOperationException("An FDF annotation rectangle is not an array.")
                : throw new InvalidOperationException("An FDF annotation has no rectangle.");
            double[] coordinates = rectangle.Select(Number).ToArray();
            if (coordinates.Length != 4 || coordinates[2] < coordinates[0]
                || coordinates[3] < coordinates[1])
                throw new InvalidOperationException("An FDF annotation rectangle is invalid.");
            double? opacity = OptionalNumber(annotation, "CA");
            if (opacity is < 0 or > 1)
                throw new InvalidOperationException("An FDF annotation opacity must be between zero and one.");
            return new PdfFormDataAnnotation
            {
                Subtype = subtype,
                PageIndex = page,
                Rectangle = Array.AsReadOnly(coordinates),
                Name = OptionalText(annotation, "NM"),
                Contents = OptionalText(annotation, "Contents"),
                Author = OptionalText(annotation, "T"),
                Subject = OptionalText(annotation, "Subj"),
                Color = Color(annotation),
                Opacity = opacity,
                CreationDate = OptionalText(annotation, "CreationDate"),
                ModifiedDate = OptionalText(annotation, "M"),
                ReplyToName = OptionalText(annotation, "IRT")
            };
        }

        string? OptionalText(PdfDictionary dictionary, string key) =>
            dictionary.TryGetValue(Name(key), out PdfObject? value)
                ? Text(Resolve(value), $"An FDF annotation /{key} value") : null;
        double? OptionalNumber(PdfDictionary dictionary, string key) =>
            dictionary.TryGetValue(Name(key), out PdfObject? value) ? Number(Resolve(value)) : null;
        double Number(PdfObject value) => Resolve(value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException("An FDF annotation number is invalid.")
        };
        string? Color(PdfDictionary dictionary)
        {
            if (!dictionary.TryGetValue(Name("C"), out PdfObject? value)) return null;
            PdfArray color = Resolve(value) as PdfArray
                ?? throw new InvalidOperationException("An FDF annotation color is not an array.");
            double[] components = color.Select(Number).ToArray();
            if (components.Length != 3 || components.Any(component => component is < 0 or > 1))
                throw new InvalidOperationException("An FDF annotation RGB color is invalid.");
            return $"#{(int)Math.Round(components[0] * 255):X2}{(int)Math.Round(components[1] * 255):X2}{(int)Math.Round(components[2] * 255):X2}";
        }

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

    /// <summary>Writes selected fields and annotations from selected pages as deterministic FDF.</summary>
    public static byte[] Write(PdfFormDataSet data, IEnumerable<string> fieldNames,
        IEnumerable<int> annotationPageIndexes)
    {
        ArgumentNullException.ThrowIfNull(data);
        return Write(data.SelectFields(fieldNames).SelectAnnotationPages(annotationPageIndexes));
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
        PdfIndirectReference? sourceSpecification = null;
        PdfIndirectReference? embeddedSource = null;
        if (data.EmbeddedSourcePdf is ReadOnlyMemory<byte> sourceBytes)
        {
            if (sourceBytes.IsEmpty || sourceBytes.Length > 256 * 1024 * 1024
                || !sourceBytes.Span.StartsWith("%PDF-"u8))
                throw new ArgumentException(
                    "An embedded FDF source must be a PDF no larger than 256 MB.", nameof(data));
            sourceSpecification = new PdfIndirectReference(2, 0);
            embeddedSource = new PdfIndirectReference(3, 0);
            fdfEntries.Add(new(Name("F"), sourceSpecification));
        }
        else if (data.SourcePdfPath is not null)
            fdfEntries.Add(new(Name("F"), String(data.SourcePdfPath)));
        if (data.Annotations.Count > 0)
            fdfEntries.Add(new(Name("Annots"), new PdfArray(
                data.Annotations.Select(annotation => (PdfObject)Annotation(annotation)))));
        var root = new PdfDictionary([
            new(Name("Type"), Name("Catalog")),
            new(Name("FDF"), new PdfDictionary(fdfEntries))]);
        using var output = new MemoryStream();
        output.Write("%FDF-1.2\n%\xE2\xE3\xCF\xD3\n"u8);
        var offsets = new List<long>();
        offsets.Add(output.Position);
        PdfObjectWriter.Write(output, new PdfIndirectObject(1, 0, root, (int)offsets[0]));
        if (sourceSpecification is not null && embeddedSource is not null)
        {
            string name = data.SourcePdfPath ?? "source.pdf";
            offsets.Add(output.Position);
            PdfObjectWriter.Write(output, new PdfIndirectObject(2, 0, new PdfDictionary([
                new(Name("Type"), Name("Filespec")),
                new(Name("F"), String(name)),
                new(Name("UF"), String(name)),
                new(Name("EF"), new PdfDictionary([
                    new(Name("F"), embeddedSource), new(Name("UF"), embeddedSource)]))
            ]), (int)offsets[1]));
            offsets.Add(output.Position);
            ReadOnlyMemory<byte> bytes = data.EmbeddedSourcePdf!.Value;
            PdfObjectWriter.Write(output, new PdfIndirectObject(3, 0, new PdfStream(
                new PdfDictionary([new(Name("Type"), Name("EmbeddedFile"))]), bytes.Span),
                (int)offsets[2]));
        }
        long xrefOffset = output.Position;
        WriteAscii(output, $"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
            WriteAscii(output, $"{offset:0000000000} 00000 n \n");
        output.Write("trailer\n"u8);
        PdfObjectWriter.Write(output, new PdfDictionary([
            new(Name("Root"), new PdfIndirectReference(1, 0)),
            new(Name("Size"), new PdfInteger(offsets.Count + 1))]));
        WriteAscii(output, $"\nstartxref\n{xrefOffset.ToString(CultureInfo.InvariantCulture)}\n%%EOF\n");
        return output.ToArray();
    }

    private static PdfDictionary Annotation(PdfFormDataAnnotation annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation.Subtype) || annotation.PageIndex < 0
            || annotation.Rectangle.Count != 4
            || annotation.Rectangle.Any(value => !double.IsFinite(value))
            || annotation.Rectangle[2] < annotation.Rectangle[0]
            || annotation.Rectangle[3] < annotation.Rectangle[1])
            throw new ArgumentException("An FDF annotation has invalid required data.", nameof(annotation));
        if (annotation.Opacity is < 0 or > 1 || annotation.Opacity is double.NaN)
            throw new ArgumentException("An FDF annotation opacity must be between zero and one.", nameof(annotation));
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(Name("Subtype"), Name(annotation.Subtype)),
            new(Name("Page"), new PdfInteger(annotation.PageIndex)),
            new(Name("Rect"), new PdfArray(annotation.Rectangle.Select(Number)))
        };
        AddText(entries, "NM", annotation.Name);
        AddText(entries, "Contents", annotation.Contents);
        AddText(entries, "T", annotation.Author);
        AddText(entries, "Subj", annotation.Subject);
        AddText(entries, "CreationDate", annotation.CreationDate);
        AddText(entries, "M", annotation.ModifiedDate);
        AddText(entries, "IRT", annotation.ReplyToName);
        if (annotation.Color is string color)
        {
            if (color.Length != 7 || color[0] != '#'
                || !int.TryParse(color.AsSpan(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int rgb))
                throw new ArgumentException("An FDF annotation color must be #RRGGBB.", nameof(annotation));
            entries.Add(new(Name("C"), new PdfArray([
                new PdfReal(((rgb >> 16) & 255) / 255d),
                new PdfReal(((rgb >> 8) & 255) / 255d),
                new PdfReal((rgb & 255) / 255d)])));
        }
        if (annotation.Opacity is double opacity)
            entries.Add(new(Name("CA"), new PdfReal(opacity)));
        return new PdfDictionary(entries);
    }

    private static void AddText(
        ICollection<KeyValuePair<PdfName, PdfObject>> entries, string key, string? value)
    {
        if (value is not null) entries.Add(new(Name(key), String(value)));
    }
    private static PdfObject Number(double value) => value == Math.Truncate(value)
        && value is >= long.MinValue and <= long.MaxValue
        ? new PdfInteger((long)value) : new PdfReal(value);

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
