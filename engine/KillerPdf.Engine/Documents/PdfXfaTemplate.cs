using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Inspects XFA template fields and bindings without executing form code.</summary>
public static class PdfXfaTemplate
{
    private const long MaximumCharacters = 64 * 1024 * 1024;

    /// <summary>Reads the template packet's ordered field definitions.</summary>
    public static PdfXfaTemplateInfo Read(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            string.Equals(item.Name, "template", StringComparison.OrdinalIgnoreCase))
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
        if (!string.Equals(root.Name.LocalName, "template", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The XFA template packet has an unexpected root element.");

        var fields = new List<PdfXfaTemplateField>();
        var behaviors = new List<PdfXfaTemplateBehavior>();
        foreach (XElement field in root.Descendants().Where(element =>
            string.Equals(element.Name.LocalName, "field", StringComparison.OrdinalIgnoreCase)))
        {
            string? name = Attribute(field, "name");
            if (string.IsNullOrWhiteSpace(name))
                throw new InvalidOperationException("An XFA template field has no name.");
            string path = string.Join('.', field.AncestorsAndSelf().Reverse()
                .Where(element => element == field || string.Equals(
                    element.Name.LocalName, "subform", StringComparison.OrdinalIgnoreCase))
                .Select(element => Attribute(element, "name"))
                .Where(value => !string.IsNullOrWhiteSpace(value)));
            XElement? bind = field.Elements().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "bind", StringComparison.OrdinalIgnoreCase));
            XElement? ui = field.Elements().FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "ui", StringComparison.OrdinalIgnoreCase));
            string? control = ui?.Elements().FirstOrDefault()?.Name.LocalName;
            fields.Add(new PdfXfaTemplateField(
                path,
                name,
                Attribute(bind, "ref"),
                control,
                field.Descendants().Any(element => string.Equals(
                    element.Name.LocalName, "calculate", StringComparison.OrdinalIgnoreCase)),
                field.Descendants().Any(element => string.Equals(
                    element.Name.LocalName, "validate", StringComparison.OrdinalIgnoreCase)),
                field.Descendants().Any(element => string.Equals(
                    element.Name.LocalName, "format", StringComparison.OrdinalIgnoreCase))));
            foreach (XElement behavior in field.Elements().Where(element =>
                PdfXfaTemplateBehaviorKindExtensions.TryParse(
                    element.Name.LocalName, out _)))
            {
                _ = PdfXfaTemplateBehaviorKindExtensions.TryParse(
                    behavior.Name.LocalName, out PdfXfaTemplateBehaviorKind kind);
                XElement? script = behavior.Descendants().FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "script",
                        StringComparison.OrdinalIgnoreCase));
                XElement? picture = behavior.Descendants().FirstOrDefault(element =>
                    string.Equals(element.Name.LocalName, "picture",
                        StringComparison.OrdinalIgnoreCase));
                behaviors.Add(new PdfXfaTemplateBehavior(
                    path,
                    kind,
                    Attribute(script, "contentType"),
                    script?.Value,
                    picture?.Value));
            }
        }
        int scriptCount = root.Descendants().Count(element => string.Equals(
            element.Name.LocalName, "script", StringComparison.OrdinalIgnoreCase));
        return new PdfXfaTemplateInfo(Array.AsReadOnly(fields.ToArray()), scriptCount)
        {
            Behaviors = Array.AsReadOnly(behaviors.ToArray())
        };
    }

    private static string? Attribute(XElement? element, string name) =>
        element?.Attributes().FirstOrDefault(attribute => string.Equals(
            attribute.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>A safe summary of an XFA template packet.</summary>
public sealed record PdfXfaTemplateInfo(
    IReadOnlyList<PdfXfaTemplateField> Fields, int ScriptCount)
{
    /// <summary>Inspectable field behavior definitions. Scripts are never executed.</summary>
    public IReadOnlyList<PdfXfaTemplateBehavior> Behaviors { get; init; } = [];
}

/// <summary>One field declared by an XFA template.</summary>
public sealed record PdfXfaTemplateField(
    string Path,
    string Name,
    string? Binding,
    string? ControlType,
    bool HasCalculation,
    bool HasValidation,
    bool HasFormatting);

/// <summary>An inspectable XFA field behavior definition.</summary>
public sealed record PdfXfaTemplateBehavior(
    string FieldPath,
    PdfXfaTemplateBehaviorKind Kind,
    string? ScriptContentType,
    string? Script,
    string? Picture);

/// <summary>The supported categories of XFA field behavior metadata.</summary>
public enum PdfXfaTemplateBehaviorKind
{
    /// <summary>A calculated field value.</summary>
    Calculate,
    /// <summary>A field validation rule.</summary>
    Validate,
    /// <summary>A field display or data-formatting rule.</summary>
    Format
}

internal static class PdfXfaTemplateBehaviorKindExtensions
{
    internal static bool TryParse(string value, out PdfXfaTemplateBehaviorKind kind)
    {
        if (value.Equals("calculate", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfXfaTemplateBehaviorKind.Calculate;
            return true;
        }
        if (value.Equals("validate", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfXfaTemplateBehaviorKind.Validate;
            return true;
        }
        if (value.Equals("format", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfXfaTemplateBehaviorKind.Format;
            return true;
        }
        kind = default;
        return false;
    }
}
