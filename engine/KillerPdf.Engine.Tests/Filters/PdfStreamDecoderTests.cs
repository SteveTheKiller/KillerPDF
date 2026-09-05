using System.IO.Compression;
using System.Security.Cryptography;
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
    public void Decode_IgnoresDecodeParametersWithoutAFilter()
    {
        byte[] source = [0x00, 0xFF, 0x41];
        PdfStream stream = Stream(source, Pair("DecodeParms", Dictionary()));

        Assert.Equal(source, PdfStreamDecoder.Decode(stream));
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
    public void Decode_CompatibilityRecoveryIgnoresInvalidZlibChecksum()
    {
        byte[] expected = Encoding.ASCII.GetBytes("recover a broken Adler checksum");
        byte[] encoded = Compress(expected);
        encoded[^1] ^= 0x01;
        PdfStream stream = Stream(encoded, Pair("Filter", Name("FlateDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal(expected,
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, value => value));
    }

    [Fact]
    public void Decode_CompatibilityRecoveryStillRejectsInvalidDeflateData()
    {
        PdfStream stream = Stream([0x78, 0x9C, 0xFF, 0xFF, 0, 0, 0, 0],
            Pair("Filter", Name("FlateDecode")));

        Assert.Throws<PdfFilterException>(() =>
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream, value => value));
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
    public void Decode_DecodesBaselineAndExtendedSequentialJpegToRgbSamples()
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
        int frameMarker = Enumerable.Range(1, jpeg.Length - 1)
            .Single(index => jpeg[index - 1] == 0xFF && jpeg[index] == 0xC0);
        jpeg[frameMarker] = 0xC1;
        PdfStream extended = Stream(jpeg, Pair("Filter", Name("DCTDecode")));

        Assert.Equal(decoded, PdfStreamDecoder.Decode(extended, 8 * 8 * 3));
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
    public void Decode_DecodesJpeg2000WithoutApplyingContainerColorMapping()
    {
        byte[] jpeg2000 = Convert.FromBase64String(
            "AAAADGpQICANCocKAAAAHGZ0eXBqcDIgAAAAAGpwMiBqcHhianB4IAAAAB5ycmVxAfj4AAUAAYAABUAADCAAEhAANwgAAAAAAFhqcDJoAAAAFmloZHIAAADsAAAA7AABBwcBAAAAAA9jb2xyAQIBAAAADAAAABNwY2xyAAEEBwcHBwD//wAAAAAYY21hcAAAAQAAAAEBAAABAgAAAQMAAAC7anAyY/9P/1EAKQAAAAAA7AAAAOwAAAAAAAAAAAAAAOwAAADsAAAAAAAAAAAAAQcBAf9SAAwAAQABAAUDAwAB/1wAE0BASEhQSEhQSEhQSEhQSEhQ/5AACgAAAAAAFgAG/5PfgCgRUFSjb/+QAAoAAAAAAA8BBv+TgP+QAAoAAAAAAA8CBv+TgP+QAAoAAAAAAA8DBv+TgP+QAAoAAAAAAA8EBv+TgP+QAAoAAAAAAA8FBv+TgP/Z");
        PdfStream stream = Stream(jpeg2000, Pair("Filter", Name("JPXDecode")));

        byte[] decoded = PdfStreamDecoder.Decode(stream, 236 * 236);

        Assert.Equal(new byte[236 * 236], decoded);
    }

    [Fact]
    public void Decode_RejectsJpeg2000BeyondOutputLimit()
    {
        byte[] jpeg2000 = Convert.FromBase64String(
            "AAAADGpQICANCocKAAAAHGZ0eXBqcDIgAAAAAGpwMiBqcHhianB4IAAAAB5ycmVxAfj4AAUAAYAABUAADCAAEhAANwgAAAAAAFhqcDJoAAAAFmloZHIAAADsAAAA7AABBwcBAAAAAA9jb2xyAQIBAAAADAAAABNwY2xyAAEEBwcHBwD//wAAAAAYY21hcAAAAQAAAAEBAAABAgAAAQMAAAC7anAyY/9P/1EAKQAAAAAA7AAAAOwAAAAAAAAAAAAAAOwAAADsAAAAAAAAAAAAAQcBAf9SAAwAAQABAAUDAwAB/1wAE0BASEhQSEhQSEhQSEhQSEhQ/5AACgAAAAAAFgAG/5PfgCgRUFSjb/+QAAoAAAAAAA8BBv+TgP+QAAoAAAAAAA8CBv+TgP+QAAoAAAAAAA8DBv+TgP+QAAoAAAAAAA8EBv+TgP+QAAoAAAAAAA8FBv+TgP/Z");
        PdfStream stream = Stream(jpeg2000, Pair("Filter", Name("JPXDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream, 236 * 236 - 1));
    }

    [Fact]
    public void Decode_ReconstructsProgressiveJpegScans()
    {
        PdfStream stream = Stream(ProgressiveGrayJpeg(),
            Pair("Filter", Name("DCTDecode")));

        byte[] decoded = PdfStreamDecoder.Decode(stream, 64);

        Assert.Equal(64, decoded.Length);
        Assert.All(decoded.Chunk(8), row =>
            Assert.Equal(new byte[] { 255, 255, 235, 186, 133, 85, 47, 27 }, row));
    }

    [Fact]
    public void Decode_RecoversProgressiveAcRefinementBeyondItsBand()
    {
        byte[] source = ProgressiveGrayJpeg();
        source = [.. source.AsSpan(0, source.Length - 4), 0x40, 0xFF, 0xD9];
        PdfStream stream = Stream(source, Pair("Filter", Name("DCTDecode")));

        Assert.Equal(64, PdfStreamDecoder.Decode(stream, 64).Length);
    }

    [Fact]
    public void Decode_RecoversProgressiveScanEndedByMarker()
    {
        byte[] source = ProgressiveGrayJpeg();
        int scanCount = 0;
        int scanData = -1;
        for (int index = 0; index < source.Length - 3; index++)
            if (source[index] == 0xFF && source[index + 1] == 0xDA
                && ++scanCount == 3)
            {
                int length = source[index + 2] << 8 | source[index + 3];
                scanData = index + 2 + length;
                break;
            }
        Assert.True(scanData >= 0);
        source = [.. source.AsSpan(0, scanData), .. source.AsSpan(scanData + 1)];
        PdfStream stream = Stream(source, Pair("Filter", Name("DCTDecode")));

        Assert.Equal(64, PdfStreamDecoder.Decode(stream, 64).Length);
    }

    [Fact]
    public void Decode_ReconstructsOneDimensionalCcittScanLines()
    {
        PdfStream stream = Stream([0x89, 0xC0],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(Pair("Columns", new PdfInteger(8)))));

        Assert.Equal(new byte[] { 0b1110_0011 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_UsesImageWidthWhenCcittColumnsAreOmitted()
    {
        PdfStream stream = Stream([0x89, 0xC0],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("Width", new PdfInteger(8)),
            Pair("DecodeParms", Dictionary()));

        Assert.Equal(new byte[] { 0b1110_0011 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_HonorsCcittEndOfLineAlignmentAndBlackPolarity()
    {
        PdfStream stream = Stream([0x00, 0x18, 0x9C],
            Pair("Filter", Name("CCF")),
            Pair("DecodeParms", Dictionary(
                Pair("Columns", new PdfInteger(8)),
                Pair("EndOfLine", new PdfBoolean(true)),
                Pair("EncodedByteAlign", new PdfBoolean(true)),
                Pair("BlackIs1", new PdfBoolean(true)))));

        Assert.Equal(new byte[] { 0b0001_1100 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ExpandsCcittMakeupRuns()
    {
        PdfStream stream = Stream([0x4D, 0x9A, 0x80],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(Pair("Columns", new PdfInteger(1728)))));

        Assert.Equal(Enumerable.Repeat((byte)255, 216), PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReconstructsGroup4HorizontalAndVerticalModes()
    {
        PdfStream stream = Stream([0x98, 0xA0],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(
                Pair("K", new PdfInteger(-1)),
                Pair("Columns", new PdfInteger(8)),
                Pair("Rows", new PdfInteger(2)))));

        Assert.Equal(new byte[] { 0xFF, 0b1110_0011 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReconstructsGroup4PassModes()
    {
        PdfStream stream = Stream([0x31, 0x46],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(
                Pair("K", new PdfInteger(-1)),
                Pair("Columns", new PdfInteger(8)),
                Pair("Rows", new PdfInteger(2)))));

        Assert.Equal(new byte[] { 0b1110_0011, 0xFF }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ReconstructsGroup4VerticalOffsets()
    {
        PdfStream stream = Stream([0x31, 0x5B, 0x80],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(
                Pair("K", new PdfInteger(-1)),
                Pair("Columns", new PdfInteger(8)),
                Pair("Rows", new PdfInteger(2)))));

        Assert.Equal(new byte[] { 0b1110_0011, 0b1111_0001 },
            PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_ClipsCcittTransitionsAtScanLineBounds()
    {
        PdfStream stream = Stream(Convert.FromHexString(
            "26A0B9E07341C9D6107184F4F47DBD049E82B26A2D25874BBA5FAEE97F6BD2FF6BE830B8C7FAFFEBFFFFAFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEF5C20712040FFE0020020"),
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("Width", new PdfInteger(52)),
            Pair("Height", new PdfInteger(70)),
            Pair("DecodeParms", Dictionary(
                Pair("K", new PdfInteger(-1)),
                Pair("Columns", new PdfInteger(52)))));

        Assert.Equal(490, PdfStreamDecoder.Decode(stream).Length);
    }

    [Fact]
    public void Decode_ReconstructsMixedGroup3Rows()
    {
        PdfStream stream = Stream([0x00, 0x1C, 0x4E],
            Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(
                Pair("K", new PdfInteger(1)),
                Pair("Columns", new PdfInteger(8)),
                Pair("EndOfLine", new PdfBoolean(true)))));

        Assert.Equal(new byte[] { 0b1110_0011 }, PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_RejectsMalformedOrUnboundedCcittData()
    {
        PdfStream malformed = Stream([0x00], Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(Pair("K", new PdfInteger(-1)))));
        PdfStream bounded = Stream([0x89, 0xC0], Pair("Filter", Name("CCITTFaxDecode")),
            Pair("DecodeParms", Dictionary(Pair("Columns", new PdfInteger(8)))));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(malformed));
        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(bounded, 0));
    }

    [Fact]
    public void Decode_RejectsMalformedJpegData()
    {
        PdfStream stream = Stream([0xFF, 0xD8, 0xFF, 0xD9],
            Pair("Filter", Name("DCTDecode")));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
    }

    [Fact]
    public void Decode_CompatibilityRecoveryUsesPngRowFilterThatContradictsFixedPredictor()
    {
        PdfDictionary parameters = Dictionary(
            Pair("Predictor", new PdfInteger(10)),
            Pair("Columns", new PdfInteger(3)));
        PdfStream stream = Stream(
            Compress([1, 10, 10, 10]),
            Pair("Filter", Name("FlateDecode")),
            Pair("DecodeParms", parameters));

        Assert.Throws<PdfFilterException>(() => PdfStreamDecoder.Decode(stream));
        Assert.Equal([10, 20, 30],
            PdfStreamDecoder.DecodeWithCompatibilityRecovery(stream));
    }

    [Fact]
    public void Decode_DecodesJbig2ImageData()
    {
        byte[] encoded = Convert.FromBase64String(
            "l0pCMg0KGgoBAAAAAQAAAAAAAQAAAAAYAAEAAAABAAAAAenL9AAmrwS/8Hgv4ABAAAAAATAAAQAAABMAAABAAAAAOAAAAAAAAAAAAQAAAAAAAgABAQAAABwAAQAAAAIAAAAC5c34AHnghBCB8IIQhhB58ACAAAAAAwdCAAIBAAAAMQAAACUAAAAIAAAABAAAAAEADAkAEAAAAAUBEAAAAAAAAAAAAAAAAAAAAAxABwhwQdAAAAAEJwABAAAALAAAADYAAAAsAAAABAAAAAsAASagcc6n//////////////////////////jwAAAABRABAQAAAC0BBAQAAAAPINGEYRhF8vl8jxHDnkXy+X1ChQqqhGIv7uxEYiI1KgqDudzud4AAAAAGFyAFAQAAAFcAAAAgAAAAJAAAABAAAAAPAAEAAAAIAAAACQAAAAAAAAAABAAAAKqqqqqACACANtVVa1rUAEAELulS0tLSiqVKACACI+CVJLSSikqSVJLSSikqSUAEAEAAAAAHMQABAAAAAA==");
        PdfStream stream = Stream(encoded, Pair("Filter", Name("JBIG2Decode")));

        byte[] decoded = PdfStreamDecoder.Decode(stream, 448);

        Assert.Equal(448, decoded.Length);
        Assert.Equal("D806F2E73907D03B1DEFBB7AFADCEE5962D80A009A844D3170D8B3F8F446A2FD",
            Convert.ToHexString(SHA256.HashData(decoded)));
    }

    [Fact]
    public void Decode_DecodesJbig2ImageDataWithGlobalSegments()
    {
        byte[] globals = Convert.FromBase64String(
            "AAAAAAABAAAAA6sAAAP//f8C/v7+AAAAKgAAACqUaqb/Lo/erhxQ1aqfI2lRqDbzVxH8KzDRZmZsUJ43oC3vqY7/fy0kgqQLZLeMgFGzTYuwokbUJTDeOr1Ft8VEXdtYH/avK0gueNsaZFF/XeMroHfvs97TxuFjdGGbdp5ItqsJnyd2T8X9SjqlZYlk9Yt17VI3iyYA7VOw6akdSsY5pgruvn2Oq4XHG9kmnBUJwZi1Yom4cBmRaUfu1EGUB1IAPaPrkrc8/1+j0DjhwlKlWnYgf+M/2MbiU3Kog2431bKtT4omDxrcfyW8wSQkzwhscgqIiWipxXXatDEI1/ytzezIGsUUMZ+hp1phypBZhA/m9uF0hNLBoCe0Of2A7YsD1Sgi/qZBFSPAZjqE5xbYZxmmSgH2TXM8D7sFwBOcvGoaUCv8wth3ewt89q9/2hQ1FZKDeoxb8Mx8Ih+Of3o62T9reUeHnCU38d35et4BeyeY4Dzkc7Bkt9z3/UZJPBY5+r7BHmh6fFk9RzhOu5JNePXgDeTBcCNIwSqDjvTe2ALa5xw82LDKVTKer4U9BEAPOeTa7u5V0vkheEETz1zppbVRa37nUboeqAdTHLZsaC8RWSwcJ3WbF8RPl1PA0yI8+4G2go87R0rGNVRYtM3V5vxB1Rl2V/h//AVZYRlTXGIEPUqWwcrVsX8MIXCrznHUsZqkvkqolsFJolxeiLTCcLJMvTcIlxmRWINgIGdlMIhZF7LcYUekCwr0kMr440pRBcBF3dMYlC6NvDDBfgZFLeKghIa1ugLfK5rCM9RtRUExHp/JkG9fuRgtWtAcz8XD3G47jynPnoydb8rv8d8Er4+rdlHd5gB5ijiqmtJ7Zm0dR+z9dMNkWuHn5u+wMU5X44iAB0MHKJqrPl/QPR9AH43RuuT3X7OpV5+x+4VI91V6tgIf3nwy2OxD9u9bTiwPFYy0Z6tLUgpxMc2bB50gPtS9X/4qD9gSU8O4qdlQ8RrTt0g8fwDhLWW5YfnDoM8MeS2+hi1RLvj4dzmyJZ7okJAilWTR1OCL/Bwmj2EY7dFglmdOBTqnzX8oduLIcCAWdxEoySXtqT8VzIMKkLvdmWpfSZQqvqNqjc9DavZ0cz1wWoHyjxjlXbK6sAorNeX9BeZL9fXisw0osFCovlP0oya2fr6UQFGEpnV7UOdeAIE1kq9H7J3aV/jsAMz/fX20cL1BEXAcGlGvGBacC1Upt92BoukKHr7WGi0ovJVkKO8IF4xH/6w=");
        byte[] encoded = Convert.FromBase64String(
            "AAAAATAAAQAAABMAAAkQAAAKcAAAAAAAAAAAQAAAAAAAAgYgAAEAAAEEAAAJEAAACnAAAAAAAAAAAAIAEAAAAHGN0fhKUK/bqCoKKRt0VWmWYApdnvtfE7vImFjXLUvvcSLh1GTu/dcrF/MItrblcCQyAZttAIgAae0UMkX6gmn06ykzjEBZ2wMSbzbuOeS831bbqrPYRVeGMB5aKp9wL6fOpjDkQRlsnSKX9nXJUnmlUahiTyuouJ9eEqJIEEfZi9tgasJBwkbfLTX5+nOUhq1gW6lq1qzVRgWUH/MAc2wz+ORIX6WadHZk4dnL44V9FPXHORJzGbLZ9O0wSx/T+XzyUP5paJtZTRU96aRPIHYpRiM/tlWQ1pMXPRgUKxJxr325vnQgYeiEtfIP/6w=");
        PdfStream globalStream = Stream(globals);
        PdfStream stream = Stream(encoded,
            Pair("Filter", Name("JBIG2Decode")),
            Pair("DecodeParms", Dictionary(
                Pair("JBIG2Globals", new PdfIndirectReference(7, 0)))));

        byte[] decoded = PdfStreamDecoder.Decode(stream,
            reference => reference.ObjectNumber == 7 ? globalStream
                : throw new InvalidOperationException(), 774_880);

        Assert.Equal(774_880, decoded.Length);
        Assert.Equal("3CFC9C001A2D44B2711378AD78C044F05C182121D24EF91C36AD285BFC5BB8C5",
            Convert.ToHexString(SHA256.HashData(decoded)));
    }

    [Fact]
    public void Decode_RejectsJbig2ImageBeforeUnboundedBitmapAllocation()
    {
        byte[] encoded = Convert.FromBase64String(
            "l0pCMg0KGgoBAAAAAQAAAAAAAQAAAAAYAAEAAAABAAAAAenL9AAmrwS/8Hgv4ABAAAAAATAAAQAAABMAAABAAAAAOAAAAAAAAAAAAQAAAAAAAgABAQAAABwAAQAAAAIAAAAC5c34AHnghBCB8IIQhhB58ACAAAAAAwdCAAIBAAAAMQAAACUAAAAIAAAABAAAAAEADAkAEAAAAAUBEAAAAAAAAAAAAAAAAAAAAAxABwhwQdAAAAAEJwABAAAALAAAADYAAAAsAAAABAAAAAsAASagcc6n//////////////////////////jwAAAABRABAQAAAC0BBAQAAAAPINGEYRhF8vl8jxHDnkXy+X1ChQqqhGIv7uxEYiI1KgqDudzud4AAAAAGFyAFAQAAAFcAAAAgAAAAJAAAABAAAAAPAAEAAAAIAAAACQAAAAAAAAAABAAAAKqqqqqACACANtVVa1rUAEAELulS0tLSiqVKACACI+CVJLSSikqSVJLSSikqSUAEAEAAAAAHMQABAAAAAA==");
        PdfStream stream = Stream(encoded, Pair("Filter", Name("JBIG2Decode")));

        PdfFilterException error = Assert.Throws<PdfFilterException>(
            () => PdfStreamDecoder.Decode(stream, 447));

        Assert.Contains("safety limit", error.Message, StringComparison.Ordinal);
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

    private static byte[] ProgressiveGrayJpeg()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0x00, 0x43, 0x00 };
        jpeg.AddRange(Enumerable.Repeat((byte)255, 64));
        jpeg.AddRange(new byte[]
        {
            0xFF, 0xC2, 0x00, 0x0B, 0x08, 0x00, 0x08, 0x00, 0x08,
            0x01, 0x01, 0x11, 0x00,
            0xFF, 0xC4, 0x00, 0x27,
            0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x10, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x01, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00,
            0x7F,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x00, 0x10,
            0xFF, 0x00,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x01, 0x01, 0x01,
            0x7F,
            0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x01, 0x01, 0x10,
            0xFF, 0x00, 0xFF, 0xD9
        });
        return [.. jpeg];
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
