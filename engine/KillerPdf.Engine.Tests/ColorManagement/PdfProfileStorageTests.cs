using System.Buffers.Binary;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using Xunit;

namespace KillerPdf.Engine.Tests.ColorManagement;

public sealed class PdfProfileStorageTests
{
    [Fact]
    public void SharedRgbToneCurveIsReadOnceWithoutRefusingTheProfile()
    {
        _ = PdfColorProfile.Load(SharedRgbCurve(2));
        byte[] data = SharedRgbCurve(8192);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var profile = PdfColorProfile.Load(data);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated <= data.Length * 8L + 65536,
            $"Shared RGB curve allocated {allocated} bytes for {data.Length} encoded bytes.");
        double[] xyz = new double[3];
        profile.ToXyz([1, 1, 1], xyz);
        Assert.Equal(new double[] { 1, 1, 1 }, xyz);
    }

    [Fact]
    public void OverlappingProcessingTablesCannotMultiplyTheProfileStorageBudget()
    {
        var profile = PdfColorProfile.Load(SharedProcessingTables(reverse: true));
        Assert.False(profile.GetTransformSelection(PdfRenderingIntent.RelativeColorimetric).IsAvailable);
        Assert.True(profile.GetTransformSelection(PdfRenderingIntent.Perceptual).IsAvailable);
    }

    [Fact]
    public void IntentAliasesOfTheSameProcessingTableRemainAvailable()
    {
        var profile = PdfColorProfile.Load(SharedProcessingTables(reverse: false));
        foreach (var intent in new[] { PdfRenderingIntent.RelativeColorimetric, PdfRenderingIntent.Perceptual, PdfRenderingIntent.Saturation })
            Assert.True(profile.GetTransformSelection(intent).IsAvailable);
        double[] xyz = new double[3];
        profile.ToXyz([0, 0, 0, 0], xyz);
        Assert.Equal(new double[3], xyz);
    }

    [Fact]
    public void AChangedTagLengthCannotRestartTheDecodedSampleBudget()
    {
        byte[] original = SharedProcessingTables(reverse: false);
        byte[] data = [.. original, 0, 0, 0, 0];
        BinaryPrimitives.WriteInt32BigEndian(data, data.Length);
        // Both slices contain every required sample; one also contains four trailing bytes.
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(140), data.Length - 168);
        var profile = PdfColorProfile.Load(data);
        Assert.True(profile.GetTransformSelection(PdfRenderingIntent.RelativeColorimetric).IsAvailable);
        Assert.False(profile.GetTransformSelection(PdfRenderingIntent.Perceptual).IsAvailable);
    }

    // Two headers point at one CLUT payload. The forward 8^4*3 samples and reverse
    // 8^3*4 samples are individually valid but their decoded doubles exceed eight
    // times the shared encoded data. Exact intent aliases need only one table.
    private static byte[] SharedProcessingTables(bool reverse)
    {
        const int first = 168, second = 200, bCurves = 232, aCurves = 268, clut = 316;
        byte[] data = new byte[clut + 20 + 8 * 8 * 8 * 8 * 3];
        Write(0, data.Length); data[8] = 4;
        "prtr"u8.CopyTo(data.AsSpan(12)); "CMYK"u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20)); "acsp"u8.CopyTo(data.AsSpan(36));
        Write(128, 3);
        Tag(132, "A2B0"u8, first);
        Tag(144, "A2B1"u8, first);
        Tag(156, reverse ? "B2A1"u8 : "A2B2"u8, reverse ? second : first);
        Header(first, false);
        if (reverse) Header(second, true);
        for (int index = 0; index < 3; index++) "curv"u8.CopyTo(data.AsSpan(bCurves + index * 12));
        for (int index = 0; index < 4; index++) "curv"u8.CopyTo(data.AsSpan(aCurves + index * 12));
        data.AsSpan(clut, 4).Fill(8); data[clut + 16] = 1;
        return data;

        void Write(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
        void Tag(int offset, ReadOnlySpan<byte> name, int start)
        {
            name.CopyTo(data.AsSpan(offset)); Write(offset + 4, start); Write(offset + 8, data.Length - start);
        }
        void Header(int offset, bool backwards)
        {
            (backwards ? "mBA "u8 : "mAB "u8).CopyTo(data.AsSpan(offset));
            data[offset + 8] = backwards ? (byte)3 : (byte)4;
            data[offset + 9] = backwards ? (byte)4 : (byte)3;
            Write(offset + 12, bCurves - offset);
            Write(offset + 24, clut - offset);
            Write(offset + 28, aCurves - offset);
        }
    }

    private static byte[] SharedRgbCurve(int samples)
    {
        const int curve = 264;
        byte[] data = new byte[curve + 12 + samples * 2];
        Write(0, data.Length); data[8] = 4;
        "mntr"u8.CopyTo(data.AsSpan(12)); "RGB "u8.CopyTo(data.AsSpan(16));
        "XYZ "u8.CopyTo(data.AsSpan(20)); "acsp"u8.CopyTo(data.AsSpan(36));
        Write(128, 6);
        string[] names = ["rXYZ", "gXYZ", "bXYZ", "rTRC", "gTRC", "bTRC"];
        for (int tag = 0; tag < names.Length; tag++)
        {
            int entry = 132 + tag * 12, offset = tag < 3 ? 204 + tag * 20 : curve;
            System.Text.Encoding.ASCII.GetBytes(names[tag]).CopyTo(data, entry);
            Write(entry + 4, offset); Write(entry + 8, tag < 3 ? 20 : data.Length - curve);
            if (tag < 3) { "XYZ "u8.CopyTo(data.AsSpan(offset)); Write(offset + 8 + tag * 4, 65536); }
        }
        "curv"u8.CopyTo(data.AsSpan(curve)); Write(curve + 8, samples);
        for (int index = 0; index < samples; index++)
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(curve + 12 + index * 2),
                (ushort)((long)index * 65535 / (samples - 1)));
        return data;
        void Write(int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(offset), value);
    }
}
