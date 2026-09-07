using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpegHuffmanTests
{
    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(8, false)]
    [InlineData(9, false)]
    [InlineData(16, false)]
    [InlineData(1, true)]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(16, true)]
    public void Decode_PreservesShortAndLongCodesAtMarkers(int codeLength, bool restart)
    {
        byte[] jpeg = GrayJpeg(codeLength, restart);

        byte[] pixels = PdfJpegDecoder.Decode(jpeg, 128);

        Assert.Equal(restart ? 128 : 64, pixels.Length);
        Assert.All(pixels, value => Assert.Equal(128, value));
    }

    [Fact]
    public void Decode_RejectsMarkerInsideLongCode()
    {
        byte[] jpeg = GrayJpeg(16, false);
        // Retain only the first byte of the four-byte entropy payload.
        byte[] truncated = [.. jpeg.AsSpan(0, jpeg.Length - 5), 0xFF, 0xD9];

        Assert.Throws<PdfFilterException>(() => PdfJpegDecoder.Decode(truncated, 64));
    }

    [Fact]
    public void Decode_RejectsUndefinedCode()
    {
        byte[] jpeg = GrayJpeg(8, false);
        byte[] invalid = [.. jpeg.AsSpan(0, jpeg.Length - 4), 0xFF, 0, 0xFF, 0, 0xFF, 0xD9];

        Assert.Throws<PdfFilterException>(() => PdfJpegDecoder.Decode(invalid, 64));
    }

    private static byte[] GrayJpeg(int codeLength, bool restart)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xDB, 0, 67, 0 };
        jpeg.AddRange(Enumerable.Repeat((byte)1, 64));
        jpeg.AddRange([0xFF, 0xC0, 0, 11, 8, 0, 8, 0, (byte)(restart ? 16 : 8), 1, 1, 0x11, 0]);
        for (int table = 0; table < 2; table++)
        {
            int symbols = codeLength == 1 ? 1 : 2;
            jpeg.AddRange([0xFF, 0xC4, 0, (byte)(19 + symbols), (byte)(table << 4)]);
            for (int length = 1; length <= 16; length++)
                jpeg.Add((byte)(length == 1 || length == codeLength ? 1 : 0));
            // The long code follows the unused one-bit symbol in canonical order.
            if (codeLength != 1) jpeg.Add(1);
            jpeg.Add(0);
        }
        if (restart) jpeg.AddRange([0xFF, 0xDD, 0, 4, 0, 1]);
        jpeg.AddRange([0xFF, 0xDA, 0, 8, 1, 1, 0, 0, 63, 0]);
        string code = codeLength == 1 ? "0" : "1" + new string('0', codeLength - 1);
        string payload = code + code;
        payload = payload.PadRight((payload.Length + 7) / 8 * 8, '1');
        for (int block = 0; block < (restart ? 2 : 1); block++)
        {
            if (block != 0) jpeg.AddRange([0xFF, 0xD0]);
            for (int offset = 0; offset < payload.Length; offset += 8)
            {
                byte value = Convert.ToByte(payload.Substring(offset, 8), 2);
                jpeg.Add(value);
                if (value == 0xFF) jpeg.Add(0);
            }
        }
        jpeg.AddRange([0xFF, 0xD9]);
        return [.. jpeg];
    }
}
