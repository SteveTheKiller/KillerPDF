using System.IO.Compression;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Filters;

/// <summary>Decodes a stream's filter pipeline with a hard output-size limit.</summary>
public static class PdfStreamDecoder
{
    /// <summary>Default maximum number of decoded bytes produced by one stream.</summary>
    public const int DefaultMaximumDecodedBytes = 256 * 1024 * 1024;

    private static readonly PdfName FilterName = new("Filter"u8);
    private static readonly PdfName DecodeParmsName = new("DecodeParms"u8);
    private static readonly PdfName PredictorName = new("Predictor"u8);
    private static readonly PdfName ColorsName = new("Colors"u8);
    private static readonly PdfName BitsPerComponentName = new("BitsPerComponent"u8);
    private static readonly PdfName ColumnsName = new("Columns"u8);
    private static readonly PdfName ColorTransformName = new("ColorTransform"u8);
    private static readonly PdfName HeightName = new("Height"u8);
    private static readonly PdfName WidthName = new("Width"u8);
    private static readonly PdfName Jbig2GlobalsName = new("JBIG2Globals"u8);

    /// <summary>Decodes a stream whose filter metadata contains no indirect references.</summary>
    public static byte[] Decode(PdfStream stream, int maximumDecodedBytes = DefaultMaximumDecodedBytes)
        => DecodeCore(stream, null, maximumDecodedBytes, 0, false);

    /// <summary>Decodes a stream while resolving indirect filter and predictor metadata.</summary>
    public static byte[] Decode(
        PdfStream stream,
        Func<PdfIndirectReference, PdfObject> resolve,
        int maximumDecodedBytes = DefaultMaximumDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return DecodeCore(stream, resolve, maximumDecodedBytes, 0, false);
    }

    /// <summary>Decodes a malformed stream using bounded viewer-compatible recoveries.</summary>
    public static byte[] DecodeWithCompatibilityRecovery(
        PdfStream stream,
        int maximumDecodedBytes = DefaultMaximumDecodedBytes)
        => DecodeCore(stream, null, maximumDecodedBytes, 0, true);

    /// <summary>Decodes a malformed stream while resolving indirect filter metadata.</summary>
    public static byte[] DecodeWithCompatibilityRecovery(
        PdfStream stream,
        Func<PdfIndirectReference, PdfObject> resolve,
        int maximumDecodedBytes = DefaultMaximumDecodedBytes)
    {
        ArgumentNullException.ThrowIfNull(resolve);
        return DecodeCore(stream, resolve, maximumDecodedBytes, 0, true);
    }

    internal static JpegDecodedImage DecodeJpegImage(
        PdfStream stream, Func<PdfIndirectReference, PdfObject> resolve,
        int maximumDecodedBytes, int reduction)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(resolve);
        List<PdfName> filters = ReadFilters(stream.Dictionary, resolve);
        if (filters.Count != 1
            || filters[0].ValueAsLatin1() is not ("DCTDecode" or "DCT"))
            throw new PdfFilterException("Reduced JPEG decoding requires one DCTDecode filter.");
        PdfDictionary? parameters = ReadParameters(
            stream.Dictionary, filters.Count, resolve)[0];
        return PdfJpegDecoder.DecodeImage(stream.EncodedData.Span,
            maximumDecodedBytes, reduction, GetDctColorTransform(parameters, resolve));
    }

    private static byte[] DecodeCore(
        PdfStream stream,
        Func<PdfIndirectReference, PdfObject>? resolve,
        int maximumDecodedBytes,
        int nestedDepth,
        bool compatibilityRecovery)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDecodedBytes);
        if (nestedDepth > 8)
            throw new PdfFilterException("Nested stream decoding is too deep.");

        List<PdfName> filters = ReadFilters(stream.Dictionary, resolve);
        if (filters.Count == 0)
        {
            EnsureWithinLimit(stream.EncodedData.Length, maximumDecodedBytes);
            return stream.EncodedData.ToArray();
        }
        PdfDictionary?[] parameters = ReadParameters(
            stream.Dictionary, filters.Count, resolve);

        byte[] current = stream.EncodedData.ToArray();

        for (int i = 0; i < filters.Count; i++)
        {
            string filter = filters[i].ValueAsLatin1();
            int outputLimit = i + 1 < filters.Count
                ? Math.Max(maximumDecodedBytes, current.Length)
                : maximumDecodedBytes;
            int filterLimit = filter is "FlateDecode" or "Fl" or "LZWDecode" or "LZW"
                ? PredictorEncodedLimit(parameters[i], resolve, outputLimit)
                : outputLimit;
            current = filter switch
            {
                "FlateDecode" or "Fl" => DecodeFlate(
                    current, filterLimit, compatibilityRecovery),
                "ASCIIHexDecode" or "AHx" => DecodeAsciiHex(current, filterLimit),
                "ASCII85Decode" or "A85" => DecodeAscii85(current, filterLimit),
                "RunLengthDecode" or "RL" => DecodeRunLength(current, filterLimit),
                "LZWDecode" or "LZW" => DecodeLzw(
                    current, parameters[i], resolve, filterLimit),
                "DCTDecode" or "DCT" => PdfJpegDecoder.Decode(current, filterLimit,
                    GetDctColorTransform(parameters[i], resolve)),
                "JPXDecode" or "JPX" => PdfJpeg2000Decoder.Decode(current, filterLimit),
                "JBIG2Decode" => PdfJbig2Decoder.Decode(current,
                    GetJbig2Globals(parameters[i], resolve),
                    globalStream => DecodeCore(globalStream, resolve,
                        DefaultMaximumDecodedBytes, nestedDepth + 1,
                        compatibilityRecovery),
                    filterLimit,
                    GetImageDimension(stream.Dictionary, WidthName, resolve),
                    GetImageDimension(stream.Dictionary, HeightName, resolve)),
                "CCITTFaxDecode" or "CCF" => PdfCcittFaxDecoder.Decode(current,
                    GetCcittOptions(stream.Dictionary, parameters[i], resolve), filterLimit),
                "Crypt" => current,
                _ => throw new PdfFilterException($"The PDF stream filter /{filter} is not supported yet.")
            };

            // /Crypt is intentionally a no-op here because decryption belongs to the
            // security handler. It must still obey the same expansion boundary as
            // every decoding filter, including when it is the only filter.
            EnsureWithinLimit(current.Length, filterLimit);
            if (filter is "FlateDecode" or "Fl" or "LZWDecode" or "LZW")
                current = ReversePredictor(
                    current, parameters[i], resolve, outputLimit,
                    compatibilityRecovery);
        }

        return current;
    }

    private static int PredictorEncodedLimit(
        PdfDictionary? parameters,
        Func<PdfIndirectReference, PdfObject>? resolve,
        int maximumDecodedBytes)
    {
        if (parameters is null)
            return maximumDecodedBytes;
        int predictor = GetInteger(parameters, PredictorName, 1, resolve);
        if (predictor is not (>= 10 and <= 15))
            return maximumDecodedBytes;

        int colors = GetPositiveInteger(parameters, ColorsName, 1, resolve);
        int bitsPerComponent = GetPositiveInteger(
            parameters, BitsPerComponentName, 8, resolve);
        int columns = GetPositiveInteger(parameters, ColumnsName, 1, resolve);
        if (bitsPerComponent is not (1 or 2 or 4 or 8 or 16))
            throw new PdfFilterException("Predictor BitsPerComponent must be 1, 2, 4, 8, or 16.");
        try
        {
            int rowLength = checked(columns * colors * bitsPerComponent + 7) / 8;
            long rows = maximumDecodedBytes / rowLength;
            return checked((int)(rows * checked(rowLength + 1L)));
        }
        catch (OverflowException ex)
        {
            throw new PdfFilterException("Predictor dimensions exceed the supported range.", ex);
        }
    }

    private static byte[] DecodeAsciiHex(ReadOnlySpan<byte> encoded, int maximumDecodedBytes)
    {
        var output = new List<byte>();
        int high = -1;
        bool ended = false;
        foreach (byte value in encoded)
        {
            if (IsWhiteSpace(value)) continue;
            if (value == '>') { ended = true; break; }
            int digit = value switch
            {
                >= (byte)'0' and <= (byte)'9' => value - '0',
                >= (byte)'A' and <= (byte)'F' => value - 'A' + 10,
                >= (byte)'a' and <= (byte)'f' => value - 'a' + 10,
                _ => throw new PdfFilterException("ASCIIHex data contains a non-hexadecimal byte.")
            };
            if (high < 0) high = digit;
            else { AddBounded(output, (byte)((high << 4) | digit), maximumDecodedBytes); high = -1; }
        }
        if (!ended) throw new PdfFilterException("ASCIIHex data has no end marker.");
        if (high >= 0) AddBounded(output, (byte)(high << 4), maximumDecodedBytes);
        return [.. output];
    }

    private static byte[] DecodeAscii85(ReadOnlySpan<byte> encoded, int maximumDecodedBytes)
    {
        var output = new List<byte>();
        Span<byte> tuple = stackalloc byte[5];
        int count = 0;
        bool ended = false;
        for (int index = 0; index < encoded.Length; index++)
        {
            byte value = encoded[index];
            if (IsWhiteSpace(value)) continue;
            if (value == '~')
            {
                int next = index + 1;
                while (next < encoded.Length && IsWhiteSpace(encoded[next])) next++;
                if (next >= encoded.Length || encoded[next] != '>')
                    throw new PdfFilterException("ASCII85 data has an invalid end marker.");
                ended = true;
                break;
            }
            if (value == 'z')
            {
                if (count != 0) throw new PdfFilterException("ASCII85 'z' appears inside a tuple.");
                for (int item = 0; item < 4; item++) AddBounded(output, 0, maximumDecodedBytes);
                continue;
            }
            if (value is < (byte)'!' or > (byte)'u')
                throw new PdfFilterException("ASCII85 data contains an invalid byte.");
            tuple[count++] = value;
            if (count == 5) { WriteAscii85Tuple(tuple, 4, output, maximumDecodedBytes); count = 0; }
        }
        if (!ended) throw new PdfFilterException("ASCII85 data has no end marker.");
        if (count == 1) throw new PdfFilterException("ASCII85 data ends with an incomplete tuple.");
        if (count > 1)
        {
            tuple[count..].Fill((byte)'u');
            WriteAscii85Tuple(tuple, count - 1, output, maximumDecodedBytes);
        }
        return [.. output];
    }

    private static void WriteAscii85Tuple(
        ReadOnlySpan<byte> tuple, int bytesToWrite, ICollection<byte> output, int maximumDecodedBytes)
    {
        ulong value = 0;
        for (int index = 0; index < 5; index++) value = value * 85 + (uint)(tuple[index] - '!');
        if (value > uint.MaxValue) throw new PdfFilterException("ASCII85 tuple exceeds 32 bits.");
        for (int shift = 24; bytesToWrite > 0; shift -= 8, bytesToWrite--)
            AddBounded(output, (byte)(value >> shift), maximumDecodedBytes);
    }

    private static byte[] DecodeRunLength(ReadOnlySpan<byte> encoded, int maximumDecodedBytes)
    {
        var output = new List<byte>();
        int offset = 0;
        while (offset < encoded.Length)
        {
            int length = encoded[offset++];
            if (length == 128) return [.. output];
            if (length <= 127)
            {
                int count = length + 1;
                if (offset + count > encoded.Length)
                    throw new PdfFilterException("RunLength literal run exceeds the encoded data.");
                for (int index = 0; index < count; index++)
                    AddBounded(output, encoded[offset++], maximumDecodedBytes);
            }
            else
            {
                if (offset >= encoded.Length)
                    throw new PdfFilterException("RunLength repeat run has no source byte.");
                byte value = encoded[offset++];
                for (int index = 0; index < 257 - length; index++)
                    AddBounded(output, value, maximumDecodedBytes);
            }
        }
        throw new PdfFilterException("RunLength data has no end marker.");
    }

    private static byte[] DecodeLzw(
        ReadOnlySpan<byte> encoded, PdfDictionary? parameters,
        Func<PdfIndirectReference, PdfObject>? resolve, int maximumDecodedBytes)
    {
        int earlyChange = GetOptionalInteger(
            parameters, new PdfName("EarlyChange"u8), 1, resolve);
        if (earlyChange is not (0 or 1))
            throw new PdfFilterException("LZW EarlyChange must be 0 or 1.");
        var dictionary = new byte[4096][];
        var output = new List<byte>();
        int bitOffset = 0;
        int width = 9;
        int nextCode = 258;
        byte[]? previous = null;
        for (int value = 0; value < 256; value++) dictionary[value] = [(byte)value];
        while (TryReadCode(encoded, ref bitOffset, width, out int code))
        {
            if (code == 256) { Reset(); previous = null; continue; }
            if (code == 257) return [.. output];
            byte[] current;
            if (code < nextCode && dictionary[code] is not null)
                current = dictionary[code];
            else if (code == nextCode && previous is not null)
                current = [.. previous, previous[0]];
            else
                throw new PdfFilterException("LZW data contains an invalid dictionary code.");
            foreach (byte value in current) AddBounded(output, value, maximumDecodedBytes);
            if (previous is not null && nextCode < 4096)
            {
                dictionary[nextCode++] = [.. previous, current[0]];
                if (width < 12 && nextCode == (1 << width) - earlyChange) width++;
            }
            previous = current;
        }
        throw new PdfFilterException("LZW data has no end-of-data code.");

        void Reset()
        {
            Array.Clear(dictionary);
            for (int value = 0; value < 256; value++) dictionary[value] = [(byte)value];
            width = 9;
            nextCode = 258;
        }
    }

    private static bool TryReadCode(
        ReadOnlySpan<byte> encoded, ref int bitOffset, int width, out int code)
    {
        if (bitOffset + width > encoded.Length * 8) { code = 0; return false; }
        code = 0;
        for (int bit = 0; bit < width; bit++)
        {
            int absolute = bitOffset + bit;
            code = (code << 1) | ((encoded[absolute / 8] >> (7 - absolute % 8)) & 1);
        }
        bitOffset += width;
        return true;
    }

    private static int GetOptionalInteger(
        PdfDictionary? dictionary, PdfName name, int defaultValue,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (dictionary is null || !dictionary.TryGetValue(name, out PdfObject value))
            return defaultValue;
        value = Resolve(value, resolve, $"Decode parameter /{name.ValueAsLatin1()}");
        return value is PdfInteger integer && integer.Value is >= int.MinValue and <= int.MaxValue
            ? (int)integer.Value
            : throw new PdfFilterException($"Decode parameter /{name.ValueAsLatin1()} must be an integer.");
    }

    private static int? GetDctColorTransform(
        PdfDictionary? dictionary, Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (dictionary is null || !dictionary.ContainsKey(ColorTransformName)) return null;
        int value = GetOptionalInteger(dictionary, ColorTransformName, -1, resolve);
        return value is 0 or 1 ? value
            : throw new PdfFilterException("DCTDecode ColorTransform must be 0 or 1.");
    }

    private static PdfStream? GetJbig2Globals(
        PdfDictionary? dictionary, Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (dictionary is null
            || !dictionary.TryGetValue(Jbig2GlobalsName, out PdfObject value))
            return null;
        value = Resolve(value, resolve, "Decode parameter /JBIG2Globals");
        return value as PdfStream ?? throw new PdfFilterException(
            "Decode parameter /JBIG2Globals must reference a stream.");
    }

    private static int? GetImageDimension(
        PdfDictionary dictionary, PdfName name,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (!dictionary.TryGetValue(name, out PdfObject value))
            return null;
        value = Resolve(value, resolve, $"image /{name.ValueAsLatin1()}");
        return value is PdfInteger integer && integer.Value is > 0 and <= int.MaxValue
            ? (int)integer.Value
            : throw new PdfFilterException(
                $"Image {name.ValueAsLatin1()} must be a positive integer.");
    }

    private static PdfCcittFaxOptions GetCcittOptions(
        PdfDictionary stream, PdfDictionary? parameters,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        int columns = parameters is not null && parameters.ContainsKey(ColumnsName)
            ? GetPositiveInteger(parameters, ColumnsName, 1728, resolve)
            : GetPositiveInteger(stream, WidthName, 1728, resolve);
        int rows = parameters is not null && parameters.ContainsKey(new PdfName("Rows"u8))
            ? GetPositiveInteger(parameters, new PdfName("Rows"u8), 1, resolve)
            : GetPositiveInteger(stream, HeightName, 1, resolve);
        return new PdfCcittFaxOptions(
            GetOptionalInteger(parameters, new PdfName("K"u8), 0, resolve),
            columns, rows,
            GetOptionalBoolean(parameters, new PdfName("EndOfLine"u8), false, resolve),
            GetOptionalBoolean(parameters, new PdfName("EncodedByteAlign"u8), false, resolve),
            GetOptionalBoolean(parameters, new PdfName("BlackIs1"u8), false, resolve));
    }

    private static bool GetOptionalBoolean(
        PdfDictionary? dictionary, PdfName name, bool defaultValue,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (dictionary is null || !dictionary.TryGetValue(name, out PdfObject value))
            return defaultValue;
        value = Resolve(value, resolve, $"Decode parameter /{name.ValueAsLatin1()}");
        return value is PdfBoolean boolean ? boolean.Value
            : throw new PdfFilterException(
                $"Decode parameter /{name.ValueAsLatin1()} must be a boolean.");
    }

    private static void AddBounded(ICollection<byte> output, byte value, int maximumDecodedBytes)
    {
        if (output.Count >= maximumDecodedBytes)
            throw new PdfFilterException("Decoded stream exceeds the configured safety limit.");
        output.Add(value);
    }

    private static bool IsWhiteSpace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;

    private static List<PdfName> ReadFilters(
        PdfDictionary dictionary, Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (!dictionary.TryGetValue(FilterName, out PdfObject filterObject))
            return [];
        filterObject = Resolve(filterObject, resolve, "stream /Filter");
        if (filterObject is PdfName name)
            return [name];
        if (filterObject is not PdfArray array)
            throw new PdfFilterException("A stream Filter entry must be a name or an array of names.");

        var filters = new List<PdfName>(array.Count);
        foreach (PdfObject rawItem in array)
        {
            PdfObject item = Resolve(rawItem, resolve, "stream /Filter array entry");
            if (item is not PdfName filter)
                throw new PdfFilterException("Every entry in a stream Filter array must be a name.");
            filters.Add(filter);
        }
        return filters;
    }

    private static PdfDictionary?[] ReadParameters(
        PdfDictionary dictionary, int filterCount,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        var result = new PdfDictionary?[filterCount];
        if (!dictionary.TryGetValue(DecodeParmsName, out PdfObject parameters))
            return result;
        parameters = Resolve(parameters, resolve, "stream /DecodeParms");
        if (parameters is PdfNull) return result;

        if (parameters is PdfDictionary single)
        {
            if (filterCount != 1)
                throw new PdfFilterException("A single DecodeParms dictionary requires exactly one stream filter.");
            result[0] = single;
            return result;
        }

        if (parameters is not PdfArray array || array.Count != filterCount)
            throw new PdfFilterException("A DecodeParms array must have one entry per stream filter.");

        for (int i = 0; i < array.Count; i++)
        {
            PdfObject resolvedItem = Resolve(array[i], resolve, "stream /DecodeParms array entry");
            result[i] = resolvedItem switch
            {
                PdfNull => null,
                PdfDictionary item => item,
                _ => throw new PdfFilterException("Each DecodeParms entry must be a dictionary or null.")
            };
        }
        return result;
    }

    private static PdfObject Resolve(
        PdfObject value, Func<PdfIndirectReference, PdfObject>? resolve, string description)
    {
        if (resolve is null) return value;
        var visited = new HashSet<(int ObjectNumber, int Generation)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32)
                throw new PdfFilterException($"The {description} indirect chain is too deep.");
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new PdfFilterException($"The {description} indirect chain contains a cycle.");
            value = resolve(reference);
        }
        return value;
    }

    private static byte[] DecodeFlate(
        byte[] encoded, int maximumDecodedBytes, bool compatibilityRecovery)
    {
        try
        {
            using var input = new MemoryStream(encoded, writable: false);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream();
            byte[] buffer = new byte[81_920];
            while (true)
            {
                int read = zlib.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                EnsureWithinLimit(output.Length + read, maximumDecodedBytes);
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (PdfFilterException)
        {
            throw;
        }
        catch (InvalidDataException ex)
        {
            if (compatibilityRecovery && HasZlibHeader(encoded))
            {
                try
                {
                    using var input = new MemoryStream(
                        encoded, 2, encoded.Length - 6, writable: false);
                    using var deflate = new DeflateStream(input, CompressionMode.Decompress);
                    using var output = new MemoryStream();
                    byte[] buffer = new byte[81_920];
                    while (true)
                    {
                        int read = deflate.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;
                        EnsureWithinLimit(output.Length + read, maximumDecodedBytes);
                        output.Write(buffer, 0, read);
                    }
                    return output.ToArray();
                }
                catch (PdfFilterException)
                {
                    throw;
                }
                catch (InvalidDataException)
                {
                }
            }
            throw new PdfFilterException("The FlateDecode stream contains invalid zlib data.", ex);
        }
    }

    private static bool HasZlibHeader(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length < 6) return false;
        int compression = encoded[0];
        int flags = encoded[1];
        return (compression & 15) == 8
            && compression >> 4 <= 7
            && (flags & 32) == 0
            && ((compression << 8) + flags) % 31 == 0;
    }

    private static byte[] ReversePredictor(
        byte[] data, PdfDictionary? parameters,
        Func<PdfIndirectReference, PdfObject>? resolve, int maximumDecodedBytes,
        bool compatibilityRecovery)
    {
        if (parameters is null)
            return data;

        int predictor = GetInteger(parameters, PredictorName, 1, resolve);
        if (predictor == 1)
            return data;

        int colors = GetPositiveInteger(parameters, ColorsName, 1, resolve);
        int bitsPerComponent = GetPositiveInteger(
            parameters, BitsPerComponentName, 8, resolve);
        int columns = GetPositiveInteger(parameters, ColumnsName, 1, resolve);
        if (bitsPerComponent is not (1 or 2 or 4 or 8 or 16))
            throw new PdfFilterException("Predictor BitsPerComponent must be 1, 2, 4, 8, or 16.");

        int bytesPerPixel;
        int rowLength;
        try
        {
            bytesPerPixel = checked(colors * bitsPerComponent + 7) / 8;
            rowLength = checked(columns * colors * bitsPerComponent + 7) / 8;
        }
        catch (OverflowException ex)
        {
            throw new PdfFilterException("Predictor dimensions exceed the supported range.", ex);
        }

        if (predictor == 2)
            return ReverseTiffPredictor(
                data, rowLength, colors, columns, bitsPerComponent);
        if (predictor is >= 10 and <= 15)
            return ReversePngPredictor(
                data, rowLength, bytesPerPixel, predictor, maximumDecodedBytes,
                compatibilityRecovery);

        throw new PdfFilterException($"Predictor {predictor} is not defined by PDF.");
    }

    private static byte[] ReverseTiffPredictor(
        byte[] data, int rowLength, int colors, int columns, int bitsPerComponent)
    {
        if (rowLength == 0 || data.Length % rowLength != 0)
            throw new PdfFilterException("TIFF predictor data does not contain complete rows.");

        byte[] decoded = (byte[])data.Clone();
        int samplesPerRow = checked(colors * columns);
        int mask = (1 << bitsPerComponent) - 1;
        for (int row = 0; row < decoded.Length; row += rowLength)
        {
            for (int sample = colors; sample < samplesPerRow; sample++)
            {
                int value = (ReadBits(decoded, row, sample * bitsPerComponent, bitsPerComponent)
                    + ReadBits(decoded, row, (sample - colors) * bitsPerComponent,
                        bitsPerComponent)) & mask;
                WriteBits(decoded, row, sample * bitsPerComponent, bitsPerComponent, value);
            }
        }
        return decoded;
    }

    private static int ReadBits(
        ReadOnlySpan<byte> data, int rowOffset, int bitOffset, int bitCount)
    {
        int value = 0;
        for (int bit = 0; bit < bitCount; bit++)
        {
            int position = bitOffset + bit;
            value = (value << 1)
                | ((data[rowOffset + position / 8] >> (7 - position % 8)) & 1);
        }
        return value;
    }

    private static void WriteBits(
        Span<byte> data, int rowOffset, int bitOffset, int bitCount, int value)
    {
        for (int bit = 0; bit < bitCount; bit++)
        {
            int position = bitOffset + bit;
            int shift = 7 - position % 8;
            byte mask = (byte)(1 << shift);
            int sourceShift = bitCount - bit - 1;
            if (((value >> sourceShift) & 1) != 0)
                data[rowOffset + position / 8] |= mask;
            else
                data[rowOffset + position / 8] &= (byte)~mask;
        }
    }

    private static byte[] ReversePngPredictor(
        byte[] data,
        int rowLength,
        int bytesPerPixel,
        int predictor,
        int maximumDecodedBytes,
        bool compatibilityRecovery)
    {
        int encodedRowLength;
        try
        {
            encodedRowLength = checked(rowLength + 1);
        }
        catch (OverflowException ex)
        {
            throw new PdfFilterException("PNG predictor dimensions exceed the supported range.", ex);
        }
        if (data.Length % encodedRowLength != 0)
            throw new PdfFilterException("PNG predictor data does not contain complete rows.");

        int rowCount = data.Length / encodedRowLength;
        int decodedLength = checked(rowCount * rowLength);
        EnsureWithinLimit(decodedLength, maximumDecodedBytes);
        var decoded = new byte[decodedLength];

        for (int row = 0; row < rowCount; row++)
        {
            int inputStart = row * encodedRowLength;
            int outputStart = row * rowLength;
            byte filter = data[inputStart];
            if (filter > 4)
                throw new PdfFilterException($"PNG predictor row {row} uses unknown filter {filter}.");
            if (!compatibilityRecovery && predictor != 15 && filter != predictor - 10)
                throw new PdfFilterException(
                    $"PNG predictor row {row} uses filter {filter}, but predictor {predictor} requires filter {predictor - 10}.");

            for (int column = 0; column < rowLength; column++)
            {
                byte raw = data[inputStart + 1 + column];
                byte left = column >= bytesPerPixel ? decoded[outputStart + column - bytesPerPixel] : (byte)0;
                byte above = row > 0 ? decoded[outputStart - rowLength + column] : (byte)0;
                byte upperLeft = row > 0 && column >= bytesPerPixel
                    ? decoded[outputStart - rowLength + column - bytesPerPixel]
                    : (byte)0;

                decoded[outputStart + column] = filter switch
                {
                    0 => raw,
                    1 => unchecked((byte)(raw + left)),
                    2 => unchecked((byte)(raw + above)),
                    3 => unchecked((byte)(raw + ((left + above) / 2))),
                    4 => unchecked((byte)(raw + Paeth(left, above, upperLeft))),
                    _ => throw new InvalidOperationException()
                };
            }
        }

        return decoded;
    }

    private static byte Paeth(byte left, byte above, byte upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        if (leftDistance <= aboveDistance && leftDistance <= upperLeftDistance)
            return left;
        return aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static int GetPositiveInteger(
        PdfDictionary dictionary, PdfName name, int defaultValue,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        int value = GetInteger(dictionary, name, defaultValue, resolve);
        if (value <= 0)
            throw new PdfFilterException($"DecodeParms {name} must be greater than zero.");
        return value;
    }

    private static int GetInteger(
        PdfDictionary dictionary, PdfName name, int defaultValue,
        Func<PdfIndirectReference, PdfObject>? resolve)
    {
        if (!dictionary.TryGetValue(name, out PdfObject value))
            return defaultValue;
        value = Resolve(value, resolve, $"DecodeParms {name}");
        if (value is not PdfInteger integer || integer.Value is < int.MinValue or > int.MaxValue)
            throw new PdfFilterException($"DecodeParms {name} must be an integer.");
        return (int)integer.Value;
    }

    private static void EnsureWithinLimit(long length, int maximumDecodedBytes)
    {
        if (length > maximumDecodedBytes)
            throw new PdfFilterException($"Decoded stream exceeds the {maximumDecodedBytes:N0}-byte safety limit.");
    }
}
