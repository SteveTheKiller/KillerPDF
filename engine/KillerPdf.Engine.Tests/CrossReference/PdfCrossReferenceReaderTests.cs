using System.IO.Compression;
using System.Text;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;
using Xunit;

namespace KillerPdf.Engine.Tests.CrossReference;

public sealed class PdfCrossReferenceReaderTests
{
    [Fact]
    public void ReadSection_ParsesClassicTableAndTrailer()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "xref\n" +
            "0 2\n" +
            "0000000000 65535 f \n" +
            "0000000017 00000 n \n" +
            "trailer\n" +
            "<< /Size 2 /Root 1 0 R >>");

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, 0);

        Assert.False(section.IsStream);
        Assert.Equal(2, section.Count);
        Assert.Equal(PdfCrossReferenceEntryType.Free, section[0].Type);
        Assert.Equal(65_535, section[0].Field2);
        Assert.Equal(PdfCrossReferenceEntryType.InUse, section[1].Type);
        Assert.Equal(17, section[1].Field1);
        var root = Assert.IsType<PdfIndirectReference>(section.Trailer[Name("Root")]);
        Assert.Equal(1, root.ObjectNumber);
    }

    [Fact]
    public void ReadSection_ParsesMultipleClassicSubsectionsAndRevisionPointers()
    {
        string padding = new('x', 200);
        byte[] source = Encoding.ASCII.GetBytes(
            "xref\n0 1\n0000000000 65535 f\n5 1\n0000000010 00000 n\n" +
            "trailer\n<< /Size 6 /Prev 3 /XRefStm 9 >>\n" + padding);

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, 0);

        Assert.Equal([0, 5], [.. section.Keys.Order()]);
        Assert.Equal(3, section.PreviousOffset);
        Assert.Equal(9, section.HybridStreamOffset);
    }

    [Fact]
    public void ReadSection_ParsesUncompressedCrossReferenceStream()
    {
        byte[] rows =
        [
            0, 0, 0, 255, 255, // free: next object 0, generation 65,535
            1, 0, 10, 0, 0,   // in use: byte offset 10, generation 0
            2, 0, 7, 0, 3     // compressed: object stream 7, index 3
        ];
        byte[] source = XrefStream(rows,
            "<< /Type /XRef /Size 8 /W [1 2 2] /Index [0 3] /Length 15 >>");

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, 0);

        Assert.True(section.IsStream);
        Assert.Equal(PdfCrossReferenceEntryType.Free, section[0].Type);
        Assert.Equal(PdfCrossReferenceEntryType.InUse, section[1].Type);
        Assert.Equal(10, section[1].Field1);
        Assert.Equal(PdfCrossReferenceEntryType.Compressed, section[2].Type);
        Assert.Equal(7, section[2].Field1);
        Assert.Equal(3, section[2].Field2);
    }

    [Fact]
    public void ReadSection_DecodesFlateCompressedCrossReferenceStreamWithIndexRanges()
    {
        byte[] rows =
        [
            0, 0, 0, 255, 255,
            1, 0, 10, 0, 0
        ];
        byte[] compressed = Compress(rows);
        string dictionary =
            $"<< /Type /XRef /Size 6 /W [1 2 2] /Index [0 1 5 1] /Filter /FlateDecode /Length {compressed.Length} >>";
        byte[] source = XrefStream(compressed, dictionary);

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, 0);

        Assert.Equal([0, 5], [.. section.Keys.Order()]);
        Assert.Equal(PdfCrossReferenceEntryType.InUse, section[5].Type);
    }

    [Fact]
    public void ReadSection_DefaultsZeroWidthTypeFieldToInUse()
    {
        byte[] rows = [0, 10, 0];
        byte[] source = XrefStream(rows, "<< /Type /XRef /Size 2 /W [0 2 1] /Index [1 1] /Length 3 >>");

        PdfCrossReferenceEntry entry = PdfCrossReferenceReader.ReadSection(source, 0)[1];

        Assert.Equal(PdfCrossReferenceEntryType.InUse, entry.Type);
        Assert.Equal(10, entry.Field1);
    }

    [Fact]
    public void ReadSection_TreatsFutureEntryTypesAsNullReferences()
    {
        byte[] rows = [3, 99, 88];
        byte[] source = XrefStream(rows, "<< /Type /XRef /Size 2 /W [1 1 1] /Index [1 1] /Length 3 >>");

        PdfCrossReferenceEntry entry = PdfCrossReferenceReader.ReadSection(source, 0)[1];

        Assert.Equal(PdfCrossReferenceEntryType.Null, entry.Type);
    }

    [Fact]
    public void ReadSection_CanonicalizesObjectZeroCrossReferenceStreamGeneration()
    {
        byte[] source = XrefStream(
            [0, 0, 0, 0, 0],
            "<< /Type /XRef /Size 1 /W [1 2 2] /Length 5 >>");

        PdfCrossReferenceEntry entry = PdfCrossReferenceReader.ReadSection(source, 0)[0];

        Assert.Equal(PdfCrossReferenceEntryType.Free, entry.Type);
        Assert.Equal(65_535, entry.Field2);
    }

    [Fact]
    public void ReadSection_CompatibilityRecoveryCanonicalizesMalformedObjectZeroGeneration()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "xref\n0 1\n0000000000 65536 f\ntrailer\n<< /Size 1 >>");

        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));
        PdfCrossReferenceEntry entry = PdfCrossReferenceReader.ReadSection(
            source, 0, compatibilityRecovery: true)[0];

        Assert.Equal(PdfCrossReferenceEntryType.Free, entry.Type);
        Assert.Equal(65_535, entry.Field2);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void ReadSection_RejectsImpossibleCompressedObjectStreamReferences(
        int objectStreamNumber)
    {
        byte[] source = XrefStream(
            [2, (byte)objectStreamNumber, 0],
            "<< /Type /XRef /Size 3 /W [1 1 1] /Index [1 1] /Length 3 >>");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));

        Assert.Contains("invalid object-stream field", error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSection_RejectsNonzeroCrossReferenceStreamGeneration()
    {
        byte[] source = XrefStream(
            [0, 0, 0, 255, 255],
            "<< /Type /XRef /Size 1 /W [1 2 2] /Length 5 >>");
        source[2] = (byte)'1';

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));

        Assert.Contains("stream object must have generation 0",
            error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("xref\n0 1\n0000009999 00000 n\ntrailer\n<< /Size 1 >>")]
    [InlineData("xref\n0 1\n0000000000 70000 f\ntrailer\n<< /Size 1 >>")]
    [InlineData("xref\n1 1\n0000000000 65535 n\ntrailer\n<< /Size 2 >>")]
    [InlineData("xref\n0 1\n0000000000 65535 z\ntrailer\n<< /Size 1 >>")]
    [InlineData("xref\n0 1\n0000000000 65535 f\ntrailer\n<< /Size 0 >>")]
    [InlineData("xref\n2 1\n0000000000 00000 f\ntrailer\n<< /Size 1 >>")]
    public void ReadSection_RejectsMalformedClassicTables(string source)
    {
        Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(Encoding.ASCII.GetBytes(source), 0));
    }

    [Fact]
    public void ReadSection_RejectsOversizedClassicSectionBeforeReadingEntries()
    {
        string source = $"xref\n0 {PdfCrossReferenceReader.MaximumEntriesPerSection + 1}\n";

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(Encoding.ASCII.GetBytes(source), 0));

        Assert.Contains("cannot contain more than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSection_RejectsRetiredGenerationInUseInCrossReferenceStream()
    {
        byte[] source = XrefStream(
            [1, 0, 0, 255, 255],
            "<< /Type /XRef /Size 10 /W [1 2 2] /Index [9 1] /Length 5 >>");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));

        Assert.Contains("invalid offset or generation", error.Message,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("<< /Type /Other /Size 1 /W [1 2 1] /Length 4 >>")]
    [InlineData("<< /Type /XRef /Size 1 /W [1 2] /Length 4 >>")]
    [InlineData("<< /Type /XRef /Size 1 /W [9 2 1] /Length 4 >>")]
    [InlineData("<< /Type /XRef /Size 1 /W [1 2 1] /Index [0] /Length 4 >>")]
    [InlineData("<< /Type /XRef /Size 1 /W [1 2 1] /Length 3 >>")]
    [InlineData("<< /Type /XRef /Size 1 /W [1 2 1] /XRefStm 0 /Length 4 >>")]
    public void ReadSection_RejectsMalformedCrossReferenceStreams(string dictionary)
    {
        byte[] source = XrefStream([0, 0, 0, 0], dictionary);

        Assert.Throws<PdfSyntaxException>(() => PdfCrossReferenceReader.ReadSection(source, 0));
    }

    [Fact]
    public void ReadSection_RejectsOversizedCrossReferenceStreamIndexBeforeDecodingRows()
    {
        int count = PdfCrossReferenceReader.MaximumEntriesPerSection + 1;
        byte[] source = XrefStream([], $"<< /Type /XRef /Size {count} /W [1 0 0] " +
            "/Filter /Unsupported /Length 0 >>");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));

        Assert.Contains("cannot contain more than", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSection_RejectsDisorderedCrossReferenceStreamIndexRanges()
    {
        byte[] source = XrefStream(
            [1, 0, 10, 0, 1, 0, 20, 0],
            "<< /Type /XRef /Size 3 /W [1 2 1] /Index [2 1 1 1] /Length 8 >>");

        PdfSyntaxException error = Assert.Throws<PdfSyntaxException>(() =>
            PdfCrossReferenceReader.ReadSection(source, 0));

        Assert.Contains("ranges must be ordered and nonoverlapping",
            error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadSection_UsesAbsoluteOffsetToFindSection()
    {
        byte[] prefix = "garbage\n"u8.ToArray();
        byte[] table = "xref\n0 1\n0000000000 65535 f\ntrailer\n<< /Size 1 >>"u8.ToArray();
        byte[] source = [.. prefix, .. table];

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, prefix.Length);

        Assert.Equal(prefix.Length, section.Offset);
    }

    [Fact]
    public void ReadSection_AllowsAFreeEntryAtTheTrailerSizeBoundary()
    {
        byte[] source = Encoding.ASCII.GetBytes(
            "xref\n0 2\n0000000000 65535 f\n0000000000 00000 f\n" +
            "trailer\n<< /Size 1 >>");

        PdfCrossReferenceSection section = PdfCrossReferenceReader.ReadSection(source, 0);

        Assert.Equal(PdfCrossReferenceEntryType.Free, section[1].Type);
    }

    private static byte[] XrefStream(byte[] payload, string dictionary)
    {
        byte[] prefix = Encoding.ASCII.GetBytes($"9 0 obj {dictionary} stream\n");
        byte[] suffix = "\nendstream endobj"u8.ToArray();
        return [.. prefix, .. payload, .. suffix];
    }

    private static byte[] Compress(byte[] source)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            zlib.Write(source);
        return output.ToArray();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
