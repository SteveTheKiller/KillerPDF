using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Plans bounded top-to-bottom XFA flow with repeated data and pagination.</summary>
public static class PdfXfaFlowLayout
{
    private const int MaximumPlacements = 10_000;

    /// <summary>Flows bound fields into explicit page dimensions using top-left coordinates.</summary>
    public static PdfXfaFlowLayoutPlan Plan(PdfXfaInfo info, PdfFormDataSet data,
        double pageWidth, double pageHeight, double margin = 36)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        if (!double.IsFinite(pageWidth) || !double.IsFinite(pageHeight)
            || !double.IsFinite(margin) || pageWidth <= margin * 2
            || pageHeight <= margin * 2 || margin < 0)
            throw new ArgumentOutOfRangeException(nameof(pageWidth),
                "Flowed XFA pages require finite positive dimensions and margins.");
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            item.Name.Equals("template", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA data has no template packet.");
        XDocument document = Load(packet);
        Dictionary<string, PdfFormDataField> values = data.Fields.ToDictionary(
            field => field.Name, StringComparer.Ordinal);
        var placements = new List<PdfXfaFlowFieldPlacement>();
        int pageIndex = 0;
        double cursor = margin;
        bool breakAfterPrevious = false;
        foreach (XElement field in document.Descendants().Where(element =>
            element.Name.LocalName.Equals("field", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? container = field.Ancestors().FirstOrDefault(element =>
                element.Name.LocalName.Equals("subform", StringComparison.OrdinalIgnoreCase)
                && IsFlowed(Attribute(element, "layout")));
            if (container is null) continue;
            string path = Path(field);
            double x = margin + Measure(Attribute(field, "x"), "x", path, false);
            double gap = Measure(Attribute(field, "y"), "y", path, false);
            double width = Measure(Attribute(field, "w"), "w", path, true);
            double height = Measure(Attribute(field, "h"), "h", path, true);
            if (width <= 0 || height <= 0 || x + width > pageWidth - margin
                || height > pageHeight - margin * 2)
                throw new InvalidOperationException(
                    $"XFA flowed field '{path}' does not fit the requested page area.");
            string dataName = BindingName(field) ?? path;
            string[] repeated = values.TryGetValue(dataName, out PdfFormDataField? dataField)
                && dataField.Values.Count > 0 ? [.. dataField.Values] : [string.Empty];
            if ((breakAfterPrevious || HasPageBreak(field, "breakBefore")) && cursor > margin)
            {
                pageIndex++;
                cursor = margin;
            }
            breakAfterPrevious = false;
            for (int occurrence = 0; occurrence < repeated.Length; occurrence++)
            {
                if (placements.Count >= MaximumPlacements)
                    throw new InvalidOperationException(
                        $"An XFA flow cannot contain more than {MaximumPlacements} placements.");
                if (cursor + gap + height > pageHeight - margin)
                {
                    pageIndex++;
                    cursor = margin;
                }
                cursor += gap;
                placements.Add(new PdfXfaFlowFieldPlacement(path, occurrence, pageIndex,
                    x, cursor, width, height, repeated[occurrence]));
                cursor += height;
            }
            breakAfterPrevious = HasPageBreak(field, "breakAfter");
        }
        return new PdfXfaFlowLayoutPlan(pageIndex + 1,
            Array.AsReadOnly(placements.ToArray()));
    }

    private static XDocument Load(PdfXfaPacket packet)
    {
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 64 * 1024 * 1024,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        return document.Root is null
            ? throw new InvalidOperationException("The XFA template packet has no root element.")
            : document;
    }

    private static bool IsFlowed(string? layout) => layout is not null
        && (layout.Equals("tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("lr-tb", StringComparison.OrdinalIgnoreCase)
            || layout.Equals("rl-tb", StringComparison.OrdinalIgnoreCase));

    private static bool HasPageBreak(XElement field, string name) =>
        field.Elements().Any(element => element.Name.LocalName.Equals(
            name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Attribute(element, "targetType"), "pageArea",
                StringComparison.OrdinalIgnoreCase));

    private static string? BindingName(XElement field)
    {
        XElement? bind = field.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("bind", StringComparison.OrdinalIgnoreCase));
        string? value = bind is null ? null : Attribute(bind, "ref");
        const string record = "$record.";
        return value?.StartsWith(record, StringComparison.Ordinal) == true
            ? value[record.Length..] : value;
    }

    private static double Measure(string? source, string attribute, string path, bool required)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (required) throw new InvalidOperationException(
                $"XFA field '{path}' has no {attribute} measurement.");
            return 0;
        }
        string value = source.Trim();
        int split = 0;
        while (split < value.Length && (char.IsAsciiDigit(value[split])
            || value[split] is '+' or '-' or '.' or 'e' or 'E')) split++;
        if (!double.TryParse(value[..split], NumberStyles.Float,
                CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number))
            throw new InvalidOperationException(
                $"XFA field '{path}' has an invalid {attribute} measurement.");
        double scale = value[split..].ToLowerInvariant() switch
        {
            "" or "pt" => 1,
            "in" => 72,
            "mm" => 72 / 25.4,
            "cm" => 72 / 2.54,
            _ => throw new NotSupportedException(
                $"XFA field '{path}' uses an unsupported {attribute} unit.")
        };
        return number * scale;
    }

    private static string Path(XElement field)
    {
        string path = string.Join('.', field.AncestorsAndSelf().Reverse()
            .Where(element => element == field || element.Name.LocalName.Equals(
                "subform", StringComparison.OrdinalIgnoreCase))
            .Select(element => Attribute(element, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("An XFA template field has no path.") : path;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
            name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>A paginated top-to-bottom XFA flow plan.</summary>
public sealed record PdfXfaFlowLayoutPlan(
    int PageCount, IReadOnlyList<PdfXfaFlowFieldPlacement> Placements);

/// <summary>One repeated flowed field using top-left page coordinates in PDF points.</summary>
public sealed record PdfXfaFlowFieldPlacement(
    string FieldPath, int OccurrenceIndex, int PageIndex,
    double X, double Y, double Width, double Height, string Value);
