using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Plans positioned static XFA fields in PDF points without rendering or executing scripts.</summary>
public static class PdfXfaStaticLayout
{
    private const long MaximumCharacters = 64 * 1024 * 1024;

    /// <summary>Resolves positioned field geometry from the template packet.</summary>
    public static PdfXfaStaticLayoutPlan Plan(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (info.FormType == PdfXfaFormType.Dynamic)
            throw new NotSupportedException("Dynamic XFA forms require a flowed layout engine.");
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            item.Name.Equals("template", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA data has no template packet.");
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidOperationException("The XFA template packet has no root element.");

        var placements = new List<PdfXfaFieldPlacement>();
        var unsupported = new List<string>();
        foreach (XElement field in root.Descendants().Where(item =>
            item.Name.LocalName.Equals("field", StringComparison.OrdinalIgnoreCase)))
        {
            string path = Path(field);
            XElement? flowed = field.Ancestors().FirstOrDefault(item =>
                item.Name.LocalName.Equals("subform", StringComparison.OrdinalIgnoreCase)
                && IsFlowed(Attribute(item, "layout")));
            if (flowed is not null)
            {
                unsupported.Add(path);
                continue;
            }
            double x = Measure(Attribute(field, "x"), "x", path, required: false);
            double y = Measure(Attribute(field, "y"), "y", path, required: false);
            foreach (XElement parent in field.Ancestors().Where(item =>
                item.Name.LocalName.Equals("subform", StringComparison.OrdinalIgnoreCase)))
            {
                x += Measure(Attribute(parent, "x"), "x", path, required: false);
                y += Measure(Attribute(parent, "y"), "y", path, required: false);
            }
            double width = Measure(Attribute(field, "w"), "w", path, required: true);
            double height = Measure(Attribute(field, "h"), "h", path, required: true);
            if (width <= 0 || height <= 0)
                throw new InvalidOperationException($"XFA field '{path}' must have positive dimensions.");
            placements.Add(new PdfXfaFieldPlacement(path, 0, x, y, width, height));
        }
        return new PdfXfaStaticLayoutPlan(Array.AsReadOnly(placements.ToArray()),
            Array.AsReadOnly(unsupported.ToArray()));
    }

    private static bool IsFlowed(string? layout) => layout is not null
        && !layout.Equals("position", StringComparison.OrdinalIgnoreCase);

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
            throw new InvalidOperationException($"XFA field '{path}' has an invalid {attribute} measurement.");
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
            .Where(item => item == field || item.Name.LocalName.Equals(
                "subform", StringComparison.OrdinalIgnoreCase))
            .Select(item => Attribute(item, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("An XFA template field has no path.") : path;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
            name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>A safe static XFA layout plan.</summary>
public sealed record PdfXfaStaticLayoutPlan(
    IReadOnlyList<PdfXfaFieldPlacement> Placements,
    IReadOnlyList<string> UnsupportedFlowedFieldPaths);

/// <summary>A positioned XFA field using top-left page coordinates in PDF points.</summary>
public sealed record PdfXfaFieldPlacement(
    string FieldPath, int PageIndex, double X, double Y, double Width, double Height);
