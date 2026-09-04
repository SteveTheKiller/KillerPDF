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
        (_, _, XElement root) = DatasetDocument(info, preserveWhitespace: false);
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
    /// Replaces the datasets packet while preserving every other packet or XDP element.
    /// </summary>
    public static PdfXfaInfo Replace(PdfXfaInfo info, PdfFormDataSet data)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(data);
        if (!info.IsPacketArray)
        {
            (int packetIndex, XDocument document, XElement datasets) =
                DatasetDocument(info, preserveWhitespace: true);
            using var replacementInput = new MemoryStream(Write(data), writable: false);
            XDocument replacement = XDocument.Load(replacementInput, LoadOptions.PreserveWhitespace);
            datasets.ReplaceWith(new XElement(replacement.Root!));
            return ReplacePacket(info, packetIndex, Serialize(document));
        }
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
        return info with { Packets = Array.AsReadOnly(packets) };
    }

    /// <summary>
    /// Replaces one existing dataset value while preserving unrelated XML and XFA packets.
    /// </summary>
    public static PdfXfaInfo SetValue(
        PdfXfaInfo info, string fieldName, int occurrenceIndex, string value)
    {
        ArgumentNullException.ThrowIfNull(info);
        if (string.IsNullOrWhiteSpace(fieldName))
            throw new ArgumentException("An XFA dataset field name is required.", nameof(fieldName));
        if (occurrenceIndex < 0)
            throw new ArgumentOutOfRangeException(nameof(occurrenceIndex));
        ArgumentNullException.ThrowIfNull(value);
        (int packetIndex, XDocument document, XElement root) =
            DatasetDocument(info, preserveWhitespace: true);
        XElement data = root.Elements().First(element =>
            string.Equals(element.Name.LocalName, "data", StringComparison.OrdinalIgnoreCase));
        string[] parts = fieldName.Split('.');
        if (parts.Any(part => part.Length == 0))
            throw new ArgumentException("The XFA dataset field name is invalid.", nameof(fieldName));
        IEnumerable<XElement> candidates = [data];
        foreach (string part in parts)
            candidates = candidates.SelectMany(parent => parent.Elements().Where(element =>
                string.Equals(element.Name.LocalName, part, StringComparison.Ordinal)));
        XElement[] matches = [.. candidates.Where(element => !element.Elements().Any())];
        if (occurrenceIndex >= matches.Length)
            throw new KeyNotFoundException(
                $"XFA dataset field '{fieldName}' occurrence {occurrenceIndex} was not found.");
        matches[occurrenceIndex].Value = value;

        return ReplacePacket(info, packetIndex, Serialize(document));
    }

    private static (int PacketIndex, XDocument Document, XElement Datasets)
        DatasetDocument(PdfXfaInfo info, bool preserveWhitespace)
    {
        int packetIndex = info.IsPacketArray ? DatasetPacketIndex(info) :
            info.Packets.Count == 1 ? 0 : throw new InvalidOperationException(
                "Combined XDP data requires exactly one stream packet.");
        XDocument document = LoadXml(info.Packets[packetIndex], preserveWhitespace);
        XElement[] datasets = document.Root!.DescendantsAndSelf().Where(element =>
            string.Equals(element.Name.LocalName, "datasets",
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (datasets.Length != 1)
            throw new InvalidOperationException(
                "The XFA data must contain exactly one datasets element.");
        if (!datasets[0].Elements().Any(element => string.Equals(
                element.Name.LocalName, "data", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The XFA datasets packet has no data element.");
        return (packetIndex, document, datasets[0]);
    }

    private static PdfXfaInfo ReplacePacket(
        PdfXfaInfo info, int packetIndex, byte[] data)
    {
        PdfXfaPacket[] packets = info.Packets.ToArray();
        packets[packetIndex] = new PdfXfaPacket(packets[packetIndex].Name, data);
        return info with { Packets = Array.AsReadOnly(packets) };
    }

    private static byte[] Serialize(XDocument document)
    {
        using var output = new MemoryStream();
        using (XmlWriter writer = XmlWriter.Create(output, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            NewLineChars = "\n",
            OmitXmlDeclaration = document.Declaration is null
        })) document.Save(writer);
        return output.ToArray();
    }

    private static int DatasetPacketIndex(PdfXfaInfo info)
    {
        int result = -1;
        for (int index = 0; index < info.Packets.Count; index++)
        {
            if (!string.Equals(info.Packets[index].Name, "datasets",
                    StringComparison.OrdinalIgnoreCase)) continue;
            if (result >= 0)
                throw new InvalidOperationException(
                    "The XFA packet array contains more than one datasets packet.");
            result = index;
        }
        return result >= 0 ? result : throw new InvalidOperationException(
            "The XFA data has no datasets packet.");
    }

    private static XDocument LoadXml(PdfXfaPacket packet, bool preserveWhitespace)
    {
        using var input = new MemoryStream(packet.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = MaximumCharacters,
            IgnoreComments = false
        });
        XDocument document = XDocument.Load(reader, preserveWhitespace
            ? LoadOptions.PreserveWhitespace : LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidOperationException("The XFA packet has no root element.");
        return document;
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
