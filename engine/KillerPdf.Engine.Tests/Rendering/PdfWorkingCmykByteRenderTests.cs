using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfWorkingCmykByteRenderTests
{
    // Same synthetic affine CMYK profile (A2B1 only) as PdfPreciseFrameDiagnosticsTests.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    // Grid inks fall on the display table's 17 points per channel, so the byte render reproduces
    // the managed (float) result up to its own 8-bit rounding; off-grid inks interpolate.
    [Theory]
    [InlineData(255, 0, 0, 0, 1)]
    [InlineData(0, 255, 0, 0, 1)]
    [InlineData(0, 0, 255, 0, 1)]
    [InlineData(0, 0, 0, 255, 1)]
    [InlineData(255, 255, 0, 0, 1)]
    [InlineData(153, 102, 102, 255, 1)]
    [InlineData(255, 255, 255, 255, 1)]
    [InlineData(192, 128, 64, 128, 2)]
    [InlineData(37, 201, 90, 11, 2)]
    public void UntaggedCmykPathsAndImagesShowTheManagedWorkingProfileColor(byte c, byte m, byte y, byte k, int tolerance)
    {
        var document = Swatch(c, m, y, k);
        var renderer = new PdfPageRenderer(document);
        byte[] managed = renderer.RenderFrame(0, new PdfRenderOptions(2, 1)).ToSrgbBgra32();

        byte[] bytes = renderer.Render(0, new PdfRenderOptions(2, 1)).Pixels.ToArray();

        for (int channel = 0; channel < bytes.Length; channel++)
            Assert.InRange(bytes[channel], managed[channel] - tolerance, managed[channel] + tolerance);
    }

    [Fact]
    public void AChosenWorkingProfileChangesTheByteRenderAndIsNotServedFromTheOldCache()
    {
        var document = Swatch(255, 0, 0, 0);
        var renderer = new PdfPageRenderer(document);
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        var builtIn = new PdfRenderOptions(2, 1);
        var custom = new PdfRenderOptions(2, 1) { WorkingCmykProfile = chosen };
        byte[] before = renderer.Render(0, builtIn).Pixels.ToArray();

        byte[] after = renderer.Render(0, custom).Pixels.ToArray();
        byte[] managed = renderer.RenderFrame(0, custom).ToSrgbBgra32();

        Assert.NotEqual(before, after);
        for (int channel = 0; channel < after.Length; channel++)
            Assert.InRange(after[channel], managed[channel] - 1, managed[channel] + 1);
        Assert.Equal(before, renderer.Render(0, builtIn).Pixels.ToArray());
    }

    // The profile has no inverse table, so the fitted inverse chooses among separations that show
    // the same color. It must stay within the CGATS TR 001 (SWOP) total ink limit of 300 percent,
    // 765 of 1020: a heavier rich black for near-black RGB blends and overprints visibly darker
    // in CMYK groups (corpus MOZILLA-1077170-7, delta E up to 18.8).
    [Theory]
    [InlineData(15, 15, 15)]
    [InlineData(15, 60, 45)]
    [InlineData(15, 45, 60)]
    public void DarkRgbPaintEnteringInkStaysWithinTheTotalInkLimit(byte red, byte green, byte blue)
    {
        uint ink = PdfDeviceCmyk.FromRgb(red, green, blue);
        int total = (byte)ink + (byte)(ink >> 8) + (byte)(ink >> 16) + (byte)(ink >> 24);
        uint shown = PdfDeviceCmyk.ToRgb(ink);

        Assert.InRange(total, 0, 765);
        Assert.InRange(Math.Abs((byte)(shown >> 16) - red), 0, 6);
        Assert.InRange(Math.Abs((byte)(shown >> 8) - green), 0, 6);
        Assert.InRange(Math.Abs((byte)shown - blue), 0, 6);
    }

    // The byte path's lookup-table evaluation is exact: it returns the general profile evaluation's
    // bytes for every ink on a dense grid and for a million random inks, for both profiles.
    [Fact]
    public void ByteEvaluationEqualsTheGeneralProfileEvaluationOnADenseGrid()
    {
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        var random = new Random(11);
        foreach (var table in new[] { PdfCmykDisplayTable.Current, PdfCmykDisplayTable.For(chosen) })
        {
            int mismatches = 0;
            string first = "";
            void Check(uint ink)
            {
                uint fast = table.ToRgb(ink), general = table.Evaluate(ink);
                if (fast != general && mismatches++ == 0) first = $"{ink:X8}: {fast:X6} against {general:X6}";
            }
            for (int c = 0; c < 256; c += 5) for (int m = 0; m < 256; m += 5) for (int y = 0; y < 256; y += 5) for (int k = 0; k < 256; k += 5)
                Check((uint)(c | m << 8 | y << 16 | k << 24));
            for (int sample = 0; sample < 1_000_000; sample++) Check((uint)random.NextInt64(0, 1L << 32));
            Assert.True(mismatches == 0, $"{mismatches} mismatches, first {first}");
        }
    }

    // RGB paint entering ink under a chosen profile round-trips through that profile's own inverse.
    [Theory]
    [InlineData(128, 128, 128)]
    [InlineData(96, 96, 96)]
    public void RgbPaintInAChosenProfileGroupUsesThatProfilesInverse(byte red, byte green, byte blue)
    {
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        var table = PdfCmykDisplayTable.For(chosen);
        uint displayed = table.ToRgb(table.FromRgb(red, green, blue));
        Assert.InRange(Math.Abs((byte)(displayed >> 16) - red), 0, 6);
        Assert.InRange(Math.Abs((byte)(displayed >> 8) - green), 0, 6);
        Assert.InRange(Math.Abs((byte)displayed - blue), 0, 6);
    }

    private static PdfDocument Swatch(byte c, byte m, byte y, byte k)
    {
        // A vector fill on the left pixel and a one-sample DeviceCMYK image on the right one.
        var content = new PdfContentStreamBuilder()
            .SetFillCmyk(c / 255d, m / 255d, y / 255d, k / 255d)
            .Rectangle(0, 0, 1, 1).Fill()
            .DrawImage(PdfImage.FromCmyk(1, 1, new byte[] { c, m, y, k }), 1, 0, 1, 1);
        return PdfDocument.Open(new PdfDocumentBuilder().AddPage(2, 1, content).Build());
    }
}
