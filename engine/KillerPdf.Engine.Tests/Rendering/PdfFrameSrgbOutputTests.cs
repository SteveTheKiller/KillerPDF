using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

// The sRGB bytes of a float frame, value by value, from the sRGB transfer function (IEC 61966-2-1) and round-half-up
// quantization. Runs of one value and values that alternate both appear, as on a page of text: the output of each
// pixel depends only on its own value.
public sealed class PdfFrameSrgbOutputTests
{
    [Fact]
    public void LinearFrameEncodesEachChannelBySrgbTransferFunction()
    {
        // (linear value, sRGB byte): 0.5 encodes to 0.7354 (187.5, rounds up to 188); 0.0031308 is the linear segment's
        // end, 12.92 * 0.0031308 = 0.04045 (10.3); 0.2 encodes to 0.4845 (123.5, rounds up to 124); out-of-range clamps.
        (float Linear, byte Srgb)[] values =
            [(1f, 255), (1f, 255), (0f, 0), (0.5f, 188), (0.5f, 188), (1f, 255), (0.0031308f, 10), (0.2f, 124),
             (-0.25f, 0), (1.5f, 255), (0.2f, 124), (0f, 0), (1f, 255)];
        var pixels = new float[values.Length * 4];
        for (int index = 0; index < values.Length; index++)
        {
            // Red, green and blue differ per pixel so a channel mixed up with its neighbor shows.
            pixels[index * 4] = values[index].Linear;
            pixels[index * 4 + 1] = values[(index + 1) % values.Length].Linear;
            pixels[index * 4 + 2] = values[(index + 2) % values.Length].Linear;
            pixels[index * 4 + 3] = index % 2 == 0 ? 1f : 0.5f;
        }
        var frame = new PdfRenderedFrame(values.Length, 1, pixels, PdfFrameColorEncoding.LinearSrgbD65);

        byte[] bgra = frame.ToSrgbBgra32();

        for (int index = 0; index < values.Length; index++)
        {
            Assert.Equal(values[(index + 2) % values.Length].Srgb, bgra[index * 4]);
            Assert.Equal(values[(index + 1) % values.Length].Srgb, bgra[index * 4 + 1]);
            Assert.Equal(values[index].Srgb, bgra[index * 4 + 2]);
            // Alpha 0.5 is 127.5, which rounds up to 128.
            Assert.Equal(index % 2 == 0 ? 255 : 128, bgra[index * 4 + 3]);
        }
    }
}
