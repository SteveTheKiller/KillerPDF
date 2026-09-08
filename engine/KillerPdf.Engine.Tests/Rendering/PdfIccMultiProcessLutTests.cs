using System.Buffers.Binary;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccMultiProcessLutTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Transform_RespectsDirectionAndMatrixOffsets(bool reverse, bool sixteen)
    {
        var table = PdfIccTable.Read(Table(reverse, sixteen, 3, 3, true));
        double[] result = new double[3];
        table.Transform([0.5, 0.5, 0.5], result);
        double expected = reverse ? 0.25 : 0.140625;
        foreach (double value in result) Assert.InRange(value, expected - 0.002, expected + 0.002);
        Assert.False(table.UsesLegacyLabEncoding);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Transform_HandlesDifferentChannelCountsAndGridDimensions(bool reverse)
    {
        var table = PdfIccTable.Read(Table(reverse, true, reverse ? 3 : 4, reverse ? 4 : 3, false));
        double[] input = reverse ? [0.25, 0.5, 0.75] : [0.25, 0.5, 0.75, 0.8];
        double[] output = new double[table.OutputChannels];
        table.Transform(input, output);
        for (int channel = 0; channel < output.Length; channel++)
            Assert.InRange(output[channel], input[channel % input.Length] - 0.00002,
                input[channel % input.Length] + 0.00002);
    }

    [Fact]
    public void Transform_CanAliasBuffersWithoutAllocating()
    {
        var table = PdfIccTable.Read(Table(false, true, 3, 3, false));
        double[] values = [0.25, 0, 0.75];
        for (int index = 0; index < 100; index++) table.Transform(values, values);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int index = 0; index < 1000; index++) table.Transform(values, values);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(new double[] { 0.25, 0, 0.75 }, values);
    }

    [Fact]
    public void Constructor_RejectsInvalidElementCombinationsAndOffsets()
    {
        byte[] data = Table(false, true, 3, 3, true);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), 0);
        Assert.Throws<FormatException>(() => PdfIccTable.Read(data));
        data = Table(false, true, 3, 3, false);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12), uint.MaxValue);
        Assert.Throws<FormatException>(() => PdfIccTable.Read(data));
        data = Table(false, true, 3, 3, false);
        int clut = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(24));
        data[clut + 16] = 3;
        Assert.Throws<FormatException>(() => PdfIccTable.Read(data));
    }

    [Fact]
    public void Constructor_RejectsTruncation()
    {
        byte[] data = Table(false, true, 4, 3, true);
        // The final curve has two bytes of optional alignment padding.
        for (int length = 0; length < data.Length - 2; length++)
            Assert.Throws<FormatException>(() => PdfIccTable.Read(data.AsMemory(0, length)));
    }

    private static byte[] Table(bool reverse, bool sixteen, int inputs, int outputs, bool matrix)
    {
        int matrixChannels = reverse ? inputs : outputs;
        int aChannels = reverse ? outputs : inputs;
        int[] grid = Enumerable.Range(0, inputs).Select(index => index == 1 ? 3 : 2).ToArray();
        int cells = grid.Aggregate(1, (product, count) => product * count);
        int sample = sixteen ? 2 : 1;
        int bOffset = 32;
        int matrixOffset = bOffset + matrixChannels * 16;
        int mOffset = matrixOffset + (matrix ? 48 : 0);
        int clutOffset = mOffset + (matrix ? matrixChannels * 16 : 0);
        int aOffset = (clutOffset + 20 + cells * outputs * sample + 3) & ~3;
        byte[] data = new byte[aOffset + aChannels * 16];
        (reverse ? "mBA "u8 : "mAB "u8).CopyTo(data);
        data[8] = (byte)inputs;
        data[9] = (byte)outputs;
        int[] offsets = [bOffset, matrix ? matrixOffset : 0, matrix ? mOffset : 0, clutOffset, aOffset];
        for (int index = 0; index < offsets.Length; index++)
            BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(12 + index * 4), (uint)offsets[index]);
        void Curves(int offset, int count, ushort gamma)
        {
            for (int index = 0; index < count; index++)
            {
                int start = offset + index * 16;
                "curv"u8.CopyTo(data.AsSpan(start));
                BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(start + 8), 1);
                BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(start + 12), gamma);
            }
        }
        Curves(bOffset, matrixChannels, matrix ? (ushort)512 : (ushort)256);
        Curves(aOffset, aChannels, 256);
        if (matrix)
        {
            Curves(mOffset, matrixChannels, 256);
            for (int row = 0; row < 3; row++)
            {
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(matrixOffset + row * 16), 32768);
                BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(matrixOffset + 36 + row * 4), 8192);
            }
        }
        for (int channel = 0; channel < inputs; channel++) data[clutOffset + channel] = (byte)grid[channel];
        data[clutOffset + 16] = (byte)sample;
        int position = clutOffset + 20;
        double[] values = new double[inputs];
        for (int cell = 0; cell < cells; cell++)
        {
            int remainder = cell;
            for (int channel = inputs - 1; channel >= 0; channel--)
            {
                values[channel] = (remainder % grid[channel]) / (double)(grid[channel] - 1);
                remainder /= grid[channel];
            }
            for (int channel = 0; channel < outputs; channel++)
            {
                double value = values[channel % inputs];
                if (sixteen) BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(position), (ushort)Math.Round(value * 65535));
                else data[position] = (byte)Math.Round(value * 255);
                position += sample;
            }
        }
        return data;
    }
}
