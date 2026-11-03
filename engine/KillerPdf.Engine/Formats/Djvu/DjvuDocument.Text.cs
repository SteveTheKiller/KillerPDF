using System.Text;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Reads up to 4 MiB of page text and validates up to 65536 optional zones without changing embedded separators.</summary>
    public string ReadText(int pageIndex, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((uint)pageIndex >= (uint)_pageForms.Length) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var form = _pageForms[pageIndex];
        var budget = 65_535;
        var chunks = ReadChunks(_data, form.Offset + 4, form.Offset + form.Length, cancellationToken, ref budget);
        if (chunks.Count(c => c.Id is 0x54585461 or 0x5458547a) > 1)
            throw new InvalidDataException("A page contains duplicate or conflicting text chunks.");
        var text = chunks.FirstOrDefault(c => c.Id == 0x5458547a);
        if (text.Id == 0) text = chunks.FirstOrDefault(c => c.Id == 0x54585461);
        if (text.Id == 0) return string.Empty;
        ReadOnlySpan<byte> payload = text.Id == 0x5458547a
            ? DjvuBzzDecoder.Decode(_data.AsSpan(text.Offset, text.Length), 4 * 1024 * 1024 + 4 + 17 * 65_536,
                4 * 1024 * 1024, 32, cancellationToken)
            : _data.AsSpan(text.Offset, text.Length);
        if (payload.Length < 4) throw new InvalidDataException("Truncated TXTa header.");
        var length = (payload[0] << 16) | (payload[1] << 8) | payload[2];
        if (length > 4 * 1024 * 1024) throw new InvalidDataException("TXTa text exceeds the 4 MiB read limit.");
        if (length > payload.Length - 4) throw new InvalidDataException("TXTa text or version is truncated.");
        if (payload[length + 3] != 1) throw new NotSupportedException("Only TXTa text version 1 is supported.");
        ValidateTextZones(payload[(length + 4)..], length, cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = new UTF8Encoding(false, true).GetString(payload.Slice(3, length));
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("TXTa contains invalid UTF8.", exception);
        }
    }

    private static void ValidateTextZones(ReadOnlySpan<byte> zones, int textLength, CancellationToken cancellationToken)
    {
        if (zones.Length / 17 > 65_536) throw new InvalidDataException("TXTa zone count exceeds the 65536 zone limit.");
        if (zones.Length % 17 != 0) throw new InvalidDataException("Truncated TXTa zone record.");
        Span<byte> types = stackalloc byte[7];
        Span<int> childrenRemaining = stackalloc int[7];
        var depth = 0;
        for (var position = 0; position < zones.Length; position += 17)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var zone = zones.Slice(position, 17);
            var type = zone[0];
            if (type is < 1 or > 7) throw new InvalidDataException("Invalid TXTa zone type.");
            var length = (zone[11] << 16) | (zone[12] << 8) | zone[13];
            if (length > textLength) throw new InvalidDataException("TXTa zone text length exceeds the page text.");
            var childCount = (zone[14] << 16) | (zone[15] << 8) | zone[16];
            while (depth > 0 && childrenRemaining[depth - 1] == 0) depth--;
            if (depth > 0)
            {
                if (type <= types[depth - 1]) throw new InvalidDataException("TXTa child zone does not have a smaller hierarchical type.");
                childrenRemaining[depth - 1]--;
            }
            if (childCount > 0)
            {
                if (depth == 7) throw new InvalidDataException("TXTa zone hierarchy is too deep.");
                types[depth] = type;
                childrenRemaining[depth++] = childCount;
            }
        }
        for (var i = 0; i < depth; i++)
            if (childrenRemaining[i] != 0) throw new InvalidDataException("TXTa zone children are truncated.");
    }
}
