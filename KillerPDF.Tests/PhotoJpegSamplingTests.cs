using System.Buffers.Binary;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PhotoJpegSamplingTests
{
    [Fact]
    public void CanceledPhotoEncodingReturnsNoJpeg()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            PdfImageJpeg.EncodePhoto(new byte[32 * 32 * 3], 32, 32, 3, 100, canceled.Token));
    }

    [Fact]
    public void PhotoJpegKeepsEveryColorSampleAtFullResolution()
    {
        byte[] samples = new byte[32 * 16 * 3];
        for (int pixel = 0; pixel < 32 * 16; pixel++)
            samples[pixel * 3 + (pixel % 2 == 0 ? 0 : 2)] = 255;

        byte[] jpeg = PdfImageJpeg.EncodePhoto(samples, 32, 16, 3, 100);

        int offset = 2;
        while (offset + 4 <= jpeg.Length)
        {
            Assert.Equal(0xff, jpeg[offset]);
            byte marker = jpeg[offset + 1];
            int length = BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(offset + 2));
            if (marker == 0xc0)
            {
                Assert.Equal(3, jpeg[offset + 9]);
                for (int channel = 0; channel < 3; channel++)
                    Assert.Equal(0x11, jpeg[offset + 11 + channel * 3]);
                return;
            }
            offset += length + 2;
        }
        Assert.Fail("The photo encoder did not write a baseline JPEG frame.");
    }
}
