using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class WicRgbJpegDecoderTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void TryDecode_ConstantJfifPreservesRgbOrderAndReducedDimensions(int reduction)
    {
        byte[] jpeg = ConstantRgbJpeg();
        byte[] padded = new byte[jpeg.Length + 19];
        Array.Fill(padded, (byte)0xA5);
        jpeg.CopyTo(padded, 7);
        var input = padded.AsMemory(7, jpeg.Length);
        int width = 1024 / reduction;
        int length = width * width * 3;
        Assert.False(WicJpegDecoder.Instance.TryDecode(input, length - 1, reduction, null, out _));
        Assert.True(WicJpegDecoder.Instance.TryDecode(input, length, reduction, null, out var image));
        Assert.Equal(width, image.Width);
        Assert.Equal(width, image.Height);
        Assert.Equal(3, image.Components);
        Assert.Equal(length, image.Samples.Length);
        for (int offset = 0; offset < image.Samples.Length; offset += 3)
        {
            Assert.InRange(image.Samples[offset], 216, 220);
            Assert.InRange(image.Samples[offset + 1], 102, 106);
            Assert.InRange(image.Samples[offset + 2], 13, 17);
        }
        Assert.True(jpeg.AsSpan().SequenceEqual(input.Span));
        Assert.Equal(0xA5, padded[6]);
        Assert.Equal(0xA5, padded[^1]);
    }

    [Fact]
    public void TryDecode_UnsupportedTransformAndProgressiveFramesUseFallback()
    {
        byte[] jpeg = ConstantRgbJpeg();
        Assert.False(WicJpegDecoder.Instance.TryDecode(jpeg, int.MaxValue, 1, 0, out _));
        Assert.False(WicJpegDecoder.Instance.TryDecode(jpeg, int.MaxValue, 1, 2, out _));
        Assert.False(WicJpegDecoder.Instance.TryDecode(jpeg, int.MaxValue, 3, null, out _));
        int frame = Array.IndexOf(jpeg, (byte)0xC0);
        Assert.True(frame > 0 && jpeg[frame - 1] == 0xFF);
        jpeg[frame] = 0xC2;
        Assert.False(WicJpegDecoder.Instance.TryDecode(jpeg, int.MaxValue, 1, null, out _));
    }

    private static byte[] ConstantRgbJpeg()
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16 };
        jpeg.AddRange("JFIF\0"u8.ToArray());
        jpeg.AddRange([1, 2, 0, 0, 1, 0, 1, 0, 0]);
        jpeg.AddRange([0xFF, 0xE2, 0x20, 2]);
        jpeg.AddRange(new byte[8192]);
        jpeg.AddRange([0xFF, 0xDB, 0, 67, 0]);
        jpeg.AddRange(Enumerable.Repeat((byte)1, 64));
        jpeg.AddRange([0xFF, 0xC0, 0, 17, 8, 4, 0, 4, 0, 3]);
        for (byte component = 1; component <= 3; component++)
            jpeg.AddRange([component, 0x11, 0]);
        jpeg.AddRange([0xFF, 0xC4, 0, 21, 0, 1, 1]);
        jpeg.AddRange(new byte[14]);
        jpeg.AddRange([0, 10]);
        jpeg.AddRange([0xFF, 0xC4, 0, 20, 0x10, 1]);
        jpeg.AddRange(new byte[15]);
        jpeg.Add(0);
        jpeg.AddRange([0xFF, 0xDA, 0, 12, 3, 1, 0, 2, 0, 3, 0, 0, 63, 0]);
        string initial = "00" + "1001111111110" + "1010000000000";
        string bits = initial + new string('0', (128 * 128 - 1) * 6);
        bits = bits.PadRight((bits.Length + 7) / 8 * 8, '1');
        for (int offset = 0; offset < bits.Length; offset += 8)
        {
            byte value = Convert.ToByte(bits.Substring(offset, 8), 2);
            jpeg.Add(value);
            if (value == 255) jpeg.Add(0);
        }
        jpeg.AddRange([0xFF, 0xD9]);
        return jpeg.ToArray();
    }
}
