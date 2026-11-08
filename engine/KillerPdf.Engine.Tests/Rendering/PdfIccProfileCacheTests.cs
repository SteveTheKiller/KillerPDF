using System.Buffers.Binary;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfIccProfileCacheTests
{
    [Fact]
    public void ASixMebibyteProfileIsParsedOncePerPageRatherThanOncePerOperator()
    {
        byte[] profile = LargeLutProfile(101);
        // 101^3 cells of three 16-bit samples: about 5.9 MiB, beyond the 4 MiB cache entry limit.
        Assert.InRange(profile.Length, 5_500_000, 6_500_000);
        long once = AllocatedRendering(profile, 1);
        long fifty = AllocatedRendering(profile, 50);

        // One parse expands the 101^3 RGB grid to about 5.9 MiB of samples. Forty-nine more
        // operators may add content parsing, but never another forty-nine parses (about 290 MiB).
        Assert.True(fifty - once < 32L * 1024 * 1024, $"Fifty operators allocated {fifty - once} more bytes than one.");
    }

    private static long AllocatedRendering(byte[] profile, int operators)
    {
        var content = new StringBuilder();
        for (int index = 0; index < operators; index++) content.Append("/CS0 cs 0.5 0.5 0.5 sc 0 0 1 1 re f\n");
        var renderer = new PdfPageRenderer(PdfDocument.Open(TestIccProfiles.IccRgbPage(profile, content.ToString())));
        var options = new PdfRenderOptions(1, 1) { CacheResult = false };
        GC.Collect();
        GC.WaitForPendingFinalizers();
        long before = GC.GetAllocatedBytesForCurrentThread();
        renderer.Render(0, options);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    // An RGB display profile whose A2B0 is a lut16 with the given grid and a constant mid-gray grid.
    private static byte[] LargeLutProfile(int grid)
    {
        int cells = grid * grid * grid;
        var lut = new byte[52 + 3 * 2 * 2 + cells * 3 * 2 + 3 * 2 * 2];
        "mft2"u8.CopyTo(lut);
        lut[8] = 3;
        lut[9] = 3;
        lut[10] = (byte)grid;
        for (int index = 0; index < 3; index++) BinaryPrimitives.WriteInt32BigEndian(lut.AsSpan(12 + index * 16), 65536);
        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(48), 2);
        BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(50), 2);
        int offset = 52;
        for (int channel = 0; channel < 3; channel++, offset += 4)
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(offset + 2), 65535);
        for (int sample = 0; sample < cells * 3; sample++, offset += 2)
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(offset), 0x4000);
        for (int channel = 0; channel < 3; channel++, offset += 4)
            BinaryPrimitives.WriteUInt16BigEndian(lut.AsSpan(offset + 2), 65535);
        return TestIccProfiles.Profile("mntr", "RGB ", "XYZ ",
            [("A2B0", lut), ("wtpt", TestIccProfiles.Xyz(TestIccProfiles.D50White))]);
    }
}
