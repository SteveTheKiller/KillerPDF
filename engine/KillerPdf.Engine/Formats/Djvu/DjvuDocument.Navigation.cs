using System.Buffers.Binary;
using System.Text;

namespace KillerPdf.Engine.Formats.Djvu;

public sealed partial class DjvuDocument
{
    /// <summary>Resolves a local page target to a zero-based page index without fetching resources.</summary>
    public int ResolveNavigationTarget(string target, int? currentPageIndex = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(target);
        if (target.Length > 4096)
            throw new ArgumentException("The DjVu navigation target exceeds the 4096 character limit.", nameof(target));
        if (currentPageIndex is { } current && (current < 0 || current >= Pages.Count))
            throw new ArgumentOutOfRangeException(nameof(currentPageIndex));
        if (target.Length == 0)
            throw new InvalidDataException("The DjVu navigation target is empty.");
        if (target[0] != '#')
            throw new NotSupportedException("Only local DjVu page targets are supported; external resources and viewer commands are not opened.");
        var pageTarget = DecodeLocalTarget(target[1..]);
        if (pageTarget.Any(char.IsControl))
            throw new InvalidDataException("The DjVu navigation target contains a control character.");
        for (var index = 0; index < Pages.Count; index++)
            if (string.Equals(Pages[index].ComponentId, pageTarget, StringComparison.Ordinal)) return index;
        if (pageTarget.Contains('?', StringComparison.Ordinal))
            throw new NotSupportedException("Only local DjVu page targets are supported; viewer commands are not executed.");
        if (int.TryParse(pageTarget, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out var ordinal) && ordinal >= 1 && ordinal <= Pages.Count)
            return ordinal - 1;
        if (pageTarget.Length > 1 && pageTarget[0] is '+' or '-' &&
            int.TryParse(pageTarget.AsSpan(1), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var displacement))
        {
            if (currentPageIndex is null)
                throw new ArgumentException("A relative DjVu target requires the current page index.", nameof(currentPageIndex));
            var destination = (long)currentPageIndex.Value + (pageTarget[0] == '+' ? displacement : -(long)displacement);
            if (destination >= 0 && destination < Pages.Count) return (int)destination;
        }
        throw new InvalidDataException("The DjVu navigation target does not identify an available page.");
    }

    private static string DecodeLocalTarget(string target)
    {
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            var bytes = utf8.GetBytes(target);
            var length = 0;
            for (var position = 0; position < bytes.Length; position++)
            {
                var value = bytes[position];
                if (value == '%')
                {
                    if (position + 2 >= bytes.Length ||
                        !byte.TryParse(Encoding.ASCII.GetString(bytes, position + 1, 2),
                            System.Globalization.NumberStyles.AllowHexSpecifier,
                            System.Globalization.CultureInfo.InvariantCulture, out value))
                        throw new InvalidDataException("The DjVu navigation target contains a malformed percent escape.");
                    position += 2;
                }
                bytes[length++] = value;
            }
            return utf8.GetString(bytes, 0, length);
        }
        catch (EncoderFallbackException exception)
        {
            throw new InvalidDataException("The DjVu navigation target contains invalid UTF-8.", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The DjVu navigation target contains invalid UTF-8.", exception);
        }
    }

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
