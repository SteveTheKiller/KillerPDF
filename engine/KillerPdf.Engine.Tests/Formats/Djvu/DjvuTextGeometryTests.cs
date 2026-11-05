using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuTextGeometryTests
{
    [Fact]
    public void ReadsLiteralMultibyteTextAndUnrotatedGeometry()
    {
        byte[] zone = [6, 0x80, 12, 0x80, 34, 0x80, 56, 0x80, 78, 0x80, 1, 0, 0, 6, 0, 0, 0];
        var page = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 9, 65, 0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, 10, 0, 1, .. zone]))).ReadTextPage(0);
        Assert.Equal("A\u00e9\U0001f600\n\0", page.Text);
        var actual = Assert.Single(page.Zones);
        Assert.Equal(DjvuTextZoneType.Word, actual.Type);
        Assert.Equal((-1, 1, 3, 12L, 34L, 56L, 78L),
            (actual.ParentIndex, actual.Start, actual.Length, actual.Left, actual.Bottom, actual.Width, actual.Height));
    }

    [Fact]
    public void RetainsLegacyHierarchyGlobalRangesAndBothSiblingCoordinateRules()
    {
        byte[] zones = [
            .. Zone(7, 100, 200, 100, 100, 1, 1, 3),
            .. Zone(6, 10, 20, 10, 10, -1, 2, 1),
            .. Zone(1, 2, 3, 4, 5, 0, 1),
            .. Zone(5, -3, 5, 20, 15, -1, 2),
            .. Zone(6, 2, -4, 5, 6, 0, 0),
            .. Zone(1, 7, 8, 9, 10, 0, 3)];
        var page = DjvuDocument.Open(Page(Chunk("TXTa", [0, 0, 3, 65, 66, 67, 1, .. zones]))).ReadTextPage(0);
        Assert.Equal("ABC", page.Text);
        Assert.Equal(new[] {
            (7, -1, 1, 1, 100L, 200L, 100L, 100L),
            (6, 0, 0, 2, 110L, 270L, 10L, 10L),
            (1, 1, 0, 1, 112L, 272L, 4L, 5L),
            (5, 0, 1, 2, 107L, 250L, 20L, 15L),
            (6, 0, 3, 0, 129L, 246L, 5L, 6L),
            (1, -1, 0, 3, 7L, 8L, 9L, 10L)
        }, page.Zones.Select(z => ((int)z.Type, z.ParentIndex, z.Start, z.Length, z.Left, z.Bottom, z.Width, z.Height)));
    }

    [Fact]
    public void ReturnedTextAndZonesCannotBeChangedByTheInputOrCollectionConsumer()
    {
        var input = Page(Chunk("TXTa", [0, 0, 1, 65, 1, .. Zone(7, 1, 2, 3, 4, 0, 1)]));
        var document = DjvuDocument.Open(input);
        var page = document.ReadTextPage(0);
        Array.Clear(input);
        var collection = Assert.IsAssignableFrom<IList<DjvuTextZone>>(page.Zones);
        Assert.True(collection.IsReadOnly);
        Assert.Throws<NotSupportedException>(() => collection.Clear());
        Assert.Throws<NotSupportedException>(() => collection[0] = page.Zones[0]);
        Assert.Equal("A", page.Text);
        Assert.Single(page.Zones);
        Assert.Equal(1, page.Zones[0].Left);
        Assert.Equal("A", document.ReadText(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingAndExplicitlyEmptyTextHaveNoInventedZones(bool explicitEmpty)
    {
        var document = DjvuDocument.Open(explicitEmpty ? Page(Chunk("TXTa", [0, 0, 0, 1])) : Page());
        var page = document.ReadTextPage(0);
        Assert.Equal(string.Empty, page.Text);
        Assert.Empty(page.Zones);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlainAndIndependentCompressedTextPreserveUnicodeWithoutInventingGeometry(bool compressed)
    {
        var chunk = compressed
            ? Chunk("TXTz", Convert.FromHexString("FFFFEBDFFC5042E9EBF0318DC264BA9731BDA676E26E7DE136"))
            : Chunk("TXTa", [0, 0, 15, 67, 97, 102, 0xc3, 0xa9, 32, 0xd7, 0xa9, 0xd7, 0x9c, 0xd7, 0x95, 0xd7, 0x9d, 10, 1]);
        var document = DjvuDocument.Open(Page(chunk));
        var page = document.ReadTextPage(0);
        Assert.Equal("Caf\u00e9 \u05e9\u05dc\u05d5\u05dd\n", page.Text);
        Assert.Equal(page.Text, document.ReadText(0));
        Assert.Empty(page.Zones);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(3, 3)]
    [InlineData(2, 1)]
    [InlineData(4, 0)]
    [InlineData(5, 1)]
    public void BothTextApisRejectRangesInsideUtf8Scalars(int start, int length)
    {
        var document = DjvuDocument.Open(Page(Chunk("TXTa",
            [0, 0, 6, 0xc3, 0xa9, 0xf0, 0x9f, 0x98, 0x80, 1, .. Zone(6, 1, 2, 3, 4, start, length)])));
        var geometryError = Assert.Throws<InvalidDataException>(() => document.ReadTextPage(0));
        var textError = Assert.Throws<InvalidDataException>(() => document.ReadText(0));
        Assert.Equal("TXTa zone text range splits a UTF8 scalar.", geometryError.Message);
        Assert.Equal(geometryError.Message, textError.Message);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1)]
    public void RejectsPageIndexesAndObservesCancellationBeforePageSelection(int index)
    {
        var document = DjvuDocument.Open(Page());
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ReadTextPage(index));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => document.ReadTextPage(index, cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => document.ReadTextPage(0, cancellation.Token));
    }

    private static byte[] Zone(byte type, int x, int y, int width, int height, int start, int length, int children = 0)
    {
        var result = new byte[17];
        result[0] = type;
        int[] values = [x, y, width, height, start];
        for (var i = 0; i < values.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1 + 2 * i), checked((ushort)(values[i] + 0x8000)));
        result[11] = (byte)(length >> 16); result[12] = (byte)(length >> 8); result[13] = (byte)length;
        result[14] = (byte)(children >> 16); result[15] = (byte)(children >> 8); result[16] = (byte)children;
        return result;
    }

    private static byte[] Page(params byte[][] chunks) => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), .. chunks.SelectMany(c => c)])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
