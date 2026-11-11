using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Rendering;

public sealed class PdfCmykWarmUpTests
{
    // The synthetic affine CMYK profile of PdfWorkingCmykByteRenderTests, with one unused header byte changed so its
    // content hash is its own: no other test has fitted this profile, and the tables are shared across the process.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";

    private static PdfColorProfile OwnProfile()
    {
        byte[] bytes = Convert.FromBase64String(AffineCmyk);
        bytes[30] ^= 1;
        return PdfColorProfile.Load(bytes);
    }

    [Fact]
    public void PreparingTheProfileDoesTheFitOnceAndTheFirstRgbPaintDoesNone()
    {
        var profile = OwnProfile();
        var table = PdfCmykDisplayTable.For(profile);
        Assert.Equal(0, table.FitIterations);

        PdfCmykWarmUp.Prepare(profile);
        long afterPrepare = table.FitIterations;
        Assert.True(afterPrepare > 0, "the warm-up ran no fit");

        PdfCmykWarmUp.Prepare(profile);
        Assert.Equal(afterPrepare, table.FitIterations);

        using (PdfCmykDisplayTable.Use(profile))
            _ = PdfDeviceCmyk.FromRgb(200, 120, 40);
        Assert.Equal(afterPrepare, table.FitIterations);
    }

    [Fact]
    public async Task ParallelPreparesShareOneFit()
    {
        var profile = OwnProfile();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => PdfCmykWarmUp.Prepare(profile))));
        var table = PdfCmykDisplayTable.For(profile);
        long once = table.FitIterations;
        // 4913 grid points, two starts of 80 steps each at most.
        Assert.InRange(once, 1, 4913L * 160);
        PdfCmykWarmUp.Prepare(profile);
        Assert.Equal(once, table.FitIterations);
    }
}