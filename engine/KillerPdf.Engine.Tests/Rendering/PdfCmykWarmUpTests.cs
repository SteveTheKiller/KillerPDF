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

    // The bundled profile's RGB to ink table ships with the engine, so neither the warm-up nor the first RGB paint
    // into ink fits it, in any process.
    [Fact]
    public void TheBundledProfileUsesItsShippedTableWithoutFitting()
    {
        var profile = PdfCmykViewingDefault.LoadProfile();
        var table = PdfCmykDisplayTable.For(profile);

        PdfCmykWarmUp.Prepare(null);
        using (PdfCmykDisplayTable.Use(profile))
            _ = PdfDeviceCmyk.FromRgb(200, 120, 40);

        Assert.Equal(0, table.FitIterations);
    }

    // The shipped table is exactly what the fit gives for the bundled profile. A change to the fit or to the profile
    // fails here until the resource is fitted again.
    [Fact]
    public void TheShippedTableIsTheFitOfTheBundledProfile()
    {
        // The table is fitted on Windows and shipped to every platform. The iterative fit lands on different bytes
        // under Linux's math library, so only Windows can say whether the shipped table is stale.
        if (!OperatingSystem.IsWindows()) return;
        var profile = PdfCmykViewingDefault.LoadProfile();

        byte[]? shipped = PdfCmykDisplayTable.ShippedInverse(profile.ContentSha256);

        Assert.NotNull(shipped);
        Assert.Equal(PdfCmykDisplayTable.FitRgbInverse(profile), shipped);
        Assert.Null(PdfCmykDisplayTable.ShippedInverse(OwnProfile().ContentSha256));
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