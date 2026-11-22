using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfPrinterColorConversionTests
{
    [Theory]
    [InlineData(PdfRenderingIntent.Perceptual)]
    [InlineData(PdfRenderingIntent.RelativeColorimetric)]
    [InlineData(PdfRenderingIntent.Saturation)]
    [InlineData(PdfRenderingIntent.AbsoluteColorimetric)]
    public void APrinterWithSrgbPrimariesAndCurveKeepsTheBytes(PdfRenderingIntent intent)
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: true), intent, blackPointCompensation: true);
        byte[] pixels = Pixels((255, 255, 255), (0, 0, 0), (200, 30, 90), (12, 160, 240), (128, 128, 128));
        byte[] original = (byte[])pixels.Clone();

        conversion.ConvertSrgbBgra32(pixels);

        for (int index = 0; index < pixels.Length; index++)
            Assert.InRange(pixels[index], original[index] - 1, original[index] + 1);
    }

    [Fact]
    public void ALinearPrinterReceivesLinearDeviceValues()
    {
        // The identity TRC makes device RGB the linear sRGB channels: sRGB 128 is linear 0.21586, byte 55.
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: false),
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        byte[] pixels = Pixels((128, 128, 128), (255, 255, 255), (0, 0, 0));

        conversion.ConvertSrgbBgra32(pixels);

        Assert.Equal(new byte[] { 55, 55, 55, 255, 255, 255, 255, 255, 0, 0, 0, 255 }, pixels);
    }

    [Fact]
    public void TheProofReturnsWhatThePrinterReproducesAsSrgb()
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: false),
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: false);
        byte[] pixels = Pixels((128, 128, 128), (255, 255, 255), (0, 0, 0), (200, 30, 90));
        byte[] original = (byte[])pixels.Clone();

        conversion.ProofSrgbBgra32(pixels);

        // A linear printer with sRGB primaries reproduces sRGB exactly; only byte rounding of the
        // device values separates the proof from the source.
        for (int index = 0; index < pixels.Length; index++)
            Assert.InRange(pixels[index], original[index] - 2, original[index] + 2);
    }

    [Fact]
    public void AlphaIsKept()
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: false),
            PdfRenderingIntent.Perceptual, blackPointCompensation: true);
        byte[] pixels = [128, 128, 128, 77];

        conversion.ConvertSrgbBgra32(pixels);

        Assert.Equal(77, pixels[3]);
    }

    [Fact]
    public void RepeatedColorsConvertTheSameAsSingleOnes()
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: false),
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);
        var colors = new List<(byte, byte, byte)>();
        for (int index = 0; index < 5000; index++)
            colors.Add(((byte)(index * 7), (byte)(index * 13 >> 2), (byte)(index * 31 >> 3)));
        byte[] batch = Pixels([.. colors, .. colors]);

        conversion.ConvertSrgbBgra32(batch);

        for (int index = 0; index < colors.Count; index++)
        {
            byte[] single = Pixels(colors[index]);
            conversion.ConvertSrgbBgra32(single);
            Assert.Equal(single, batch.AsSpan(index * 4, 4).ToArray());
            Assert.Equal(single, batch.AsSpan((colors.Count + index) * 4, 4).ToArray());
        }
    }

    // The conversion interpolates a sampled grid; it stays within a code value of evaluating the
    // printer chain for every pixel, even on a curve as far from sRGB as linear.
    [Fact]
    public void TheInterpolatedConversionMatchesTheExactChain()
    {
        var printer = RgbPrinter(srgbCurve: false);
        var conversion = new PdfPrinterColorConversion(printer, PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);
        var chain = new KillerPdf.Engine.Rendering.PdfProofPixelChain(printer, new PdfProofOptions
        {
            RenderingIntent = PdfRenderingIntent.RelativeColorimetric, BlackPointCompensation = true, SimulateBlack = true
        });
        var random = new Random(520);
        var colors = new (byte, byte, byte)[4000];
        for (int index = 0; index < colors.Length; index++)
            colors[index] = ((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
        byte[] pixels = Pixels(colors);

        conversion.ConvertSrgbBgra32(pixels);

        int worst = 0;
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[3];
        for (int index = 0; index < colors.Length; index++)
        {
            var (red, green, blue) = colors[index];
            double r = Linear(red), g = Linear(green), b = Linear(blue);
            xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
            xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
            xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
            chain.ToDevice(xyz, Math.Min(red, Math.Min(green, blue)) / 255d, device);
            for (int channel = 0; channel < 3; channel++)
            {
                int exact = (int)Math.Round(Math.Clamp(device[channel], 0, 1) * 255);
                worst = Math.Max(worst, Math.Abs(exact - pixels[index * 4 + 2 - channel]));
            }
        }
        Assert.InRange(worst, 0, 1);

        static double Linear(byte value)
        {
            double unit = value / 255d;
            return unit <= .04045 ? unit / 12.92 : Math.Pow((unit + .055) / 1.055, 2.4);
        }
    }

    [Theory]
    [InlineData("mntr", "RGB ")]
    [InlineData("prtr", "GRAY")]
    public void OnlyRgbOutputProfilesCanDrivePrinting(string profileClass, string space)
    {
        var profile = space == "GRAY" ? GrayPrinter() : RgbPrinter(srgbCurve: true, profileClass);

        Assert.False(PdfPrinterColorConversion.Supports(profile, PdfRenderingIntent.RelativeColorimetric));
        Assert.Throws<NotSupportedException>(() => new PdfPrinterColorConversion(profile,
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true));
    }

    [Fact]
    public void AbsoluteColorimetricNeedsAMediaWhitePoint()
    {
        var profile = RgbPrinter(srgbCurve: true, mediaWhite: false);

        Assert.True(PdfPrinterColorConversion.Supports(profile, PdfRenderingIntent.RelativeColorimetric));
        Assert.False(PdfPrinterColorConversion.Supports(profile, PdfRenderingIntent.AbsoluteColorimetric));
    }

    [Fact]
    public void AbsoluteColorimetricNeverCompensatesBlack()
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: true),
            PdfRenderingIntent.AbsoluteColorimetric, blackPointCompensation: true);

        Assert.False(conversion.BlackPointCompensation);
    }

    [Fact]
    public void BytesThatAreNotWholePixelsAreRejected()
    {
        var conversion = new PdfPrinterColorConversion(RgbPrinter(srgbCurve: true),
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);

        Assert.Throws<ArgumentException>(() => conversion.ConvertSrgbBgra32(new byte[6]));
    }

    private static byte[] Pixels(params (byte Red, byte Green, byte Blue)[] colors)
    {
        var pixels = new byte[colors.Length * 4];
        for (int index = 0; index < colors.Length; index++)
        {
            pixels[index * 4] = colors[index].Blue;
            pixels[index * 4 + 1] = colors[index].Green;
            pixels[index * 4 + 2] = colors[index].Red;
            pixels[index * 4 + 3] = 255;
        }
        return pixels;
    }

    // A matrix/TRC output profile with the D50-adapted sRGB colorants (ICC.1:2022 F.3) and either a
    // sampled sRGB curve or the identity curve. Minimal encoding fixture, not a conformance specimen.
    private static PdfColorProfile RgbPrinter(bool srgbCurve, string profileClass = "prtr", bool mediaWhite = true)
    {
        double[][] colorants =
        [
            [.4360747, .2225045, .0139322],
            [.3850649, .7168786, .0971045],
            [.1430804, .0606169, .7141733],
            [.9642, 1, .8249]
        ];
        const int Samples = 4096;
        int curveLength = 12 + (srgbCurve ? Samples * 2 : 0);
        string[] xyzTags = mediaWhite ? ["rXYZ", "gXYZ", "bXYZ", "wtpt"] : ["rXYZ", "gXYZ", "bXYZ"];
        int tagCount = xyzTags.Length + 3;
        int offset = 132 + tagCount * 12;
        int size = offset + xyzTags.Length * 20 + curveLength;
        byte[] data = new byte[size];
        Write32(data, 0, size);
        data[8] = 4;
        Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12);
        "RGB "u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tagCount);
        int entry = 132;
        for (int index = 0; index < xyzTags.Length; index++, entry += 12, offset += 20)
        {
            Encoding.ASCII.GetBytes(xyzTags[index]).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, 20);
            "XYZ "u8.CopyTo(data.AsSpan(offset));
            for (int channel = 0; channel < 3; channel++)
                Write32(data, offset + 8 + channel * 4, (int)Math.Round(colorants[index][channel] * 65536));
        }
        foreach (string curve in new[] { "rTRC", "gTRC", "bTRC" })
        {
            Encoding.ASCII.GetBytes(curve).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, curveLength);
            entry += 12;
        }
        "curv"u8.CopyTo(data.AsSpan(offset));
        if (srgbCurve)
        {
            Write32(data, offset + 8, Samples);
            for (int sample = 0; sample < Samples; sample++)
            {
                double value = sample / (Samples - 1d);
                double linear = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset + 12 + sample * 2),
                    (ushort)Math.Round(linear * 65535));
            }
        }
        return PdfColorProfile.Load(data);
    }

    private static PdfColorProfile GrayPrinter()
    {
        byte[] data = new byte[156];
        Write32(data, 0, 156);
        data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        Write32(data, 136, 144);
        Write32(data, 140, 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return PdfColorProfile.Load(data);
    }

    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
