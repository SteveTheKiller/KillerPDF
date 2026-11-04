using System.Buffers.Binary;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorProfileTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(.5)]
    [InlineData(1)]
    public void LargeParametricCurveValuesRetainRequiredOutputClipping(double input)
    {
        byte[] data = IdentityGray();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(140), 24);
        "para"u8.CopyTo(data.AsSpan(144));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(152), 1);
        for (int offset = 156; offset <= 164; offset += 4)
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), 32767 << 16);

        // Type1 evaluates (32767*x+32767)^32767 throughout [0,1].
        // ICC.1:2022 section10.18 clips function values to [0,1].
        // https://www.color.org/specifications/ICC.1-2022-05.pdf
        var profile = PdfColorProfile.Load(data);
        double[] xyz = new double[3];
        profile.ToXyz([input], xyz);
        Assert.Equal(.9642, xyz[0], 10);
        Assert.Equal(1, xyz[1], 10);
        Assert.Equal(.8249, xyz[2], 10);
    }

    [Fact]
    public void IdentityGrayUsesRelativeD50ConnectionValues()
    {
        var profile = PdfColorProfile.Load(IdentityGray());
        double[] xyz = new double[3];
        profile.ToXyz([0.5], xyz);
        Assert.Equal(0.4821, xyz[0], 10);
        Assert.Equal(0.5, xyz[1], 10);
        Assert.Equal(0.41245, xyz[2], 10);
    }

    [Fact]
    public void ConnectionLuminanceConvertsBackToIdentityGray()
    {
        var profile = PdfColorProfile.Load(IdentityGray());
        double[] device = new double[1];
        profile.FromXyz([0.24105, 0.25, 0.206225], device);
        Assert.Equal(0.25, device[0], 10);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(5)]
    public void UnsupportedIccVersionIsDiagnosed(int version)
    {
        byte[] data = IdentityGray();
        data[8] = (byte)version;
        Assert.Throws<NotSupportedException>(() => PdfColorProfile.Load(data));
    }

    [Fact]
    public void OversizedInputIsRejectedBeforeCopyingIt()
    {
        byte[] data = new byte[32 * 1024 * 1024 + 1];
        IdentityGray().CopyTo(data, 0);
        long before = GC.GetAllocatedBytesForCurrentThread();
        Assert.Throws<FormatException>(() => PdfColorProfile.Load(data));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 1024 * 1024, $"Rejected input allocated {allocated} bytes.");
    }

    [Theory]
    [InlineData("link")]
    [InlineData("abst")]
    [InlineData("nmcl")]
    [InlineData("????")]
    public void UnsupportedProfileClassIsDiagnosed(string profileClass)
    {
        byte[] data = IdentityGray();
        System.Text.Encoding.ASCII.GetBytes(profileClass).CopyTo(data, 12);
        Assert.Throws<NotSupportedException>(() => PdfColorProfile.Load(data));
    }

    [Theory]
    [InlineData(PdfRenderingIntent.AbsoluteColorimetric, 0.375, 0.4375, 0.25)]
    [InlineData(PdfRenderingIntent.RelativeColorimetric, 0.4821, 0.5, 0.41245)]
    [InlineData(PdfRenderingIntent.Perceptual, 0.4821, 0.5, 0.41245)]
    [InlineData(PdfRenderingIntent.Saturation, 0.4821, 0.5, 0.41245)]
    public void IntentUsesAbsoluteOrRelativeMediaWhite(PdfRenderingIntent intent, double x, double y, double z)
    {
        var data = new byte[188];
        IdentityGray().AsSpan(0, 132).CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data, 188);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 2);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(136), 156);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(140), 12);
        "wtpt"u8.CopyTo(data.AsSpan(144));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(148), 168);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(152), 20);
        "curv"u8.CopyTo(data.AsSpan(156));
        "XYZ "u8.CopyTo(data.AsSpan(168));
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(176), 49152);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(180), 57344);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(184), 32768);
        var profile = PdfColorProfile.Load(data);
        double[] xyz = new double[3];
        profile.ToXyz([0.5], xyz, intent);
        Assert.Equal(x, xyz[0], 10);
        Assert.Equal(y, xyz[1], 10);
        Assert.Equal(z, xyz[2], 10);
        double[] gray = new double[1];
        profile.FromXyz(xyz, gray, intent);
        Assert.Equal(0.5, gray[0], 10);
    }

    [Fact]
    public void GrayToRgbLinksThroughD50WithoutByteQuantization()
    {
        var source = PdfColorProfile.Load(IdentityGray());
        var destination = PdfColorProfile.Load(IdentityRgb());
        double[] result = new double[3];
        source.ConvertTo(destination, [0.5], result);
        Assert.Equal(0.4821, result[0], 10);
        Assert.Equal(0.5, result[1], 10);
        Assert.Equal(0.41245, result[2], 10);
    }

    [Fact]
    public void BlackPointCompensationCapsSourceBlackLightnessAtFifty()
    {
        byte[] data = IdentityGray();
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(140), 16);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(152), 2);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(156), 16384);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(158), 65535);
        var source = PdfColorProfile.Load(data);
        var destination = PdfColorProfile.Load(IdentityGray());
        double[] result = new double[1];
        source.ConvertTo(destination, [0.5], result);
        Assert.Equal(0.6250019073777371, result[0], 10);
        source.ConvertTo(destination, [0.5], result, blackPointCompensation: true);
        // ICC-hosted ISO/CD 18619 section 4.2.3 caps source L* at 50.
        // The capped luminance is (33/58)^3. Literals use 60-digit decimal arithmetic.
        Assert.Equal(0.5403384460643005404682286110, result[0], 10);
        source.ConvertTo(destination, [0], result, blackPointCompensation: true);
        Assert.Equal(0.0806768921286010809364572220, result[0], 10);
    }

    static byte[] IdentityRgb()
    {
        var data = new byte[300];
        IdentityGray().AsSpan(0, 132).CopyTo(data);
        BinaryPrimitives.WriteUInt32BigEndian(data, 300);
        "RGB "u8.CopyTo(data.AsSpan(16));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 6);
        string[] signatures = ["rTRC", "gTRC", "bTRC", "rXYZ", "gXYZ", "bXYZ"];
        for (int index = 0; index < 6; index++)
        {
            int entry = 132 + index * 12;
            System.Text.Encoding.ASCII.GetBytes(signatures[index]).CopyTo(data, entry);
            int offset = index < 3 ? 204 : 216 + (index - 3) * 20;
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(entry + 8), index < 3 ? 12u : 20u);
            if (index < 3) "curv"u8.CopyTo(data.AsSpan(offset));
            else
            {
                "XYZ "u8.CopyTo(data.AsSpan(offset));
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset + 8 + (index - 3) * 4), 65536);
            }
        }
        return data;
    }

    static byte[] IdentityGray()
    {
        var data = new byte[168];
        BinaryPrimitives.WriteUInt32BigEndian(data, 168);
        data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12));
        "GRAY"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(128), 1);
        "kTRC"u8.CopyTo(data.AsSpan(132));
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(136), 144);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(140), 12);
        "curv"u8.CopyTo(data.AsSpan(144));
        return data;
    }
}
