using System.Text;
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
            ContainsScript = packets.Any(packet => ContainsScript(packet.Data.Span))
        };
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
    /// <summary>Gets whether a packet declares script content.</summary>
    public bool ContainsScript { get; init; }
}

/// <summary>One named XFA packet and its decoded bytes.</summary>
public sealed record PdfXfaPacket(string Name, ReadOnlyMemory<byte> Data);
