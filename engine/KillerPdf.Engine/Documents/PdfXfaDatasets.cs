using System.Xml;
using System.Xml.Linq;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads XFA datasets into portable field values without executing form code.</summary>
public static class PdfXfaDatasets
{
    private const long MaximumCharacters = 64 * 1024 * 1024;

    /// <summary>Reads leaf values from the datasets packet using qualified element paths.</summary>
    public static PdfFormDataSet Read(PdfXfaInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        PdfXfaPacket packet = info.Packets.FirstOrDefault(item =>
            string.Equals(item.Name, "datasets", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA data has no datasets packet.");
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
            ?? throw new InvalidOperationException("The XFA datasets packet has no root element.");
        if (!string.Equals(root.Name.LocalName, "datasets", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The XFA datasets packet has an unexpected root element.");
        XElement data = root.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "data", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("The XFA datasets packet has no data element.");

        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (XElement leaf in data.Descendants().Where(element => !element.Elements().Any()))
        {
            string name = string.Join('.', leaf.AncestorsAndSelf().Reverse()
                .SkipWhile(element => !ReferenceEquals(element, data)).Skip(1)
                .Select(element => element.Name.LocalName));
            if (name.Length == 0) continue;
            if (!values.TryGetValue(name, out List<string>? fieldValues))
            {
                fieldValues = [];
                values.Add(name, fieldValues);
                order.Add(name);
            }
            fieldValues.Add(leaf.Value);
        }
        return new PdfFormDataSet
        {
            Fields = Array.AsReadOnly(order.Select(name => new PdfFormDataField
            {
                Name = name,
                Values = Array.AsReadOnly(values[name].ToArray())
            }).ToArray()),
            ContainsJavaScript = info.ContainsScript
        };
    }
}
