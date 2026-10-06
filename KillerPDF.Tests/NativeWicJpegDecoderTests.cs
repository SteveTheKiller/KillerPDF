using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class NativeWicJpegDecoderTests
{
    [Fact]
    public void TryDecode_EmptyInputDoesNotCreateAStream()
    {
        Assert.False(NativeWicJpegDecoder.TryDecode(ReadOnlyMemory<byte>.Empty, 1, 1, out var samples));
        Assert.Empty(samples);
    }

    [Fact]
    public void TryDecode_SlicedInputPreservesSamplesAfterRejectedSize()
    {
        byte[] jpeg = ConstantYcckJpeg();
        byte[] padded = new byte[jpeg.Length + 37];
        Array.Fill(padded, (byte)0xA5);
        jpeg.CopyTo(padded, 17);
        ReadOnlyMemory<byte> slice = padded.AsMemory(17, jpeg.Length);
        Assert.True(NativeWicJpegDecoder.TryDecode(jpeg, 8, 8, out var expected));
        Assert.Equal(256, expected.Length);
        for (int index = 0; index < 8; index++)
        {
            Assert.False(NativeWicJpegDecoder.TryDecode(slice, 7, 7, out var rejected));
            Assert.Empty(rejected);
            Assert.True(NativeWicJpegDecoder.TryDecode(slice, 8, 8, out var actual));
            Assert.True(expected.AsSpan().SequenceEqual(actual));
        }
        Assert.True(jpeg.AsSpan().SequenceEqual(slice.Span));
        Assert.Equal(0xA5, padded[16]);
        Assert.Equal(0xA5, padded[^1]);
    }

    private static byte[] ConstantYcckJpeg()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xEE, 0, 14 };
        jpeg.AddRange("Adobe"u8.ToArray());
        jpeg.AddRange([0, 100, 0, 0, 0, 0, 2]);
        jpeg.AddRange([0xFF, 0xDB, 0, 67, 0]);
        jpeg.AddRange(Enumerable.Repeat((byte)1, 64));
        jpeg.AddRange([0xFF, 0xC0, 0, 20, 8, 0, 8, 0, 8, 4]);
        for (byte component = 1; component <= 4; component++)
            jpeg.AddRange([component, 0x11, 0]);
        for (int table = 0; table < 2; table++)
        {
            jpeg.AddRange([0xFF, 0xC4, 0, 20, (byte)(table << 4), 1]);
            jpeg.AddRange(new byte[15]);
            jpeg.Add(0);
        }
        jpeg.AddRange([0xFF, 0xDA, 0, 14, 4, 1, 0, 2, 0, 3, 0, 4, 0, 0, 63, 0,
            0, 0xFF, 0xD9]);
        return jpeg.ToArray();
    }
}
