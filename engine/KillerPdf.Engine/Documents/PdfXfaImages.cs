using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Inspects embedded and referenced XFA image values without resolving external sources.</summary>
public static class PdfXfaImages
{
    private const int MaximumDecodedBytes = 64 * 1024 * 1024;

    /// <summary>Reads image values in template order while preserving their encoded payloads.</summary>
    public static IReadOnlyList<PdfXfaImageValue> Read(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            item.Name.Equals("template", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA data has no template packet.");
        XDocument document = Load(packet);
        var images = new List<PdfXfaImageValue>();
        foreach (XElement image in document.Descendants().Where(element =>
            element.Name.LocalName.Equals("image", StringComparison.OrdinalIgnoreCase)))
        {
            XElement? field = image.Ancestors().FirstOrDefault(element =>
                element.Name.LocalName.Equals("field", StringComparison.OrdinalIgnoreCase));
            if (field is null) continue;
            string path = Path(field);
            string? contentType = Attribute(image, "contentType")?.Trim();
            string? href = Attribute(image, "href")?.Trim();
            string transferEncoding = Attribute(image, "transferEncoding")?.Trim() ?? "base64";
            byte[]? data = null;
            if (string.IsNullOrWhiteSpace(href) && !string.IsNullOrWhiteSpace(image.Value))
            {
                if (!transferEncoding.Equals("base64", StringComparison.OrdinalIgnoreCase))
                    throw new NotSupportedException(
                        $"XFA image field '{path}' uses unsupported transfer encoding '{transferEncoding}'.");
                try { data = Convert.FromBase64String(image.Value); }
                catch (FormatException exception)
                {
                    throw new FormatException(
                        $"XFA image field '{path}' contains invalid base64 data.", exception);
                }
                if (data.Length > MaximumDecodedBytes)
                    throw new InvalidOperationException(
                        $"XFA image field '{path}' exceeds the decoded image limit.");
            }
            images.Add(new PdfXfaImageValue(path, Empty(contentType), Empty(href),
                transferEncoding, data is null ? ReadOnlyMemory<byte>.Empty : data));
        }
        return Array.AsReadOnly(images.ToArray());
    }

    private static XDocument Load(PdfXfaPacket packet)
    {
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 96 * 1024 * 1024,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        return document.Root is null
            ? throw new InvalidOperationException("The XFA template packet has no root element.")
            : document;
    }

    private static string Path(XElement field)
    {
        string path = string.Join('.', field.AncestorsAndSelf().Reverse()
            .Where(element => element == field || element.Name.LocalName.Equals(
                "subform", StringComparison.OrdinalIgnoreCase))
            .Select(element => Attribute(element, "name"))
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(path)
            ? throw new InvalidOperationException("An XFA image field has no path.") : path;
    }

    private static string? Attribute(XElement element, string name) =>
        element.Attributes().FirstOrDefault(attribute => attribute.Name.LocalName.Equals(
            name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Empty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>One embedded or externally referenced XFA image value.</summary>
public sealed record PdfXfaImageValue(
    string FieldPath,
    string? ContentType,
    string? Href,
    string TransferEncoding,
    ReadOnlyMemory<byte> Data)
{
    /// <summary>Gets whether the image requires external resolution.</summary>
    public bool IsExternal => Href is not null;
}
