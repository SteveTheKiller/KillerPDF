using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfColorProfileVectorTests
{
    // Synthetic encoding fixtures, not complete ICC-conformance profiles.
    // ICC.1:2022 sections 6.3.4, 10.10 (mft2), 10.6 (curv), and 10.31 (XYZ).
    // https://www.color.org/specifications/ICC.1-2022-05.pdf
    // ICC.1:1998-09 sections 6.5.6, 6.5.2 and 6.5.25 define the v2 forms.
    // https://archive.color.org/files/ICC-1_1998-09.PDF
    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void CmykLut16ForwardUsesAllFourDeviceChannelsAndPcsXyzEncoding(int version)
    {
        var profile = PdfColorProfile.Load(Cmyk(version, reverse: false));
        double[] xyz = new double[3];
        profile.ToXyz([0.25, 0.5, 0.75, 0.5], xyz);
        // Affine cube: X=1/2-(C+K)/4, Y=1/2-(M+K)/4, Z=1/2-(Y+K)/4.
        // At this point those fractions are 5/16, 1/4 and 3/16.
        AssertVector([0.3125, 0.25, 0.1875], xyz);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void CmykLut16ReverseUsesB2AAndDecodesPcsBeforeDeviceChannels(int version)
    {
        var profile = PdfColorProfile.Load(Cmyk(version, reverse: true));
        double[] cmyk = new double[4];
        // PCS values are exactly 65535/131072, 65535/65536 and 196605/131072.
        // They represent normalized LUT inputs 1/4, 1/2, 3/4.
        profile.FromXyz([0.49999237060546875, 0.9999847412109375, 1.49997711181640625], cmyk);
        AssertVector([0.25, 0.5, 0.75, 0.25], cmyk);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void CmykConversionUsesDestinationReverseRatherThanDestinationForward(int version)
    {
        var source = PdfColorProfile.Load(Cmyk(version, reverse: false));
        var destination = PdfColorProfile.Load(Cmyk(version, reverse: true));
        double[] result = new double[4];
        source.ConvertTo(destination, [0.25, 0.5, 0.75, 0.5], result);
        // Destination device fractions: 10240/65535, 8192/65535, 6144/65535, 10240/65535.
        AssertVector([0.15625238422217136, 0.1250019073777371,
            0.09375143053330281, 0.15625238422217136], result);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void ForwardOnlyCmykDiagnosesMissingReverseForDirectAndDestinationConversion(int version)
    {
        var profile = PdfColorProfile.Load(Cmyk(version, reverse: false));
        double[] result = new double[4];
        var direct = Assert.Throws<NotSupportedException>(() => profile.FromXyz([0.3, 0.2, 0.1], result));
        Assert.Contains("reverse transform", direct.Message, StringComparison.OrdinalIgnoreCase);
        var conversion = Assert.Throws<NotSupportedException>(() =>
            profile.ConvertTo(profile, [0.25, 0.5, 0.75, 0.5], result));
        Assert.Contains("reverse transform", conversion.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public void RgbGammaTwoAndNonDiagonalMatrixMatchWorkedForwardAndReverseVectors(int version)
    {
        byte[] curve = new byte[14];
        "curv"u8.CopyTo(curve);
        Write32(curve, 8, 1);
        Write16(curve, 12, 512);
        byte[] Column(int x, int y, int z)
        {
            byte[] column = new byte[20];
            "XYZ "u8.CopyTo(column);
            Write32(column, 8, x); Write32(column, 12, y); Write32(column, 16, z);
            return column;
        }
        var profile = PdfColorProfile.Load(Profile(version, "RGB ",
            ("rTRC", curve), ("gTRC", curve), ("bTRC", curve),
            ("rXYZ", Column(32768, 16384, 8192)),
            ("gXYZ", Column(16384, 32768, 8192)),
            ("bXYZ", Column(8192, 8192, 32768))));
        double[] xyz = new double[3];
        profile.ToXyz([0.5, 0.25, 0.75], xyz);
        // Squared channels are 1/4, 1/16, 9/16; XYZ is 27/128, 21/128, 41/128.
        AssertVector([0.2109375, 0.1640625, 0.3203125], xyz);
        double[] rgb = new double[3];
        // Independent literal XYZ input, not a round trip using the preceding result.
        profile.FromXyz([0.2109375, 0.1640625, 0.3203125], rgb);
        AssertVector([0.5, 0.25, 0.75], rgb);
    }

    private static byte[] Cmyk(int version, bool reverse)
    {
        byte[] forward = Lut16(4, 3, cell =>
        {
            int c = (cell >> 3) & 1, m = (cell >> 2) & 1;
            int y = (cell >> 1) & 1, k = cell & 1;
            return [(ushort)(16384 - 8192 * (c + k)),
                (ushort)(16384 - 8192 * (m + k)),
                (ushort)(16384 - 8192 * (y + k))];
        });
        if (!reverse) return Profile(version, "CMYK", ("A2B1", forward));
        byte[] backward = Lut16(3, 4, cell =>
        {
            int x = (cell >> 2) & 1, y = (cell >> 1) & 1, z = cell & 1;
            return [(ushort)(65535 * x), (ushort)(65535 * y),
                (ushort)(65535 * z), (ushort)(65535 * x)];
        });
        return Profile(version, "CMYK", ("A2B1", forward), ("B2A1", backward));
    }

    private static byte[] Lut16(int inputs, int outputs, Func<int, ushort[]> vertex)
    {
        var data = new byte[52 + 4 * inputs + 2 * (1 << inputs) * outputs + 4 * outputs];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs;
        data[9] = (byte)outputs;
        data[10] = 2;
        for (int axis = 0; axis < 3; axis++) Write32(data, 12 + axis * 16, 65536);
        Write16(data, 48, 2);
        Write16(data, 50, 2);
        int position = 52;
        void Append(ushort value) { Write16(data, position, value); position += 2; }
        for (int channel = 0; channel < inputs; channel++) { Append(0); Append(65535); }
        for (int cell = 0; cell < 1 << inputs; cell++)
            foreach (ushort value in vertex(cell)) Append(value);
        for (int channel = 0; channel < outputs; channel++) { Append(0); Append(65535); }
        return data;
    }

    private static byte[] Profile(int version, string space, params (string Name, byte[] Data)[] tags)
    {
        int offset = 132 + 12 * tags.Length;
        var data = new byte[offset + tags.Sum(tag => (tag.Data.Length + 3) & ~3)];
        Write32(data, 0, data.Length);
        data[8] = (byte)version;
        Encoding.ASCII.GetBytes(space).CopyTo(data, 16);
        "prtr"u8.CopyTo(data.AsSpan(12));
        "XYZ "u8.CopyTo(data.AsSpan(20));
        "acsp"u8.CopyTo(data.AsSpan(36));
        Write32(data, 128, tags.Length);
        for (int index = 0; index < tags.Length; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(data, entry);
            Write32(data, entry + 4, offset);
            Write32(data, entry + 8, tags[index].Data.Length);
            tags[index].Data.CopyTo(data, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return data;
    }

    private static void AssertVector(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int channel = 0; channel < expected.Length; channel++)
            Assert.Equal(expected[channel], actual[channel], 12);
    }

    private static void Write16(byte[] data, int offset, ushort value)
        => BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), value);
    private static void Write32(byte[] data, int offset, int value)
        => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
}
