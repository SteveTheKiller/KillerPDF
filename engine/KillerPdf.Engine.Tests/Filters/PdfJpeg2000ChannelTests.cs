using System.Buffers.Binary;
using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpeg2000ChannelTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 1)]
    public void ReadOpacityChannel_UsesTheDeclaredChannel(int channel, int type)
    {
        byte[] data = Header((channel, type, 0));
        Assert.Equal(channel, PdfJpeg2000Decoder.ReadOpacityChannel(data, 4));
    }

    [Theory]
    [InlineData(4, 1, 0)]
    [InlineData(1, 1, 1)]
    public void ReadOpacityChannel_RejectsInvalidOrLocalOpacity(int channel, int type, int association)
    {
        byte[] data = Header((channel, type, association));
        Assert.Throws<PdfFilterException>(() => PdfJpeg2000Decoder.ReadOpacityChannel(data, 4));
    }

    [Fact]
    public void ReadOpacityChannel_RejectsMultipleOpacityChannels()
    {
        byte[] data = Header((0, 1, 0), (3, 1, 0));
        Assert.Throws<PdfFilterException>(() => PdfJpeg2000Decoder.ReadOpacityChannel(data, 4));
    }

    [Fact]
    public void ReadOpacityChannel_RejectsTruncatedDefinitions()
    {
        byte[] data = Header((3, 1, 0));
        data[17] = 2;
        Assert.Throws<PdfFilterException>(() => PdfJpeg2000Decoder.ReadOpacityChannel(data, 4));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(int.MaxValue)]
    public void ReadOpacityChannel_RejectsInvalidBoxLengths(int length)
    {
        byte[] data = Header((3, 1, 0));
        BinaryPrimitives.WriteInt32BigEndian(data, length);
        Assert.Throws<PdfFilterException>(() => PdfJpeg2000Decoder.ReadOpacityChannel(data, 4));
    }

    private static byte[] Header(params (int Channel, int Type, int Association)[] entries)
    {
        var data = new byte[18 + entries.Length * 6];
        BinaryPrimitives.WriteInt32BigEndian(data, data.Length);
        "jp2h"u8.CopyTo(data.AsSpan(4));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8), data.Length - 8);
        "cdef"u8.CopyTo(data.AsSpan(12));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(16), (ushort)entries.Length);
        for (int index = 0; index < entries.Length; index++)
        {
            int offset = 18 + index * 6;
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)entries[index].Channel);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 2), (ushort)entries[index].Type);
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 4), (ushort)entries[index].Association);
        }
        return data;
    }
}
