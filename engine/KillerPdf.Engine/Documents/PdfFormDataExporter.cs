using System.Globalization;
using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>Extracts portable AcroForm values for FDF or XFDF export.</summary>
public static class PdfFormDataExporter
{
    /// <summary>Extracts every exportable terminal field in document order.</summary>
    public static PdfFormDataSet Export(PdfDocument document, string? sourcePdfPath = null,
        bool includeNoExportFields = false, bool includeAnnotations = true)
    {
        ArgumentNullException.ThrowIfNull(document);
        int pageCount = PdfDocumentInformation.Read(document).PageCount;
        var fields = new List<PdfFormDataField>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            foreach (PdfFormWidgetInfo widget in PdfFormWidgetReader.ReadPage(document, pageIndex))
            {
                if (!seen.Add(widget.FieldName) || !includeNoExportFields && (widget.Flags & 4) != 0)
                    continue;
                fields.Add(new PdfFormDataField
                {
                    Name = widget.FieldName,
                    MappingName = string.IsNullOrEmpty(widget.MappingName) ? null : widget.MappingName,
                    Values = Values(widget.Values, widget.Value),
                    DefaultValues = Values(widget.DefaultValues, widget.DefaultValue)
                });
            }
        }
        return new PdfFormDataSet
        {
            SourcePdfPath = sourcePdfPath,
            Fields = Array.AsReadOnly(fields.ToArray()),
            Annotations = includeAnnotations ? Annotations(document) : []
        };
    }

    private static IReadOnlyList<string> Values(IReadOnlyList<string> values, string scalar) =>
        values.Count > 0 ? Array.AsReadOnly(values.ToArray()) : [scalar];

    private static IReadOnlyList<PdfFormDataAnnotation> Annotations(PdfDocument document)
    {
        var pending = new List<PendingAnnotation>();
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var identitiesByObject = new Dictionary<int, string>();
        foreach (PdfPageTreeEntry page in PdfPageTree.Read(document).Pages)
        {
            if (!page.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue))
                continue;
            PdfArray annotations = Resolve(document, annotationsValue) as PdfArray
                ?? throw new InvalidOperationException(
                    $"Page {page.Index + 1} /Annots value is not an array.");
            for (int index = 0; index < annotations.Count; index++)
            {
                PdfIndirectReference? reference = annotations[index] as PdfIndirectReference;
                PdfDictionary annotation = Resolve(document, annotations[index]) as PdfDictionary
                    ?? throw new InvalidOperationException(
                        $"Page {page.Index + 1} annotation {index + 1} is not a dictionary.");
                string subtype = NameValue(document, annotation, "Subtype") ?? string.Empty;
                if (!SupportedAnnotationSubtype(subtype)) continue;
                string baseIdentity = Text(document, annotation, "NM")
                    ?? $"annotation-{page.Index + 1}-{index + 1}";
                string identity = UniqueIdentity(baseIdentity, identities);
                if (reference is not null) identitiesByObject[reference.ObjectNumber] = identity;
                pending.Add(new PendingAnnotation(page.Index, annotation, identity,
                    ReplyTarget(annotation)));
            }
        }
        return Array.AsReadOnly(pending.Select(item => new PdfFormDataAnnotation
        {
            Subtype = NameValue(document, item.Dictionary, "Subtype")!,
            PageIndex = item.PageIndex,
            Rectangle = Rectangle(document, item.Dictionary),
            Name = item.Identity,
            Contents = Text(document, item.Dictionary, "Contents"),
            Author = Text(document, item.Dictionary, "T"),
            Subject = Text(document, item.Dictionary, "Subj"),
            Color = Color(document, item.Dictionary),
            Opacity = OptionalNumber(document, item.Dictionary, "CA"),
            CreationDate = Text(document, item.Dictionary, "CreationDate"),
            ModifiedDate = Text(document, item.Dictionary, "M"),
            ReplyToName = item.ReplyObjectNumber is int reply
                && identitiesByObject.TryGetValue(reply, out string? identity) ? identity : null
        }).ToArray());
    }

    private static bool SupportedAnnotationSubtype(string value) => value is
        "Highlight" or "Underline" or "StrikeOut" or "Squiggly" or "Square" or "Circle" or "Text";

    private static string UniqueIdentity(string candidate, ISet<string> identities)
    {
        if (identities.Add(candidate)) return candidate;
        for (int suffix = 2; ; suffix++)
        {
            string unique = candidate + "-" + suffix.ToString(CultureInfo.InvariantCulture);
            if (identities.Add(unique)) return unique;
        }
    }

    private static int? ReplyTarget(PdfDictionary annotation) =>
        annotation.TryGetValue(Name("IRT"), out PdfObject? value)
            && value is PdfIndirectReference reference ? reference.ObjectNumber : null;

    private static IReadOnlyList<double> Rectangle(PdfDocument document, PdfDictionary annotation)
    {
        PdfArray rectangle = annotation.TryGetValue(Name("Rect"), out PdfObject? value)
            ? Resolve(document, value) as PdfArray
                ?? throw new InvalidOperationException("An exported annotation /Rect is not an array.")
            : throw new InvalidOperationException("An exported annotation has no /Rect array.");
        if (rectangle.Count != 4)
            throw new InvalidOperationException("An exported annotation /Rect has the wrong size.");
        double[] result = [.. rectangle.Select(value => Number(document, value))];
        if (result[2] < result[0] || result[3] < result[1])
            throw new InvalidOperationException("An exported annotation /Rect is invalid.");
        return Array.AsReadOnly(result);
    }

    private static string? Color(PdfDocument document, PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(Name("C"), out PdfObject? value)) return null;
        PdfArray color = Resolve(document, value) as PdfArray
            ?? throw new InvalidOperationException("An exported annotation /C value is not an array.");
        if (color.Count != 3)
            throw new NotSupportedException("Only RGB annotation colors can be exported.");
        int[] components = [.. color.Select(component =>
        {
            double number = Number(document, component);
            if (number is < 0 or > 1)
                throw new InvalidOperationException(
                    "An exported annotation color component is outside zero through one.");
            return (int)Math.Round(number * 255, MidpointRounding.AwayFromZero);
        })];
        return $"#{components[0]:X2}{components[1]:X2}{components[2]:X2}";
    }

    private static double? OptionalNumber(PdfDocument document,
        PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value) ? Number(document, value) : null;

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value) switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException(
                "An exported annotation contains a nonnumeric value.")
        };

    private static string? Text(PdfDocument document,
        PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
        return Resolve(document, value) is PdfString text
            ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                $"An annotation /{key} value")
            : throw new InvalidOperationException($"An annotation /{key} value is not a string.");
    }

    private static string? NameValue(PdfDocument document,
        PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(document, value) is PdfName name ? name.ValueAsLatin1() : null;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An annotation reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));

    private sealed record PendingAnnotation(int PageIndex, PdfDictionary Dictionary,
        string Identity, int? ReplyObjectNumber);
}
