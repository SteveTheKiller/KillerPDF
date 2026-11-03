using System.Buffers.Binary;
using System.Text;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Reads document bookmarks without executing links or fetching resources.</summary>
    public IReadOnlyList<DjvuNavigationEntry> ReadNavigation(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var chunkBudget = 65_535;
        var rootEnd = 12 + (int)BinaryPrimitives.ReadUInt32BigEndian(_data.AsSpan(8));
        var chunks = ReadChunks(_data, 16, rootEnd, cancellationToken, ref chunkBudget);
        if (chunks.Count(c => c.Id == 0x4e41564d) > 1) throw new InvalidDataException("Duplicate DjVu navigation chunks.");
        var navigation = chunks.FirstOrDefault(c => c.Id == 0x4e41564d);
        if (navigation.Id == 0) return Array.Empty<DjvuNavigationEntry>();
        if (chunks.Count < 2 || chunks[0].Id != 0x4449524d || chunks[1] != navigation)
            throw new InvalidDataException("DjVu NAVM must immediately follow DIRM.");
        var decoded = DjvuBzzDecoder.Decode(_data.AsSpan(navigation.Offset, navigation.Length),
            4 * 1024 * 1024, 4 * 1024 * 1024, 32, cancellationToken);
        if (decoded.Length < 2) throw new InvalidDataException("Truncated DjVu navigation header.");
        var remaining = (int)BinaryPrimitives.ReadUInt16BigEndian(decoded);
        if (remaining > 1024) throw new InvalidDataException("DjVu navigation exceeds the 1024 bookmark limit.");
        var position = 2;
        var result = new List<DjvuNavigationEntry>();
        while (remaining > 0) result.Add(ReadEntry(1));
        if (position != decoded.Length) throw new InvalidDataException("Unexpected bytes after DjVu navigation records.");
        return result.AsReadOnly();

        DjvuNavigationEntry ReadEntry(int depth)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (depth > 32) throw new InvalidDataException("DjVu navigation exceeds the 32 level depth limit.");
            if (remaining == 0 || position >= decoded.Length)
                throw new InvalidDataException("Truncated DjVu navigation record.");
            remaining--;
            var childCount = decoded[position++];
            if (childCount > remaining) throw new InvalidDataException("DjVu navigation children exceed the declared bookmark count.");
            var label = ReadString();
            var target = ReadString();
            var children = new List<DjvuNavigationEntry>();
            for (var i = 0; i < childCount; i++) children.Add(ReadEntry(depth + 1));
            return new(label, target, children.AsReadOnly());
        }

        string ReadString()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (decoded.Length - position < 3) throw new InvalidDataException("Truncated DjVu navigation string length.");
            var length = (decoded[position] << 16) | (decoded[position + 1] << 8) | decoded[position + 2];
            position += 3;
            if (length > decoded.Length - position) throw new InvalidDataException("Truncated DjVu navigation string.");
            try
            {
                var text = new UTF8Encoding(false, true).GetString(decoded, position, length);
                position += length;
                cancellationToken.ThrowIfCancellationRequested();
                return text;
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException("DjVu navigation contains invalid UTF-8.", exception);
            }
        }
    }
}

/// <summary>A bookmark label, target and ordered child entries.</summary>
public sealed record DjvuNavigationEntry(string Label, string Target, IReadOnlyList<DjvuNavigationEntry> Children);
