using System.Text;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    private readonly record struct DirectoryComponent(string Id, int Kind);

    private static DirectoryComponent[]? ReadDirectoryMetadata(ReadOnlySpan<byte> encoded,
        List<Chunk> components, CancellationToken token)
    {
        if (encoded.IsEmpty) return null;
        var bytes = DjvuBzzDecoder.Decode(encoded, 8 * 1024 * 1024, 4 * 1024 * 1024, 32, token);
        var count = components.Count;
        if (bytes.Length < count * 4) throw new InvalidDataException("Truncated decoded DjVu directory.");
        var flagsOffset = count * 3;
        var position = count * 4;
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var result = new DirectoryComponent[count];
        for (var i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            var size = (bytes[i * 3] << 16) | (bytes[i * 3 + 1] << 8) | bytes[i * 3 + 2];
            if (size != components[i].Length + 8)
                throw new InvalidDataException("Directory component size does not match its FORM extent.");
            var flags = bytes[flagsOffset + i];
            var kind = flags & 0x3f;
            // Kinds follow DjVuLibre's DjVmDir: include 0, page 1, thumbnails 2, shared annotations 3.
            if (kind > 3) throw new InvalidDataException("Invalid directory component flags.");
            var id = ReadDirectoryString(bytes, ref position, token);
            if (id.Length == 0 || !ids.Add(id)) throw new InvalidDataException("Directory component IDs must be nonempty and unique.");
            if ((flags & 0x80) != 0) _ = ReadDirectoryString(bytes, ref position, token);
            if ((flags & 0x40) != 0) _ = ReadDirectoryString(bytes, ref position, token);
            result[i] = new(id, kind);
        }
        if (position != bytes.Length) throw new InvalidDataException("Unexpected decoded bytes after the directory.");
        return result;
    }

    private static string ReadDirectoryString(byte[] bytes, ref int position, CancellationToken token)
    {
        var start = position;
        while (position < bytes.Length && bytes[position] != 0)
        {
            token.ThrowIfCancellationRequested();
            position++;
        }
        if (position == bytes.Length) throw new InvalidDataException("Unterminated directory string.");
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes, start, position++ - start);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Directory strings must contain valid UTF-8.", exception);
        }
    }
}
