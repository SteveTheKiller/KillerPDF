using System.Text;
using System.Xml;
using System.Xml.Linq;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads XFA packets without executing form scripts.</summary>
public static class PdfXfaReader
{
    /// <summary>Reads the XFA stream or packet array from a document's AcroForm.</summary>
    public static PdfXfaInfo? Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading XFA data.");
        PdfDictionary catalog = PdfPageTree.Read(document).Catalog;
        if (!TryValue(document, catalog, "AcroForm", out PdfObject? formValue))
            return null;
        if (formValue is not PdfDictionary form)
            throw new InvalidOperationException("The catalog /AcroForm value is not a dictionary.");
        if (!TryValue(document, form, "XFA", out PdfObject? xfaValue)) return null;
        var packets = new List<PdfXfaPacket>();
        bool isPacketArray = xfaValue is PdfArray;
        if (xfaValue is PdfStream stream)
            packets.Add(Packet(document, "xdp", stream));
        else if (xfaValue is PdfArray array)
        {
            if (array.Count == 0 || array.Count % 2 != 0)
                throw new InvalidOperationException(
                    "The /XFA packet array does not contain name and stream pairs.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (int index = 0; index < array.Count; index += 2)
            {
                PdfObject nameValue = Resolve(document, array[index]);
                if (nameValue is not PdfString nameString)
                    throw new InvalidOperationException(
                        "An /XFA packet name is not a string.");
                string name = PdfUnicodeEncoding.DecodeTextString(
                    nameString.Bytes.Span, "An XFA packet name");
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                    throw new InvalidOperationException(
                        "XFA packet names must be nonempty and unique.");
                if (Resolve(document, array[index + 1]) is not PdfStream packetStream)
                    throw new InvalidOperationException(
                        "An /XFA packet value is not a stream.");
                packets.Add(Packet(document, name, packetStream));
            }
        }
        else
            throw new InvalidOperationException(
                "The /XFA value is not a stream or packet array.");
        return new PdfXfaInfo
        {
            IsPacketArray = isPacketArray,
            Packets = Array.AsReadOnly(packets.ToArray()),
            FormType = DetectFormType(packets),
            ContainsScript = packets.Any(packet => ContainsScript(packet.Data.Span))
        };
    }

    private static PdfXfaFormType DetectFormType(IReadOnlyList<PdfXfaPacket> packets)
    {
        PdfXfaPacket? config = packets.FirstOrDefault(packet =>
            string.Equals(packet.Name, "config", StringComparison.OrdinalIgnoreCase));
        if (config is null) return PdfXfaFormType.Unknown;
        using var input = new MemoryStream(config.Data.ToArray(), writable: false);
        using XmlReader reader = XmlReader.Create(input, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 16 * 1024 * 1024,
            IgnoreComments = true
        });
        XDocument document = XDocument.Load(reader, LoadOptions.None);
        XElement root = document.Root
            ?? throw new InvalidOperationException("The XFA config packet has no root element.");
        XElement? dynamicRender = root.Descendants().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "dynamicRender", StringComparison.OrdinalIgnoreCase));
        if (dynamicRender is null) return PdfXfaFormType.Static;
        return string.Equals(dynamicRender.Value.Trim(), "required", StringComparison.OrdinalIgnoreCase)
            ? PdfXfaFormType.Dynamic
            : PdfXfaFormType.Static;
    }

    private static PdfXfaPacket Packet(
        PdfDocument document, string name, PdfStream stream) =>
        new(name, PdfStreamDecoder.Decode(
            stream, document.Resolve, 64 * 1024 * 1024));

    private static bool ContainsScript(ReadOnlySpan<byte> data)
    {
        string text = Encoding.UTF8.GetString(data);
        return text.Contains("<script", StringComparison.OrdinalIgnoreCase)
            || text.Contains("application/x-javascript",
                StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryValue(
        PdfDocument document, PdfDictionary dictionary,
        string key, out PdfObject? value)
    {
        if (!dictionary.TryGetValue(
                new PdfName(Encoding.ASCII.GetBytes(key)), out value)) return false;
        value = Resolve(document, value);
        return true;
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("An XFA reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }
}

/// <summary>Safe, immutable XFA packet data.</summary>
public sealed record PdfXfaInfo
{
    /// <summary>Gets whether the source used an XFA packet array.</summary>
    public bool IsPacketArray { get; init; }
    /// <summary>Gets the XFA packets in source order.</summary>
    public IReadOnlyList<PdfXfaPacket> Packets { get; init; } = [];
    /// <summary>Gets the form layout type declared by the config packet.</summary>
    public PdfXfaFormType FormType { get; init; }
    /// <summary>Gets whether a packet declares script content.</summary>
    public bool ContainsScript { get; init; }
}

/// <summary>The layout type declared by an XFA form.</summary>
public enum PdfXfaFormType
{
    /// <summary>No config packet declares the layout type.</summary>
    Unknown,
    /// <summary>The form uses a fixed page layout.</summary>
    Static,
    /// <summary>The form can reflow and add pages as data changes.</summary>
    Dynamic
}

/// <summary>One named XFA packet and its decoded bytes.</summary>
public sealed record PdfXfaPacket(string Name, ReadOnlyMemory<byte> Data);
