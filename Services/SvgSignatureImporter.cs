using System.Globalization;
using System.IO;
using System.Xml;
using System.Xml.Linq;

namespace KillerPDF.Services;

internal sealed record SvgSignatureDocument(
    string Source, double Width, double Height, IReadOnlyList<string> Paths);

/// <summary>Reads passive path-only SVG signatures without resolving external content.</summary>
internal static class SvgSignatureImporter
{
    internal static SvgSignatureDocument Import(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string source = File.ReadAllText(path);
        using var reader = XmlReader.Create(new StringReader(source), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidDataException("The SVG document is empty.");
        if (root.Name.LocalName != "svg")
            throw new InvalidDataException("The signature file does not contain an SVG root.");
        if (root.Descendants().Any(element => element.Name.LocalName is
                "script" or "image" or "use" or "foreignObject")
            || root.DescendantsAndSelf().Attributes().Any(attribute =>
                attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase)
                || attribute.Name.LocalName is "href"))
            throw new InvalidDataException("SVG signatures cannot contain active or external content.");

        (double width, double height) = Dimensions(root);
        string[] paths = root.Descendants()
            .Where(element => element.Name.LocalName == "path")
            .Select(element => (string?)element.Attribute("d"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (paths.Length == 0)
            throw new InvalidDataException("The SVG signature does not contain any paths.");
        return new SvgSignatureDocument(source, width, height, Array.AsReadOnly(paths));
    }

    private static (double Width, double Height) Dimensions(XElement root)
    {
        string? viewBox = (string?)root.Attribute("viewBox");
        if (!string.IsNullOrWhiteSpace(viewBox))
        {
            double[] values = viewBox.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseNumber).ToArray();
            if (values.Length == 4 && values[2] > 0 && values[3] > 0)
                return (values[2], values[3]);
        }
        double width = ParseLength((string?)root.Attribute("width"));
        double height = ParseLength((string?)root.Attribute("height"));
        if (width <= 0 || height <= 0)
            throw new InvalidDataException("The SVG signature requires a positive viewBox or size.");
        return (width, height);
    }

    private static double ParseLength(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return 0;
        int end = 0;
        while (end < value.Length && (char.IsDigit(value[end])
            || value[end] is '.' or '+' or '-' or 'e' or 'E')) end++;
        return end == 0 ? 0 : ParseNumber(value[..end]);
    }

    private static double ParseNumber(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double result) || !double.IsFinite(result))
            throw new InvalidDataException("The SVG signature contains an invalid dimension.");
        return result;
    }
}
