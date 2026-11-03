using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuDocumentTests
{
    [Fact]
    public void RejectsAnInvalidSignatureWithAFormatError()
    {
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(new byte[34]));
    }

    [Fact]
    public void OpensThePublishedInfoExample()
    {
        var document = DjvuDocument.Open(File(Form("DJVU", Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1]))));
        Assert.Equal(new DjvuPageInfo(2202, 967, 300, 26, 0, DjvuOrientation.Upright), Assert.Single(document.Pages));
    }

    [Fact]
    public void RejectsChunksWhoseDeclaredExtentDoesNotMatchTheirContainer()
    {
        var file = File(Form("DJVU", Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1])));
        file[20] = 0xff;
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Theory]
    [InlineData("JUNK", 10, false)]
    [InlineData("INFO", 9, false)]
    [InlineData("INFO", 10, true)]
    public void RequiresExactlyOneCompleteInfoChunkAtTheStart(string firstId, int length, bool duplicate)
    {
        byte[] info = [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1];
        var chunks = new List<byte[]> { Chunk(firstId, info[..length]) };
        if (duplicate) chunks.Add(Chunk("INFO", info));
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(File(Form("DJVU", chunks.ToArray()))));
    }

    [Theory]
    [InlineData(1, DjvuOrientation.Upright)]
    [InlineData(2, DjvuOrientation.UpsideDown)]
    [InlineData(5, DjvuOrientation.Clockwise90)]
    [InlineData(6, DjvuOrientation.CounterClockwise90)]
    public void PreservesTheStoredOrientation(byte flags, DjvuOrientation expected)
    {
        var document = DjvuDocument.Open(File(Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, flags]))));
        Assert.Equal(expected, Assert.Single(document.Pages).Orientation);
    }

    [Theory]
    [InlineData(0, 100, 300)]
    [InlineData(100, 0, 300)]
    [InlineData(65535, 65535, 300)]
    [InlineData(100, 100, 0)]
    [InlineData(100, 100, 65535)]
    public void RejectsUnsafeDimensionsAndResolution(int width, int height, int dpi)
    {
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(File(Form("DJVU", Chunk("INFO",
            [(byte)(width >> 8), (byte)width, (byte)(height >> 8), (byte)height, 26, 0, (byte)dpi, (byte)(dpi >> 8), 22, 1])))));
    }

    [Fact]
    public void HonorsCancellationBeforeReadingInput()
    {
        Assert.Throws<OperationCanceledException>(() => DjvuDocument.Open(ReadOnlyMemory<byte>.Empty, new CancellationToken(true)));
    }

    [Fact]
    public void DiscoversBundledPagesAndReportsUnverifiedDirectoryIndexing()
    {
        var page = Form("DJVU", Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1]));
        var document = DjvuDocument.Open(File(Form("DJVM", Chunk("DIRM", [0x81, 0, 2, 0, 0, 0, 36, 0, 0, 0, 66]), page, page)));
        Assert.Equal(2, document.Pages.Count);
        Assert.Contains(document.Diagnostics, d => d.Code == "BundledDirectoryIndexUnsupported");
    }

    [Theory]
    [InlineData("INCL", "IncludedComponentUnsupported")]
    [InlineData("BG44", "ImageDecodingUnsupported")]
    public void ReportsRecognizedFeaturesWithoutDecodingOrResolvingThem(string chunkId, string diagnostic)
    {
        var document = DjvuDocument.Open(File(Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), Chunk(chunkId, [1]))));
        Assert.Contains(document.Diagnostics, d => d.Code == diagnostic);
    }

    [Fact]
    public void DiagnosesIndirectDocumentsWithoutFetchingTheirComponents()
    {
        var document = DjvuDocument.Open(File(Form("DJVM", Chunk("DIRM", [1, 0, 1]))));
        Assert.Empty(document.Pages);
        Assert.Contains(document.Diagnostics, d => d.Code == "ExternalComponentsUnsupported");
    }

    [Theory]
    [InlineData("length")]
    [InlineData("chunks")]
    [InlineData("pages")]
    public void RejectsInputsExceedingResourceBudgets(string budget)
    {
        var info = Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]);
        byte[] file;
        if (budget == "length")
        {
            file = new byte[64 * 1024 * 1024 + 1];
            File(Form("DJVU", info)).CopyTo(file, 0);
            "JUNK"u8.CopyTo(file.AsSpan(34));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(8), (uint)(file.Length - 12));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(38), (uint)(file.Length - 42));
        }
        else if (budget == "chunks")
            file = File(Form("DJVU", [info, .. Enumerable.Repeat(Chunk("JUNK", []), 65_537)]));
        else
            file = Bundle(Enumerable.Repeat(Form("DJVU", info), 4097).ToArray());
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsFormNestingInsidePagesAndSharedComponents(bool shared)
    {
        var info = Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]);
        var nested = Form("DJVU", info);
        var file = shared ? Bundle(Form("DJVI", nested)) : File(Form("DJVU", info, nested));
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(37u)]
    [InlineData(uint.MaxValue)]
    [InlineData(66u)]
    public void RejectsDirectoryOffsetsOutsideComponentStartsOrDuplicatingAComponent(uint offset)
    {
        var page = Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]));
        var file = Bundle(page, page);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(27), offset);
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AcceptsAnOddFinalChunkWithOptionalOuterPadding(bool outerPadding)
    {
        var info = Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1, 99]);
        var form = Chunk("FORM", [.. "DJVU"u8, .. info[..^1]]);
        var document = DjvuDocument.Open(File(outerPadding ? form : form[..^1]));
        Assert.Equal(2202, Assert.Single(document.Pages).Width);
    }

    [Fact]
    public void ReportsFuturePageVersionsWithoutClaimingFullCompatibility()
    {
        var document = DjvuDocument.Open(File(Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 99, 1, 0x2c, 1, 22, 1]))));
        Assert.Contains(document.Diagnostics, d => d.Code == "PageVersionUnsupported");
        Assert.Equal((byte)99, Assert.Single(document.Pages).MinorVersion);
    }

    [Theory]
    [InlineData("DJVM")]
    [InlineData("XXXX")]
    [InlineData("DJVI")]
    public void RejectsInvalidBundledComponentKindsAndSharedPageInfo(string componentType)
    {
        var component = Form(componentType, Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]));
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(Bundle(component)));
    }

    [Fact]
    public void ReportsUnknownDirectoryVersionsWithoutInterpretingTheirIndex()
    {
        var document = DjvuDocument.Open(File(Form("DJVM", Chunk("DIRM", [0x82, 0, 1]))));
        Assert.Empty(document.Pages);
        Assert.Contains(document.Diagnostics, d => d.Code == "DirectoryVersionUnsupported");
    }

    [Fact]
    public void RejectsEveryTruncationOfThePublishedMetadataContainer()
    {
        var file = File(Form("DJVU", Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1])));
        for (var length = 0; length < file.Length; length++)
            Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file.AsMemory(0, length)));
    }

    [Fact]
    public void RejectsNonzeroPaddingBeforeTheNextChunk()
    {
        var file = File(Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), Chunk("JUNK", [7]), Chunk("JUNK", [])));
        file[43] = 9;
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Fact]
    public void RejectsAPageChunkThatCrossesIntoTheFollowingBundledPage()
    {
        var page = Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]));
        var file = Bundle(page, page);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(52), 30);
        Assert.Throws<InvalidDataException>(() => DjvuDocument.Open(file));
    }

    [Fact]
    public void UnknownChunksDoNotPreventMetadataDiscovery()
    {
        var document = DjvuDocument.Open(File(Form("DJVU", Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), Chunk("XXXX", [7]))));
        Assert.Equal(100, Assert.Single(document.Pages).Width);
    }

    [Fact]
    public void RepeatedUnsupportedFeaturesProduceOneBoundedNotice()
    {
        var document = DjvuDocument.Open(File(Form("DJVU", [Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), .. Enumerable.Repeat(Chunk("INCL", [1]), 100)])));
        Assert.Equal("IncludedComponentUnsupported", Assert.Single(document.Diagnostics).Code);
    }

    [Fact]
    public void OpenedMetadataCannotBeChangedThroughTheInputOrPageCollection()
    {
        var file = File(Form("DJVU", Chunk("INFO", [0x08, 0x9a, 0x03, 0xc7, 26, 0, 0x2c, 1, 22, 1])));
        var document = DjvuDocument.Open(file);
        Array.Clear(file);
        Assert.Equal(2202, Assert.Single(document.Pages).Width);
        Assert.Throws<NotSupportedException>(() => ((IList<DjvuPageInfo>)document.Pages).Clear());
    }

    private static byte[] Bundle(params byte[][] components)
    {
        var directory = new byte[3 + components.Length * 4];
        directory[0] = 0x81;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(directory.AsSpan(1), (ushort)components.Length);
        var offset = 16 + Chunk("DIRM", directory).Length;
        for (var i = 0; i < components.Length; i++)
        {
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(directory.AsSpan(3 + i * 4), (uint)offset);
            offset += components[i].Length;
        }
        return File(Form("DJVM", [Chunk("DIRM", directory), .. components]));
    }

    private static byte[] File(byte[] form) => [0x41, 0x54, 0x26, 0x54, .. form];
    private static byte[] Form(string type, params byte[][] chunks) => Chunk("FORM", [.. System.Text.Encoding.ASCII.GetBytes(type), .. chunks.SelectMany(c => c)]);
    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
