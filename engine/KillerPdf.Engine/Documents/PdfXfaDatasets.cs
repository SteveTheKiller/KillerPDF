using System.Xml;
using System.Xml.Linq;
using System.Text;

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

    /// <summary>Writes portable field values as a deterministic XFA datasets packet.</summary>
    public static byte[] Write(PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(data);
        var rootNode = new DatasetNode();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (PdfFormDataField field in data.Fields)
        {
            if (string.IsNullOrWhiteSpace(field.Name) || !names.Add(field.Name))
                throw new ArgumentException(
                    "XFA dataset field names must be nonempty and unique.", nameof(data));
            string[] parts = field.Name.Split('.');
            if (parts.Any(part => part.Length == 0 || !IsXmlName(part)))
                throw new ArgumentException(
                    $"XFA dataset field '{field.Name}' is not a valid qualified XML name.",
                    nameof(data));
            DatasetNode node = rootNode;
            foreach (string part in parts)
            {
                if (node.Values is not null)
                    throw new ArgumentException(
                        "An XFA dataset field cannot also contain child fields.", nameof(data));
                if (!node.Children.TryGetValue(part, out DatasetNode? child))
                {
                    child = new DatasetNode();
                    node.Children.Add(part, child);
                }
                node = child;
            }
            if (node.Children.Count > 0)
                throw new ArgumentException(
                    "An XFA dataset field cannot also contain child fields.", nameof(data));
            node.Values = field.Values.ToArray();
        }

        XNamespace xfa = "http://www.xfa.org/schema/xfa-data/1.0/";
        var dataElement = new XElement(xfa + "data");
        AddChildren(dataElement, rootNode);
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(xfa + "datasets", dataElement));
        using var output = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = true,
            NewLineChars = "\n"
        })) document.Save(writer);
        return output.ToArray();
    }

    /// <summary>
    /// Replaces the datasets packet in an XFA packet array while preserving every other packet.
    /// </summary>
    public static PdfXfaInfo Replace(PdfXfaInfo info, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        if (!info.IsPacketArray)
            throw new NotSupportedException(
                "Replacing datasets inside a combined XDP stream is not supported.");
        int index = -1;
        for (int packetIndex = 0; packetIndex < info.Packets.Count; packetIndex++)
        {
            if (!string.Equals(info.Packets[packetIndex].Name, "datasets",
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (index >= 0)
                throw new InvalidOperationException(
                    "The XFA packet array contains more than one datasets packet.");
            index = packetIndex;
        }
        if (index < 0)
            throw new InvalidOperationException("The XFA data has no datasets packet.");
        PdfXfaPacket[] packets = info.Packets.ToArray();
        packets[index] = new PdfXfaPacket(packets[index].Name, Write(data));
        return new PdfXfaInfo
        {
            IsPacketArray = true,
            Packets = Array.AsReadOnly(packets),
            ContainsScript = info.ContainsScript
        };
    }

    private static void AddChildren(XElement parent, DatasetNode node)
    {
        foreach ((string name, DatasetNode child) in node.Children)
        {
            if (child.Values is not null)
                foreach (string value in child.Values) parent.Add(new XElement(name, value));
            else
            {
                var element = new XElement(name);
                AddChildren(element, child);
                parent.Add(element);
            }
        }
    }

    private static bool IsXmlName(string value)
    {
        try { XmlConvert.VerifyNCName(value); return true; }
        catch (XmlException) { return false; }
    }

    private sealed class DatasetNode
    {
        internal Dictionary<string, DatasetNode> Children { get; } =
            new(StringComparer.Ordinal);
        internal string[]? Values { get; set; }
    }
}
