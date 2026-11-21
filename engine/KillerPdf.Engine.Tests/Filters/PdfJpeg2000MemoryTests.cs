using CoreJ2K;
using CoreJ2K.Configuration;
using CoreJ2K.Util;
using KillerPdf.Engine.Filters;
using Xunit;

namespace KillerPdf.Engine.Tests.Filters;

public sealed class PdfJpeg2000MemoryTests
{
    [Fact]
    public void DecodingAllocatesLittleBeyondThePackedSamples()
    {
        const int width = 1024, height = 768, components = 3;
        int[][] samples = [.. Enumerable.Range(0, components).Select(component =>
            Enumerable.Range(0, width * height).Select(index =>
                ((index % width * 7 + index / width * 3 + component * 41) & 255) - 128).ToArray())];
        var source = new InterleavedImageSource(width, height, components, 8, new bool[components], samples);
        byte[] encoded = J2kImage.ToBytes(source, new J2KEncoderConfiguration().WithLossless().ToParameterList());
        PdfJpeg2000Decoder.DecodeImage(encoded, 4_000_000, -1);

        long before = GC.GetAllocatedBytesForCurrentThread();
        Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(encoded, 4_000_000, -1);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(width * height * components, decoded.Samples.Length);
        Assert.True(allocated < decoded.Samples.Length + encoded.Length + 1024 * 1024,
            $"Decoding allocated {allocated:N0} bytes for {decoded.Samples.Length:N0} bytes of samples.");
        for (int pixel = 0; pixel < width * height; pixel += 4099)
        for (int component = 0; component < components; component++)
            Assert.Equal(samples[component][pixel] + 128, decoded.Samples[pixel * components + component]);
    }
}
