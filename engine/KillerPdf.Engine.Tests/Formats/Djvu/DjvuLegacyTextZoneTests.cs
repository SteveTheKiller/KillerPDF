using KillerPdf.Engine.Formats.Djvu;
using Xunit;

namespace KillerPdf.Engine.Tests.Formats.Djvu;

public sealed class DjvuLegacyTextZoneTests
{
    [Fact]
    public void ReadsLegacyPageZonesNestedInsideAPageZone()
    {
        byte[] parent = [1, 0x80, 0, 0x80, 0, 0x80, 100, 0x80, 100, 0x80, 0, 0, 0, 3, 0, 0, 1];
        byte[] child = [1, 0x80, 0, 0x80, 0, 0x80, 20, 0x80, 20, 0x80, 0, 0, 0, 3, 0, 0, 0];
        var input = Page([0, 0, 3, 65, 66, 67, 1, .. parent, .. child]);
        var original = input.ToArray();

        Assert.Equal("ABC", DjvuDocument.Open(input).ReadText(0));
        Assert.Equal(original, input);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    public void RejectsZoneTextRangesOutsideThePage(int start)
    {
        var zone = Zone(1, start: start);
        var document = DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. zone]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(10, 0)]
    [InlineData(10, -1)]
    public void RejectsEmptyOrNegativeZoneRectangles(int width, int height)
    {
        var zone = Zone(1, width: width, height: height);
        var document = DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. zone]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RejectsAZoneAtTheSixtyFifthStructuralLevel()
    {
        var zones = Enumerable.Range(0, 65).SelectMany(i => Zone(1, children: i == 64 ? 0 : 1));
        var document = DjvuDocument.Open(Page([0, 0, 1, 65, 1, .. zones]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void AcceptsExactlySixtyFourStructuralLevelsWithRepeatedLabels()
    {
        var zones = Enumerable.Range(0, 64).SelectMany(i => Zone(1, children: i == 63 ? 0 : 1));
        Assert.Equal("A", DjvuDocument.Open(Page([0, 0, 1, 65, 1, .. zones])).ReadText(0));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void AcceptsEveryZoneLabelBelowACharacterAndAfterASibling(byte type)
    {
        var parent = Zone(7, x: 100, y: 200, width: 100, height: 100, length: 3, children: 2);
        var first = Zone(7, x: 10, y: 20, length: 1);
        var second = Zone(type, x: -3, y: 5, width: 20, height: 15, length: 2);
        Assert.Equal("ABC", DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. parent, .. first, .. second])).ReadText(0));
    }

    [Fact]
    public void AcceptsGloballyValidChildTextOutsideItsParentsTextRange()
    {
        var parent = Zone(7, start: 1, length: 1, children: 2);
        var first = Zone(1, start: -1, length: 2);
        var second = Zone(1, start: -1, length: 2);
        Assert.Equal("ABC", DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. parent, .. first, .. second])).ReadText(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RejectsOutOfBoundsResolvedParentAndPreviousSiblingTextOffsets(bool sibling)
    {
        var parent = Zone(1, start: 1, length: 1, children: sibling ? 2 : 1);
        var first = Zone(1, start: sibling ? 0 : -2);
        var second = sibling ? Zone(1, start: 1) : [];
        var document = DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. parent, .. first, .. second]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RestoresThePreviousSiblingAfterItsDescendantsAndResetsAtEachRoot()
    {
        var parent = Zone(1, length: 3, children: 2);
        var first = Zone(1, children: 1);
        var descendant = Zone(1, start: 2, length: 0);
        var second = Zone(1, start: 1);
        var tree = parent.Concat(first).Concat(descendant).Concat(second).ToArray();
        Assert.Equal("ABC", DjvuDocument.Open(Page([0, 0, 3, 65, 66, 67, 1, .. tree, .. tree])).ReadText(0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    [InlineData(255)]
    public void RejectsLabelsOutsideTheSevenDocumentedTypes(byte type)
    {
        var document = DjvuDocument.Open(Page([0, 0, 1, 65, 1, .. Zone(type)]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    [Fact]
    public void RejectsMissingChildrenEvenAfterACompleteDescendantTree()
    {
        var parent = Zone(1, children: 2);
        var first = Zone(1, children: 1);
        var descendant = Zone(1);
        var document = DjvuDocument.Open(Page([0, 0, 1, 65, 1, .. parent, .. first, .. descendant]));
        Assert.Throws<InvalidDataException>(() => document.ReadText(0));
    }

    private static byte[] Zone(byte type, int x = 0, int y = 0, int width = 10, int height = 10, int start = 0, int length = 1, int children = 0)
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

    private static byte[] Page(byte[] text) => [.. "AT&T"u8, .. Chunk("FORM", [.. "DJVU"u8, .. Chunk("INFO", [0, 100, 0, 200, 26, 0, 0x2c, 1, 22, 1]), .. Chunk("TXTa", text)])];

    private static byte[] Chunk(string id, byte[] payload)
    {
        var length = payload.Length;
        return [.. System.Text.Encoding.ASCII.GetBytes(id), (byte)(length >> 24), (byte)(length >> 16), (byte)(length >> 8), (byte)length, .. payload, .. (length % 2 == 1 ? new byte[1] : [])];
    }
}
