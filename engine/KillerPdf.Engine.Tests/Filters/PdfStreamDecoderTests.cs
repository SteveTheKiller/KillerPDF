using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Objects;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfStreamDecoderTests
{
    [Fact]
    public void Decode_AllowsBoundedPngPredictorRowBytesBeforeReconstruction()
    {
        byte[] predicted = [0, 1, 2, 3, 4, 0, 5, 6, 7, 8];
        byte[] compressed = Compress(predicted);
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", new PdfDictionary([
                Pair("Predictor", new PdfInteger(15)),
                Pair("Columns", new PdfInteger(4))]))]), compressed);

        byte[] decoded = PdfStreamDecoder.Decode(stream, maximumDecodedBytes: 8);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, decoded);
    }

    [Fact]
    public void Decode_AllowsBoundedIntermediateBytesInMultiFilterPipeline()
    {
        byte[] expected = [0x41];
        byte[] compressed = Compress(expected);
        byte[] hexadecimal = Encoding.ASCII.GetBytes(
            Convert.ToHexString(compressed) + ">");
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfArray([
                Name("ASCIIHexDecode"), Name("FlateDecode")]))]), hexadecimal);

        byte[] decoded = PdfStreamDecoder.Decode(stream, maximumDecodedBytes: 1);

        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Decode_EnforcesFinalLimitAfterBoundedMultiFilterIntermediate()
    {
        byte[] compressed = Compress([0x41, 0x42]);
        byte[] hexadecimal = Encoding.ASCII.GetBytes(
            Convert.ToHexString(compressed) + ">");
        var stream = new PdfStream(new PdfDictionary([
            Pair("Filter", new PdfArray([
                Name("ASCIIHexDecode"), Name("FlateDecode")]))]), hexadecimal);

        PdfFilterException error = Assert.Throws<PdfFilterException>(() =>
            PdfStreamDecoder.Decode(stream, maximumDecodedBytes: 1));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_ReturnsUnfilteredBytes()
    {
        byte[] source = [0x00, 0xFF, 0x41];

        Assert.Equal(source, PdfStreamDecoder.Decode(Stream(source)));
    }

    [Fact]
    public void Decode_EnforcesOutputLimitForUnfilteredBytes()
    {
        PdfStream stream = Stream(new byte[101]);

        PdfFilterException error = Assert.Throws<PdfFilterException>(
            () => PdfStreamDecoder.Decode(stream, 100));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_InflatesZlibData()
    {
        byte[] expected = Encoding.ASCII.GetBytes("PDF 2.0 object stream");
        PdfStream stream = Stream(Compress(expected), Pair("Filter", Name("FlateDecode")));

        Assert.Equal(expected, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ResolvesIndirectFilterParametersAndScalarValues()
    {
        byte[] expected = Encoding.ASCII.GetBytes("indirect stream metadata");
        var objects = new Dictionary<int, PdfObject>
        {
            [1] = new PdfIndirectReference(2, 0),
            [2] = Name("FlateDecode"),
            [3] = new PdfIndirectReference(4, 0),
            [4] = Dictionary(Pair("Predictor", new PdfIndirectReference(5, 0))),
            [5] = new PdfInteger(1)
        };
        PdfStream stream = Stream(Compress(expected),
            Pair("Filter", new PdfIndirectReference(1, 0)),
            Pair("DecodeParms", new PdfIndirectReference(3, 0)));

        Assert.Equal(expected, PdfStreamDecoder.Decode(stream, reference => objects[reference.ObjectNumber]));
    }

    [Fact]
    public void Decode_RejectsIndirectFilterCycles()
    {
        var objects = new Dictionary<int, PdfObject>
        {
            [1] = new PdfIndirectReference(2, 0),
            [2] = new PdfIndirectReference(1, 0)
        };
        PdfStream stream = Stream([], Pair("Filter", new PdfIndirectReference(1, 0)));

        PdfFilterException error = Assert.Throws<PdfFilterException>(() =>
            PdfStreamDecoder.Decode(stream, reference => objects[reference.ObjectNumber]));

        Assert.Contains("cycle", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_DecodesAsciiHexIncludingOddFinalNibble()
    {
        PdfStream stream = Stream("61 62 6>"u8.ToArray(),
            Pair("Filter", Name("ASCIIHexDecode")));

        Assert.Equal("ab`"u8.ToArray(), PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_DecodesAscii85ZeroTuple()
    {
        PdfStream stream = Stream("z~>"u8.ToArray(),
            Pair("Filter", Name("ASCII85Decode")));

        Assert.Equal(new byte[4], PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_DecodesRunLengthLiteralAndRepeatRuns()
    {
        PdfStream stream = Stream([2, (byte)'A', (byte)'B', (byte)'C', 254, (byte)'Z', 128],
            Pair("Filter", Name("RunLengthDecode")));

        Assert.Equal("ABCZZZ", Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Fact]
    public void Decode_DecodesLzwCodesWithClearAndEndMarkers()
    {
        PdfStream stream = Stream(PackNineBitCodes(256, 'A', 'B', 'C', 257),
            Pair("Filter", Name("LZWDecode")));

        Assert.Equal("ABC", Encoding.ASCII.GetString(PdfStreamDecoder.Decode(stream)));
    }

    [Fact]
    public void Decode_AppliesLosslessFilterPipelineInDeclaredOrder()
    {
        byte[] expected = "chained PDF filters"u8.ToArray();
        byte[] compressed = Compress(expected);
        byte[] hexadecimal = Encoding.ASCII.GetBytes(
            Convert.ToHexString(compressed) + ">");
        PdfStream stream = Stream(hexadecimal,
            Pair("Filter", new PdfArray([Name("ASCIIHexDecode"), Name("FlateDecode")])));

        Assert.Equal(expected, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_DoesNotApplyPredictorsToFiltersThatDoNotDefineThem()
    {
        var parameters = new PdfDictionary([
            Pair("Predictor", new PdfInteger(2)),
            Pair("Columns", new PdfInteger(3))
        ]);
        PdfStream stream = Stream("010101>"u8.ToArray(),
            Pair("Filter", Name("ASCIIHexDecode")),
            Pair("DecodeParms", parameters));

        Assert.Equal(new byte[] { 1, 1, 1 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReversesPngUpPrediction()
    {
        byte[] predicted = [2, 10, 20, 30, 2, 5, 5, 5];
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(12)),
            Pair("Columns", new PdfInteger(3)));
        PdfStream stream = Stream(
            Compress(predicted),
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", parameters));

        Assert.Equal(new byte[] { 10, 20, 30, 15, 25, 35 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_RejectsPngRowFilterThatContradictsFixedPredictor()
    {
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(12)),
            Pair("Columns", new PdfInteger(3)));
        PdfStream stream = Stream(
            Compress([1, 10, 20, 30]),
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", parameters));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_AllowsMixedRowFiltersForOptimumPngPredictor()
    {
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(15)),
            Pair("Columns", new PdfInteger(3)));
        PdfStream stream = Stream(
            Compress([0, 1, 2, 3, 2, 1, 1, 1]),
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", parameters));

        Assert.Equal([1, 2, 3, 2, 3, 4], PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReversesTiffPrediction()
    {
        byte[] predicted = [10, 10, 10, 40, 10, 10];
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(2)),
            Pair("Columns", new PdfInteger(3)));
        PdfStream stream = Stream(
            Compress(predicted),
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", parameters));

        Assert.Equal(new byte[] { 10, 20, 30, 40, 50, 60 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReversesPackedFourBitTiffPrediction()
    {
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(2)),
            Pair("BitsPerComponent", new PdfInteger(4)),
            Pair("Columns", new PdfInteger(4)));
        PdfStream stream = Stream(Compress([0x12, 0x48]),
            Pair("Filter", Name("FlateDecode")), Pair("DecodeParms", parameters));

        Assert.Equal(new byte[] { 0x13, 0x7F }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_EnforcesOutputLimitWhileInflating()
    {
        PdfStream stream = Stream(Compress(new byte[10_000]), Pair("Filter", Name("FlateDecode")));

        PdfFilterException error = Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 100));
        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_EnforcesOutputLimitForCryptPassThrough()
    {
        PdfStream stream = Stream(new byte[101], Pair("Filter", Name("Crypt")));

        PdfFilterException error = Assert.Throws<PdfFilterException>(
            () => PdfStreamDecoder.Decode(stream, 100));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_AllowsCryptPassThroughAtOutputLimit()
    {
        byte[] source = new byte[100];
        PdfStream stream = Stream(source, Pair("Filter", Name("Crypt")));

        Assert.Equal(source, PdfStreamDecoder.Decode(stream, 100));
    }

    [Fact]
    public void Decode_DecodesBaselineJpegToRgbSamples()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwyiiivw8/0oP/2Q==");
        PdfStream stream = Stream(jpeg, Pair("Filter", Name("DCTDecode")));

        byte[] decoded = PdfStreamDecoder.Decode(stream, 8 * 8 * 3);

        Assert.Equal(8 * 8 * 3, decoded.Length);
        Assert.All(decoded.Chunk(3), pixel =>
        {
            Assert.InRange(pixel[0], 235, 245);
            Assert.InRange(pixel[1], 15, 25);
            Assert.InRange(pixel[2], 5, 15);
        });
    }

    [Fact]
    public void Decode_DecodesSubsampledJpegWithPartialEdgeBlocks()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAQCAwMDAgQDAwMEBAQEBQkGBQUFBQsICAYJDQsNDQ0LDAwOEBQRDg8TDwwMEhgSExUWFxcXDhEZGxkWGhQWFxb/2wBDAQQEBAUFBQoGBgoWDwwPFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhYWFhb/wAARCAANABEDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwD5z8KfDb7v+j/pXpfhT4bfd/0f9K9j8KeFNN+Xj/x2vS/CnhPTfl4/8dry8Hm8tD57gnxFre7qz58/4Vt/07/pRX1b/wAIpp3p/wCO0V639ryP2b/iItbuz//Z");
        PdfStream stream = Stream(jpeg, Pair("Filter", Name("DCTDecode")));

        byte[] decoded = PdfStreamDecoder.Decode(stream, 17 * 13 * 3);

        Assert.Equal(17 * 13 * 3, decoded.Length);
        AssertPixelNear(decoded, 17, 0, 0, 0, 1, 0);
        AssertPixelNear(decoded, 17, 8, 6, 104, 115, 99);
        AssertPixelNear(decoded, 17, 16, 12, 209, 225, 198);
    }

    [Fact]
    public void Decode_RejectsJpegBeyondOutputLimit()
    {
        byte[] jpeg = Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wAARCAAIAAgDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDwyiiivw8/0oP/2Q==");
        PdfStream stream = Stream(jpeg, Pair("Filter", Name("DCTDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 8 * 8 * 3 - 1));
    }

    [Fact]
    public void Decode_RejectsMalformedJpegData()
    {
        PdfStream stream = Stream([0xFF, 0xD8, 0xFF, 0xD9],
            Pair("Filter", Name("DCTDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_RejectsInvalidJpegColorTransform()
    {
        PdfStream stream = Stream([], Pair("Filter", Name("DCTDecode")),
            Pair("DecodeParms", Dictionary(Pair("ColorTransform", new PdfInteger(2)))));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_RejectsInvalidZlibData()
    {
        PdfStream stream = Stream("not zlib"u8.ToArray(), Pair("Filter", Name("FlateDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
    }

    private static void AssertPixelNear(
        byte[] pixels, int width, int x, int y, int red, int green, int blue)
    {
        int offset = (y * width + x) * 3;
        Assert.InRange(pixels[offset], Math.Max(0, red - 25), Math.Min(255, red + 25));
        Assert.InRange(pixels[offset + 1], Math.Max(0, green - 25), Math.Min(255, green + 25));
        Assert.InRange(pixels[offset + 2], Math.Max(0, blue - 25), Math.Min(255, blue + 25));
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(source);
        return output.ToArray();
    }

    private static byte[] PackNineBitCodes(params int[] codes)
    {
        byte[] result = new byte[(codes.Length * 9 + 7) / 8];
        int bitOffset = 0;
        foreach (int code in codes)
            for (int bit = 8; bit >= 0; bit--, bitOffset++)
                result[bitOffset / 8] |= (byte)(((code >> bit) & 1) << (7 - bitOffset % 8));
        return result;
    }

    private static PdfStream Stream(byte[] data, params KeyValuePair<PdfName, PdfObject>[] entries) =>
        new(Dictionary(entries), data);

    private static PdfDictionary Dictionary(params KeyValuePair<PdfName, PdfObject>[] entries) => new(entries);
    private static KeyValuePair<PdfName, PdfObject> Pair(string name, PdfObject value) => new(Name(name), value);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
