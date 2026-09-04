using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads portable XFA locale definitions without loading external resources.</summary>
public static class PdfXfaLocales
{
    private const long MaximumCharacters = 64 * 1024 * 1024;

    /// <summary>Reads the ordered locales from the localeSet packet.</summary>
    public static IReadOnlyList<PdfXfaLocale> Read(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            string.Equals(item.Name, "localeSet", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA data has no localeSet packet.");
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
            ?? throw new InvalidOperationException("The XFA localeSet packet has no root element.");
        if (!root.Name.LocalName.Equals("localeSet", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The XFA localeSet packet has an unexpected root element.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locales = new List<PdfXfaLocale>();
        foreach (XElement element in root.Elements().Where(item =>
            item.Name.LocalName.Equals("locale", StringComparison.OrdinalIgnoreCase)))
        {
            string? name = Attribute(element, "name");
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidOperationException("XFA locale names must be nonempty and unique.");
            locales.Add(new PdfXfaLocale(name, Attribute(element, "desc"),
                Values(element, "numberSymbols", "numberSymbol"),
                Values(element, "numberPatterns", "numberPattern"),
                Values(element, "datePatterns", "datePattern"),
                Values(element, "timePatterns", "timePattern"),
                Values(element, "currencySymbols", "currencySymbol"),
                element.Descendants().Where(item => item.Name.LocalName.Equals(
                    "typeface", StringComparison.OrdinalIgnoreCase))
                    .Select(item => Attribute(item, "name"))
                    .Where(value => !string.IsNullOrWhiteSpace(value)).Cast<string>().ToArray()));
        }
        return Array.AsReadOnly(locales.ToArray());
    }

    private static IReadOnlyList<PdfXfaLocaleValue> Values(
        XElement locale, string containerName, string valueName)
    {
        XElement? container = locale.Elements().FirstOrDefault(item =>
            item.Name.LocalName.Equals(containerName, StringComparison.OrdinalIgnoreCase));
        if (container is null) return [];
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        PdfXfaLocaleValue[] values = [.. container.Elements().Where(item =>
            item.Name.LocalName.Equals(valueName, StringComparison.OrdinalIgnoreCase)).Select(item =>
        {
            string? name = Attribute(item, "name");
            if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                throw new InvalidOperationException(
                    $"XFA {valueName} names must be nonempty and unique within a locale.");
            return new PdfXfaLocaleValue(name, item.Value);
        })];
        return Array.AsReadOnly(values);
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
            name, StringComparison.OrdinalIgnoreCase))?.Value;
}

/// <summary>An inspectable XFA locale definition.</summary>
public sealed record PdfXfaLocale(
    string Name,
    string? Description,
    IReadOnlyList<PdfXfaLocaleValue> NumberSymbols,
    IReadOnlyList<PdfXfaLocaleValue> NumberPatterns,
    IReadOnlyList<PdfXfaLocaleValue> DatePatterns,
    IReadOnlyList<PdfXfaLocaleValue> TimePatterns,
    IReadOnlyList<PdfXfaLocaleValue> CurrencySymbols,
    IReadOnlyList<string> Typefaces);

/// <summary>A named symbol or pattern in an XFA locale.</summary>
public sealed record PdfXfaLocaleValue(string Name, string Value);
