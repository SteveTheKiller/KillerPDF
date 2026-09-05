using System.Globalization;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.CrossReference;

/// <summary>Reads one classic cross-reference table or one PDF 1.5+ cross-reference stream.</summary>
public static class PdfCrossReferenceReader
{
    /// <summary>Maximum number of cross-reference entries accepted in one section.</summary>
    public const int MaximumEntriesPerSection = 1_000_000;

    private static readonly PdfName TypeName = new("Type"u8);
    private static readonly PdfName XRefName = new("XRef"u8);
    private static readonly PdfName SizeName = new("Size"u8);
    private static readonly PdfName WidthsName = new("W"u8);
    private static readonly PdfName IndexName = new("Index"u8);
    private static readonly PdfName XRefStmName = new("XRefStm"u8);

    /// <summary>Reads and validates one classic or stream cross-reference section at the specified offset.</summary>
    public static PdfCrossReferenceSection ReadSection(
        ReadOnlyMemory<byte> source, long offset, bool compatibilityRecovery = false)
    {
        if (offset is < 0 or > int.MaxValue || offset >= source.Length)
            throw new PdfSyntaxException("The cross-reference offset is outside the file", ClampOffset(offset));

        var probe = new PdfTokenizer(source, (int)offset);
        PdfToken first = probe.Read();
        if (IsKeyword(first, "xref"))
            return ReadClassic(source, first, probe, compatibilityRecovery);
        if (!compatibilityRecovery)
            return ReadStream(source, (int)offset, compatibilityRecovery: false);
        if (first.Kind == PdfTokenKind.Integer)
        {
            try
            {
                return ReadStream(source, (int)offset, compatibilityRecovery: true);
            }
            catch (PdfSyntaxException)
            {
                (PdfToken Token, PdfTokenizer Tokenizer)? recovered =
                    FindNearbyClassicTable(source, (int)offset);
                if (!recovered.HasValue)
                    throw;
                return ReadClassic(source, recovered.Value.Token,
                    recovered.Value.Tokenizer, compatibilityRecovery: true);
            }
        }

        (PdfToken Token, PdfTokenizer Tokenizer)? nearby =
            FindNearbyClassicTable(source, (int)offset);
        return nearby.HasValue
            ? ReadClassic(source, nearby.Value.Token, nearby.Value.Tokenizer,
                compatibilityRecovery: true)
            : ReadStream(source, (int)offset, compatibilityRecovery: true);
    }

    private static (PdfToken Token, PdfTokenizer Tokenizer)? FindNearbyClassicTable(
        ReadOnlyMemory<byte> source, int offset)
    {
        const int maximumDistance = 1_048_576;
        for (int distance = 1; distance <= maximumDistance; distance++)
        {
            foreach (int candidate in new[] { offset + distance, offset - distance })
            {
                if (candidate < 0 || candidate + 4 > source.Length
                    || !source.Span.Slice(candidate, 4).SequenceEqual("xref"u8)
                    || candidate > 0 && !IsBoundary(source.Span[candidate - 1])
                    || candidate + 4 < source.Length && !IsBoundary(source.Span[candidate + 4]))
                    continue;
                var tokenizer = new PdfTokenizer(source, candidate);
                PdfToken token = tokenizer.Read();
                if (IsKeyword(token, "xref") && token.Offset == candidate)
                    return (token, tokenizer);
            }
        }
        return null;

        static bool IsBoundary(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
            or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    }

    private static PdfCrossReferenceSection ReadClassic(
        ReadOnlyMemory<byte> source,
        PdfToken xrefToken,
        PdfTokenizer tokenizer,
        bool compatibilityRecovery)
    {
        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        while (true)
        {
            PdfToken first = tokenizer.Read();
            if (IsKeyword(first, "trailer"))
            {
                var parser = new PdfObjectParser(source, tokenizer.Position,
                    allowDuplicateDictionaryKeys: compatibilityRecovery);
                if (parser.ParseObject() is not PdfDictionary trailer)
                    throw Error("A classic xref trailer must be a dictionary", tokenizer.Position);
                ValidateSize(trailer, entries.Values, first.Offset,
                    compatibilityRecovery);
                ValidateTrailerOffsets(trailer, source.Length, first.Offset,
                    compatibilityRecovery);
                return new PdfCrossReferenceSection(xrefToken.Offset, entries.Values, trailer,
                    isStream: false, compatibilityRecovery: compatibilityRecovery);
            }

            long firstObject = ParseInteger(first, "An xref subsection must begin with an object number");
            PdfToken countToken = tokenizer.Read();
            long count = ParseInteger(countToken, "An xref subsection must include an entry count");
            if (firstObject < 0 || firstObject > int.MaxValue
                || count < 0 || count > int.MaxValue - firstObject)
                throw Error("The xref subsection range is outside the supported object-number range", first.Offset);
            if (count > MaximumEntriesPerSection - entries.Count)
                throw Error(
                    $"A cross-reference section cannot contain more than {MaximumEntriesPerSection:N0} entries",
                    first.Offset);

            for (long index = 0; index < count; index++)
            {
                int objectNumber = (int)(firstObject + index);
                PdfToken field1Token = tokenizer.Read();
                PdfToken field2Token = tokenizer.Read();
                PdfToken statusToken = tokenizer.Read();
                long field1 = ParseInteger(field1Token, "An xref entry must begin with a numeric field");
                long field2 = ParseInteger(field2Token, "An xref entry must include a generation field");
                PdfCrossReferenceEntry entry = ParseClassicEntry(
                    source.Length, objectNumber, field1, field2, statusToken,
                    compatibilityRecovery);
                if (!entries.TryAdd(objectNumber, entry))
                    throw Error($"The xref table defines object {objectNumber} more than once", first.Offset);
            }
        }
    }

    private static PdfCrossReferenceEntry ParseClassicEntry(
        int sourceLength,
        int objectNumber,
        long field1,
        long field2,
        PdfToken statusToken,
        bool compatibilityRecovery)
    {
        if (compatibilityRecovery && objectNumber != 0 && field1 == 0 && field2 > 65_535
            && IsKeyword(statusToken, "n"))
            return new PdfCrossReferenceEntry(
                objectNumber, PdfCrossReferenceEntryType.Null, 0, 0);

        if (objectNumber == 0 && compatibilityRecovery && IsKeyword(statusToken, "f"))
            field2 = 65_535;
        else if (field2 is < 0 or > 65_535)
            throw Error("An xref generation must be between 0 and 65,535", statusToken.Offset);
        if (objectNumber == 0 && !IsKeyword(statusToken, "f"))
            throw Error("Cross-reference object 0 must be free with generation 65,535",
                statusToken.Offset);
        if (objectNumber == 0)
            field2 = 65_535;

        if (IsKeyword(statusToken, "n"))
        {
            if (field1 < 0 || field1 >= sourceLength || field2 == 65_535)
                throw Error("An in-use xref entry contains an invalid offset or retired generation", statusToken.Offset);
            return new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.InUse, field1, (int)field2);
        }

        if (IsKeyword(statusToken, "f"))
        {
            if (field1 is < 0 or > int.MaxValue)
                throw Error("A free xref entry has an invalid next-free object number", statusToken.Offset);
            return new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.Free, field1, (int)field2);
        }

        throw Error("An xref entry must end with n or f", statusToken.Offset);
    }

    private static PdfCrossReferenceSection ReadStream(
        ReadOnlyMemory<byte> source, int offset, bool compatibilityRecovery)
    {
        PdfIndirectObject indirect = new PdfObjectParser(source, offset,
            allowDuplicateDictionaryKeys: compatibilityRecovery).ParseIndirectObject();
        if (indirect.Generation != 0)
            throw Error("A cross-reference stream object must have generation 0", offset);
        if (indirect.Value is not PdfStream stream)
            throw Error("The startxref target is neither an xref table nor an xref stream", offset);
        if (!stream.Dictionary.TryGetValue(TypeName, out PdfObject type)
            || type is not PdfName name
            || !name.Equals(XRefName))
            throw Error("A cross-reference stream must have /Type /XRef", offset);
        if (stream.Dictionary.ContainsKey(XRefStmName))
            throw Error("A cross-reference stream cannot contain /XRefStm", offset);

        int size = RequiredNonNegativeInt(stream.Dictionary, SizeName, offset);
        int[] widths = ReadWidths(stream.Dictionary, offset);
        IReadOnlyList<(int First, int Count)> ranges = ReadIndex(stream.Dictionary, size, offset);
        ValidateTrailerOffsets(stream.Dictionary, source.Length, offset,
            compatibilityRecovery);

        int rowWidth = checked(widths[0] + widths[1] + widths[2]);
        long rowCount = ranges.Sum(range => (long)range.Count);
        if (rowCount > MaximumEntriesPerSection)
            throw Error(
                $"A cross-reference section cannot contain more than {MaximumEntriesPerSection:N0} entries",
                offset);
        long expectedLength = checked(rowCount * rowWidth);
        int decodeLimit = checked((int)expectedLength + 1);
        byte[] decoded = PdfStreamDecoder.Decode(stream, decodeLimit);
        if (decoded.LongLength != expectedLength)
            throw Error("The decoded xref stream length does not match its /W and /Index entries", offset);

        var entries = new Dictionary<int, PdfCrossReferenceEntry>();
        int position = 0;
        foreach ((int first, int count) in ranges)
        {
            for (int index = 0; index < count; index++)
            {
                int objectNumber = first + index;
                ulong typeField = widths[0] == 0 ? 1UL : ReadBigEndian(decoded, ref position, widths[0]);
                ulong field1 = ReadBigEndian(decoded, ref position, widths[1]);
                ulong field2 = ReadBigEndian(decoded, ref position, widths[2]);
                PdfCrossReferenceEntry entry = ParseStreamEntry(
                    source.Length, size, objectNumber, typeField, field1, field2, offset);
                if (!entries.TryAdd(objectNumber, entry))
                    throw Error($"The xref stream defines object {objectNumber} more than once", offset);
            }
        }

        ValidateSize(stream.Dictionary, entries.Values, offset,
            compatibilityRecovery);
        return new PdfCrossReferenceSection(offset, entries.Values, stream.Dictionary,
            isStream: true, streamObjectNumber: indirect.ObjectNumber,
            compatibilityRecovery: compatibilityRecovery);
    }

    private static PdfCrossReferenceEntry ParseStreamEntry(
        int sourceLength,
        int size,
        int objectNumber,
        ulong type,
        ulong field1,
        ulong field2,
        int offset)
    {
        if (objectNumber == 0 && type != 0)
            throw Error("Cross-reference object 0 must be free with generation 65,535",
                offset);
        if (objectNumber == 0)
            field2 = 65_535;
        return type switch
        {
            0 when field1 <= int.MaxValue && field2 <= 65_535 =>
                new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.Free, (long)field1, (int)field2),
            1 when field1 < (ulong)sourceLength && field2 < 65_535 =>
                new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.InUse, (long)field1, (int)field2),
            2 when field1 > 0 && field1 < (ulong)size
                && field1 != (ulong)objectNumber && field2 <= int.MaxValue =>
                new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.Compressed, (long)field1, (int)field2),
            0 => throw Error("A free xref-stream entry contains an invalid field", offset),
            1 => throw Error("An in-use xref-stream entry contains an invalid offset or generation", offset),
            2 => throw Error("A compressed xref-stream entry contains an invalid object-stream field", offset),
            // PDF reserves additional types for future versions and requires older readers to treat
            // an entry with an unknown type as a reference to the null object.
            _ => new PdfCrossReferenceEntry(objectNumber, PdfCrossReferenceEntryType.Null, 0, 0)
        };
    }

    private static int[] ReadWidths(PdfDictionary dictionary, int offset)
    {
        if (!dictionary.TryGetValue(WidthsName, out PdfObject value)
            || value is not PdfArray array
            || array.Count != 3)
            throw Error("A cross-reference stream must contain a three-integer /W array", offset);

        var widths = new int[3];
        for (int index = 0; index < 3; index++)
        {
            if (array[index] is not PdfInteger integer || integer.Value is < 0 or > 8)
                throw Error("Each cross-reference /W width must be between 0 and 8", offset);
            widths[index] = (int)integer.Value;
        }
        if (widths.Sum() == 0)
            throw Error("A cross-reference /W array cannot contain three zero widths", offset);
        return widths;
    }

    private static List<(int First, int Count)> ReadIndex(
        PdfDictionary dictionary,
        int size,
        int offset)
    {
        if (!dictionary.TryGetValue(IndexName, out PdfObject value))
            return [(0, size)];
        if (value is not PdfArray array || array.Count == 0 || array.Count % 2 != 0)
            throw Error("A cross-reference /Index array must contain object/count pairs", offset);

        var ranges = new List<(int First, int Count)>(array.Count / 2);
        long previousEnd = -1;
        for (int index = 0; index < array.Count; index += 2)
        {
            int first = ArrayNonNegativeInt(array[index], "/Index object number", offset);
            int count = ArrayNonNegativeInt(array[index + 1], "/Index count", offset);
            if ((long)first + count > size)
                throw Error("A cross-reference /Index range exceeds /Size", offset);
            if (first < previousEnd)
                throw Error(
                    "Cross-reference /Index ranges must be ordered and nonoverlapping",
                    offset);
            ranges.Add((first, count));
            previousEnd = (long)first + count;
        }
        return ranges;
    }

    private static ulong ReadBigEndian(byte[] source, ref int position, int width)
    {
        ulong value = 0;
        for (int index = 0; index < width; index++)
            value = (value << 8) | source[position++];
        return value;
    }

    private static int RequiredNonNegativeInt(PdfDictionary dictionary, PdfName name, int offset)
    {
        if (!dictionary.TryGetValue(name, out PdfObject value))
            throw Error($"The cross-reference dictionary is missing {name}", offset);
        return ArrayNonNegativeInt(value, name.ToString(), offset);
    }

    private static int ArrayNonNegativeInt(PdfObject value, string description, int offset)
    {
        if (value is not PdfInteger integer || integer.Value is < 0 or > int.MaxValue)
            throw Error($"{description} must be a non-negative 32-bit integer", offset);
        return (int)integer.Value;
    }

    private static void ValidateSize(
        PdfDictionary trailer,
        IEnumerable<PdfCrossReferenceEntry> entries,
        int offset,
        bool compatibilityRecovery)
    {
        int size = RequiredNonNegativeInt(trailer, SizeName, offset);
        if (size == 0 || !compatibilityRecovery && entries.Any(entry =>
                entry.Type is PdfCrossReferenceEntryType.InUse or PdfCrossReferenceEntryType.Compressed
                && entry.ObjectNumber >= size))
            throw Error("Trailer /Size must be greater than every in-use object number", offset);
        if (entries.Any(entry => entry.Type == PdfCrossReferenceEntryType.Free
                && entry.ObjectNumber > size))
            throw Error("A free cross-reference entry lies beyond trailer /Size", offset);
    }

    private static void ValidateTrailerOffsets(
        PdfDictionary trailer,
        int sourceLength,
        int offset,
        bool compatibilityRecovery)
    {
        ValidateOptionalOffset(trailer, new PdfName("Prev"u8), sourceLength, offset,
            allowPastEnd: compatibilityRecovery);
        ValidateOptionalOffset(trailer, new PdfName("XRefStm"u8), sourceLength, offset);
    }

    private static void ValidateOptionalOffset(
        PdfDictionary trailer,
        PdfName name,
        int sourceLength,
        int offset,
        bool allowPastEnd = false)
    {
        if (!trailer.TryGetValue(name, out PdfObject value))
            return;
        if (allowPastEnd && value is PdfIndirectReference { Generation: 0 } reference)
        {
            if (reference.ObjectNumber < sourceLength) return;
            throw Error($"Trailer {name} must point inside the file", offset);
        }
        if (value is not PdfInteger integer || integer.Value < 0
            || !allowPastEnd && integer.Value >= sourceLength)
            throw Error($"Trailer {name} must point inside the file", offset);
    }

    private static long ParseInteger(PdfToken token, string message)
    {
        if (token.Kind != PdfTokenKind.Integer
            || !long.TryParse(token.Value.Span, NumberStyles.AllowLeadingSign,
                              CultureInfo.InvariantCulture, out long value))
            throw Error(message, token.Offset);
        return value;
    }

    private static bool IsKeyword(PdfToken token, string keyword) =>
        token.Kind == PdfTokenKind.Keyword
        && token.Value.Span.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(keyword));

    private static int ClampOffset(long offset) => offset switch
    {
        < 0 => 0,
        > int.MaxValue => int.MaxValue,
        _ => (int)offset
    };

    private static PdfSyntaxException Error(string message, int offset) => new(message, offset);
}
