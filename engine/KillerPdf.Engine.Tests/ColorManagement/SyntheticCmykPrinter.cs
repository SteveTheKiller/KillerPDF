using System.Buffers.Binary;
using System.Text;

namespace KillerPdf.Engine.Tests.ColorManagement;

/// <summary>Builds a portable ICC v2 CMYK printer profile with a known, limited gamut.</summary>
/// <remarks>
/// The press model: ink reflectances (1-c)(1-k), (1-m)(1-k), (1-y)(1-k) are pulled 45% toward their
/// mean (so saturated colors are out of reach) and lifted onto a 4% paper-black floor, then read as
/// linear sRGB in D50. The reverse tables invert that model exactly and clip each channel, so colors
/// the press prints round-trip within table interpolation error. An optional constant cyan offset in
/// the reverse tables makes them imprecise, the way many real CMYK profiles are.
/// </remarks>
internal static class SyntheticCmykPrinter
{
    internal const double Saturation = .55;
    internal const double BlackFloor = .04;

    internal static byte[] Create(double reverseCyanOffset = 0)
    {
        byte[] forward = Forward(17), reverse = Reverse(33, reverseCyanOffset);
        var tags = new List<(string Name, byte[] Data)>
        {
            ("A2B0", forward), ("A2B1", forward), ("A2B2", forward),
            ("B2A0", reverse), ("B2A1", reverse), ("B2A2", reverse),
            ("wtpt", Xyz(.9642, 1, .8249))
        };
        int size = 132 + tags.Count * 12 + tags.Sum(tag => (tag.Data.Length + 3) & ~3);
        var bytes = new byte[size];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, (uint)size);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), 0x02100000);
        "prtr"u8.CopyTo(bytes.AsSpan(12));
        "CMYK"u8.CopyTo(bytes.AsSpan(16));
        "Lab "u8.CopyTo(bytes.AsSpan(20));
        "acsp"u8.CopyTo(bytes.AsSpan(36));
        Xyz(.9642, 1, .8249).AsSpan(8).CopyTo(bytes.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(128), (uint)tags.Count);
        int offset = 132 + tags.Count * 12;
        for (int index = 0; index < tags.Count; index++)
        {
            int entry = 132 + index * 12;
            Encoding.ASCII.GetBytes(tags[index].Name).CopyTo(bytes, entry);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 4), (uint)offset);
            BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(entry + 8), (uint)tags[index].Data.Length);
            tags[index].Data.CopyTo(bytes, offset);
            offset += (tags[index].Data.Length + 3) & ~3;
        }
        return bytes;
    }

    /// <summary>The D50 XYZ the model prints for device CMYK, before any table interpolation.</summary>
    internal static double[] Printed(double c, double m, double y, double k)
    {
        double[] reflectance = [(1 - c) * (1 - k), (1 - m) * (1 - k), (1 - y) * (1 - k)];
        double mean = reflectance.Average();
        double[] rgb = reflectance.Select(value => BlackFloor + (1 - BlackFloor) * (mean + Saturation * (value - mean))).ToArray();
        return ToXyz(rgb);
    }

    private static byte[] Forward(int grid) => Table(4, 3, grid, node =>
    {
        double[] xyz = Printed(node[0], node[1], node[2], node[3]);
        var (l, a, b) = Lab(xyz);
        return [l * 652.80 / 65535, (a + 128) * 256 / 65535, (b + 128) * 256 / 65535];
    });

    private static byte[] Reverse(int grid, double cyanOffset) => Table(3, 4, grid, node =>
    {
        double l = node[0] * 65535 / 652.80, a = node[1] * 65535 / 256 - 128, b = node[2] * 65535 / 256 - 128;
        double[] rgb = FromXyz(FromLab(l, a, b));
        double[] lifted = rgb.Select(value => (value - BlackFloor) / (1 - BlackFloor)).ToArray();
        double mean = lifted.Average();
        double[] reflectance = lifted.Select(value => Math.Clamp(mean + (value - mean) / Saturation, 0, 1)).ToArray();
        return [Math.Clamp(1 - reflectance[0] + cyanOffset, 0, 1), 1 - reflectance[1], 1 - reflectance[2], 0];
    });

    private static byte[] Table(int inputs, int outputs, int grid, Func<double[], double[]> node)
    {
        const int entries = 2;
        int cells = (int)Math.Pow(grid, inputs);
        byte[] data = new byte[52 + 2 * ((inputs + outputs) * entries + cells * outputs)];
        "mft2"u8.CopyTo(data);
        data[8] = (byte)inputs;
        data[9] = (byte)outputs;
        data[10] = (byte)grid;
        for (int row = 0; row < 3; row++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12 + row * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(48), entries);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(50), entries);
        int offset = 52;
        void Write(double value)
        {
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(offset), (ushort)Math.Round(Math.Clamp(value, 0, 1) * 65535));
            offset += 2;
        }
        for (int channel = 0; channel < inputs; channel++) { Write(0); Write(1); }
        var coordinate = new double[inputs];
        for (int cell = 0; cell < cells; cell++)
        {
            int rest = cell;
            for (int channel = inputs - 1; channel >= 0; channel--)
            {
                coordinate[channel] = rest % grid / (double)(grid - 1);
                rest /= grid;
            }
            foreach (double value in node(coordinate)) Write(value);
        }
        for (int channel = 0; channel < outputs; channel++) { Write(0); Write(1); }
        return data;
    }

    private static double[] ToXyz(double[] rgb) =>
    [
        .4360747 * rgb[0] + .3850649 * rgb[1] + .1430804 * rgb[2],
        .2225045 * rgb[0] + .7168786 * rgb[1] + .0606169 * rgb[2],
        .0139322 * rgb[0] + .0971045 * rgb[1] + .7141733 * rgb[2]
    ];

    private static double[] FromXyz(double[] xyz) =>
    [
        3.1338561 * xyz[0] - 1.6168667 * xyz[1] - .4906146 * xyz[2],
        -.9787684 * xyz[0] + 1.9161415 * xyz[1] + .0334540 * xyz[2],
        .0719453 * xyz[0] - .2289914 * xyz[1] + 1.4052427 * xyz[2]
    ];

    internal static (double L, double A, double B) Lab(double[] xyz)
    {
        static double F(double t) => t > 216d / 24389 ? Math.Cbrt(t) : (841d / 108) * t + 4d / 29;
        double x = F(xyz[0] / .9642), y = F(xyz[1]), z = F(xyz[2] / .8249);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));
    }

    private static double[] FromLab(double l, double a, double b)
    {
        static double G(double t) => t > 6d / 29 ? t * t * t : 108d / 841 * (t - 4d / 29);
        double y = (l + 16) / 116, x = y + a / 500, z = y - b / 200;
        return [.9642 * G(x), G(y), .8249 * G(z)];
    }

    private static byte[] Xyz(double x, double y, double z)
    {
        var data = new byte[20];
        "XYZ "u8.CopyTo(data);
        double[] values = [x, y, z];
        for (int i = 0; i < 3; i++) BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(8 + 4 * i), (int)Math.Round(values[i] * 65536));
        return data;
    }
}
