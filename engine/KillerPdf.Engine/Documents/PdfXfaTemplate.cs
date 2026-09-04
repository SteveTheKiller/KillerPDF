using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;

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
                    element.Name.LocalName, "format", StringComparison.OrdinalIgnoreCase)))
            {
                ChoiceOptions = ChoiceOptions(field, path),
                Description = AssistText(field),
                Appearance = Appearance(field, path)
            });
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
                    picture?.Value)
                {
                    Activity = kind == PdfXfaTemplateBehaviorKind.Event
                        ? EmptyToNull(Attribute(behavior, "activity")) : null
                });
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

    private static IReadOnlyList<PdfXfaChoiceOption> ChoiceOptions(XElement field, string path)
    {
        XElement[] sets = [.. field.Elements().Where(element =>
            element.Name.LocalName.Equals("items", StringComparison.OrdinalIgnoreCase))];
        if (sets.Length == 0) return [];
        if (sets.Length > 2)
            throw new InvalidOperationException($"XFA field '{path}' has too many item lists.");
        string[] First(XElement set) => [.. set.Elements().Select(item => item.Value)];
        XElement displaySet = sets.FirstOrDefault(set => Attribute(set, "save") != "1") ?? sets[0];
        XElement exportSet = sets.FirstOrDefault(set => Attribute(set, "save") == "1") ?? displaySet;
        string[] displays = First(displaySet);
        string[] exports = First(exportSet);
        if (displays.Length != exports.Length)
            throw new InvalidOperationException(
                $"XFA field '{path}' has mismatched display and saved item lists.");
        return Array.AsReadOnly(exports.Select((value, index) =>
            new PdfXfaChoiceOption(value, displays[index])).ToArray());
    }

    private static string? AssistText(XElement field)
    {
        XElement? assist = field.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("assist", StringComparison.OrdinalIgnoreCase));
        XElement? text = assist?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("toolTip", StringComparison.OrdinalIgnoreCase))
            ?? assist?.Elements().FirstOrDefault(element =>
                element.Name.LocalName.Equals("speak", StringComparison.OrdinalIgnoreCase));
        return string.IsNullOrWhiteSpace(text?.Value) ? null : text.Value;
    }

    private static PdfXfaFieldAppearance Appearance(XElement field, string path)
    {
        XElement? font = Child(field, "font");
        XElement? paragraph = Child(field, "para");
        XElement? fill = Child(field, "fill");
        XElement? border = Child(field, "border");
        return new PdfXfaFieldAppearance
        {
            Typeface = EmptyToNull(Attribute(font, "typeface")),
            FontSize = Measurement(Attribute(font, "size"), path, "font size"),
            TextColor = Color(Child(font, "fill"), path, "text color"),
            BackgroundColor = Color(fill, path, "background color"),
            BorderColor = Color(border, path, "border color"),
            Alignment = Alignment(Attribute(paragraph, "hAlign"), path)
        };
    }

    private static XElement? Child(XElement? parent, string name) =>
        parent?.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? EmptyToNull(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static double? Measurement(string? source, string path, string label)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        string value = source.Trim();
        string number = value.EndsWith("pt", StringComparison.OrdinalIgnoreCase)
            ? value[..^2] : value;
        if (!double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double result) || !double.IsFinite(result) || result <= 0)
            throw new InvalidOperationException($"XFA field '{path}' has an invalid {label}.");
        return result;
    }

    private static PdfRgbColor? Color(XElement? container, string path, string label)
    {
        XElement? color = container is not null
            && container.Name.LocalName.Equals("color", StringComparison.OrdinalIgnoreCase)
                ? container : Child(container, "color");
        string? source = Attribute(color, "value");
        if (string.IsNullOrWhiteSpace(source)) return null;
        string[] parts = source.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 3 || parts.Any(part => !byte.TryParse(part,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out _)))
            throw new InvalidOperationException($"XFA field '{path}' has an invalid {label}.");
        return new PdfRgbColor(
            byte.Parse(parts[0], CultureInfo.InvariantCulture) / 255d,
            byte.Parse(parts[1], CultureInfo.InvariantCulture) / 255d,
            byte.Parse(parts[2], CultureInfo.InvariantCulture) / 255d);
    }

    private static PdfTextFieldAlignment? Alignment(string? source, string path)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        if (Enum.TryParse(source, ignoreCase: true, out PdfTextFieldAlignment alignment)
            && Enum.IsDefined(alignment)) return alignment;
        throw new NotSupportedException(
            $"XFA field '{path}' uses unsupported horizontal alignment '{source}'.");
    }
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
    bool HasFormatting)
{
    /// <summary>Gets ordered saved and displayed choice-list values.</summary>
    public IReadOnlyList<PdfXfaChoiceOption> ChoiceOptions { get; init; } = [];
    /// <summary>Gets the field's user-facing assist text.</summary>
    public string? Description { get; init; }
    /// <summary>Gets safely representable field appearance metadata.</summary>
    public PdfXfaFieldAppearance Appearance { get; init; } = new();
}

/// <summary>Safely representable appearance metadata declared by an XFA field.</summary>
public sealed record PdfXfaFieldAppearance
{
    /// <summary>Gets the declared typeface name without loading external font resources.</summary>
    public string? Typeface { get; init; }
    /// <summary>Gets the declared font size in points.</summary>
    public double? FontSize { get; init; }
    /// <summary>Gets the declared text color.</summary>
    public PdfRgbColor? TextColor { get; init; }
    /// <summary>Gets the declared field background color.</summary>
    public PdfRgbColor? BackgroundColor { get; init; }
    /// <summary>Gets the declared field border color.</summary>
    public PdfRgbColor? BorderColor { get; init; }
    /// <summary>Gets the declared horizontal text alignment.</summary>
    public PdfTextFieldAlignment? Alignment { get; init; }
}

/// <summary>One saved and displayed XFA choice-list item.</summary>
public sealed record PdfXfaChoiceOption(string ExportValue, string DisplayValue);

/// <summary>An inspectable XFA field behavior definition.</summary>
public sealed record PdfXfaTemplateBehavior(
    string FieldPath,
    PdfXfaTemplateBehaviorKind Kind,
    string? ScriptContentType,
    string? Script,
    string? Picture)
{
    /// <summary>Gets the declared event activity when this behavior is an event.</summary>
    public string? Activity { get; init; }
}

/// <summary>The supported categories of XFA field behavior metadata.</summary>
public enum PdfXfaTemplateBehaviorKind
{
    /// <summary>A calculated field value.</summary>
    Calculate,
    /// <summary>A field validation rule.</summary>
    Validate,
    /// <summary>A field display or data-formatting rule.</summary>
    Format,
    /// <summary>A form event that remains caller-selected and sandboxed.</summary>
    Event
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
        if (value.Equals("event", StringComparison.OrdinalIgnoreCase))
        {
            kind = PdfXfaTemplateBehaviorKind.Event;
            return true;
        }
        kind = default;
        return false;
    }
}
