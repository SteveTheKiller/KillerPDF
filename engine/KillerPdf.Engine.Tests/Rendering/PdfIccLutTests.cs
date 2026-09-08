using System.Buffers.Binary;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccLutTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transform_InterpolatesAllFourInputDimensions(bool sixteen)
    {
        var lut = new PdfIccLut(Table(4, sixteen));
        double[] result = new double[3];
        lut.Transform([0.25, 0.5, 0.75, 0.8], result);
        Assert.Equal(0.2, result[0], 10);
        Assert.Equal(0.5, result[1], 10);
        Assert.Equal(0.75, result[2], 10);
        lut.Transform([-1, 2, 1, 1], result);
        Assert.Equal(new double[] { 0, 1, 1 }, result);
    }

    [Fact]
    public void Transform_AppliesXyzMatrixBeforeInputCurves()
    {
        byte[] data = Table(3, true);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(12), 32768);
        var lut = new PdfIccLut(data);
        double[] result = new double[3];
        lut.Transform([0.8, 0.5, 0.25], result, applyXyzMatrix: true);
        Assert.Equal(0.4, result[0], 10);
        Assert.Equal(0.5, result[1], 10);
        Assert.Equal(0.25, result[2], 10);
        lut.Transform([0.8, 0.5, 0.25], result);
        Assert.Equal(0.8, result[0], 10);
    }

    [Fact]
    public void Transform_AppliesInputAndOutputCurvesInOrder()
    {
        byte[] data = Table(3, true);
        // Red input maps 0..1 to 0..0.5. Red output maps 0..1 to 0.25..1.
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(54), 32768);
        int outputOffset = 52 + 3 * 2 * 2 + 8 * 3 * 2;
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(outputOffset), 16384);
        var lut = new PdfIccLut(data);
        double[] result = new double[3];
        lut.Transform([1, 0, 0], result);
        double start = 16384d / 65535;
        Assert.Equal(start + (1 - start) * (32768d / 65535), result[0], 12);
    }

    [Fact]
    public void Transform_CanReuseInputStorageWithoutAllocating()
    {
        var lut = new PdfIccLut(Table(3, true));
        double[] values = [0.25, 0.5, 0.75];
        for (int index = 0; index < 100; index++) lut.Transform(values, values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) lut.Transform(values, values);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(new double[] { 0.25, 0.5, 0.75 }, values);
    }

    [Theory]
    [InlineData(8, 0)]
    [InlineData(8, 5)]
    [InlineData(9, 0)]
    [InlineData(10, 1)]
    [InlineData(48, 255)]
    public void Constructor_RejectsInvalidDimensions(int offset, byte value)
    {
        byte[] data = Table(4, true);
        data[offset] = value;
        Assert.Throws<FormatException>(() => new PdfIccLut(data));
    }

    [Fact]
    public void Constructor_RejectsTruncationBeforeEvaluating()
    {
        byte[] data = Table(4, true);
        for (int length = 0; length < data.Length; length++)
            Assert.Throws<FormatException>(() => new PdfIccLut(data.AsMemory(0, length)));
    }

    private static byte[] Table(int inputs, bool sixteen)
    {
        int size = sixteen ? 2 : 1;
        int entries = sixteen ? 2 : 256;
        int header = sixteen ? 52 : 48;
        int cells = 1 << inputs;
        byte[] bytes = new byte[header + (inputs * entries + cells * 3 + 3 * entries) * size];
        (sixteen ? "mft2"u8 : "mft1"u8).CopyTo(bytes);
        bytes[8] = (byte)inputs;
        bytes[9] = 3;
        bytes[10] = 2;
        for (int row = 0; row < 3; row++)
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12 + row * 16), 65536);
        if (sixteen)
        {
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(48), (ushort)entries);
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(50), (ushort)entries);
        }
        int offset = header;
        void Write(double value)
        {
            if (sixteen) BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset),
                (ushort)Math.Round(value * 65535));
            else bytes[offset] = (byte)Math.Round(value * 255);
            offset += size;
        }
        for (int channel = 0; channel < inputs; channel++)
            for (int entry = 0; entry < entries; entry++) Write(entry / (double)(entries - 1));
        for (int cell = 0; cell < cells; cell++)
        {
            double first = (cell >> (inputs - 1)) & 1;
            if (inputs == 4) first *= cell & 1;
            Write(first);
            Write((cell >> (inputs - 2)) & 1);
            Write((cell >> (inputs - 3)) & 1);
        }
        for (int channel = 0; channel < 3; channel++)
            for (int entry = 0; entry < entries; entry++) Write(entry / (double)(entries - 1));
        return bytes;
    }
}
