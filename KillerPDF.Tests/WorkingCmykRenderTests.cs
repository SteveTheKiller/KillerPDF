using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// The working CMYK source is process-wide, so these tests never run beside other render tests.
[CollectionDefinition(nameof(WorkingCmykRenderTests), DisableParallelization = true)]
public sealed class WorkingCmykRenderCollection;

[Collection(nameof(WorkingCmykRenderTests))]
public sealed class WorkingCmykRenderTests : IDisposable
{
    // A synthetic affine CMYK output profile with a colorimetric forward table only.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";
    private readonly Func<PdfColorProfile?> _original = PdfPageRenderSession.WorkingCmykSource;
    private readonly string _path = Path.Combine(Path.GetTempPath(), "kp-working-cmyk-" + Guid.NewGuid().ToString("N") + ".pdf");

    [Fact]
    public void ChangingTheWorkingChoiceChangesTheManagedRenderAndReadsTheProfileOnce()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(8, 8, "1 0 0 0 k 0 0 8 8 re f"u8.ToArray()).Build();
        File.WriteAllBytes(_path, source);
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        // Independent expectation: the engine's own render with that working profile.
        float[] expected = new PdfPageRenderer(PdfDocument.Open(source))
            .RenderFrame(0, new PdfRenderOptions(8, 8) { WorkingCmykProfile = chosen }).Pixels[..4].ToArray();
        int reads = 0;
        PdfColorProfile? current = null;
        PdfPageRenderSession.WorkingCmykSource = () => { reads++; return current; };
        PdfPageRenderSession.InvalidateWorkingCmyk();
        using var session = PdfPageRenderSession.OpenEngineFirst(_path, 8, 8, null);

        // Built in: the frozen CGATS001 cyan corner oracle.
        var builtIn = session.RenderFittedFrame(0, 8, 8, default);
        Assert.InRange(builtIn.Pixels[0], -0.20839846805136676f - 0.000002f, -0.20839846805136676f + 0.000002f);

        current = chosen;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        reads = 0;
        PdfRenderedFrame? last = null;
        for (int render = 0; render < 10; render++) last = session.RenderFittedFrame(0, 8, 8, default);

        Assert.Equal(expected, last!.Pixels[..4].ToArray());
        Assert.Equal(chosen.ContentSha256, last.AssumedCmykProfileSha256);
        Assert.Equal(1, reads);
    }

    // Every byte surface shows untagged CMYK through the same working profile as managed views:
    // the viewer page, the thumbnail, a print page and an exported page image.
    [Theory]
    [InlineData("viewer")]
    [InlineData("thumbnail")]
    [InlineData("print")]
    [InlineData("export")]
    public void EveryByteSurfaceShowsTheChosenWorkingProfile(string surface)
    {
        byte[] source = new PdfDocumentBuilder().AddPage(8, 8, "1 0 0 0 k 0 0 8 8 re f"u8.ToArray()).Build();
        File.WriteAllBytes(_path, source);
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        PdfColorProfile? current = null;
        PdfPageRenderSession.WorkingCmykSource = () => current;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        byte[] builtIn = Render(surface);

        current = chosen;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        byte[] shown = Render(surface);

        // Independent expectation: the engine's managed frame of the page with that working profile.
        byte[] managed = new PdfPageRenderer(PdfDocument.Open(source))
            .RenderFrame(0, new PdfRenderOptions(8, 8) { WorkingCmykProfile = chosen }).ToSrgbBgra32();
        Assert.NotEqual(builtIn[..3], shown[..3]);
        for (int channel = 0; channel < 3; channel++)
            Assert.InRange(shown[channel], managed[channel] - 1, managed[channel] + 1);
    }

    // The color conversion macro that every host registers reads untagged CMYK through the chosen
    // working profile, so its gray matches the color the views show.
    [Fact]
    public void TheColorConversionMacroUsesTheChosenWorkingProfile()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, "1 0 0 0 k 0 0 1 1 re f"u8.ToArray()).Build();
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        PdfPageRenderSession.WorkingCmykSource = () => chosen;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        byte[] shown = new PdfPageRenderer(PdfDocument.Open(source))
            .RenderFrame(0, new PdfRenderOptions(1, 1) { WorkingCmykProfile = chosen }).ToSrgbBgra32();
        int expected = (int)Math.Round(0.3 * shown[2] + 0.59 * shown[1] + 0.11 * shown[0], MidpointRounding.AwayFromZero);
        var dispatcher = new PdfMacroDispatcher([new(PdfMacroOperation.ConvertColor, WorkingCmykProfiles.ConvertColor)]);

        var result = dispatcher.Execute(PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale), source,
            new Dictionary<string, string>());

        byte gray = new PdfPageRenderer(PdfDocument.Open(result.Data)).Render(0, new PdfRenderOptions(1, 1)).Pixels.Span[0];
        Assert.InRange(gray, expected - 1, expected + 1);
    }

    private byte[] Render(string surface)
    {
        if (surface == "print")
        {
            using var printing = PdfPageRenderSession.OpenEngineFirst(_path, 1, null, forPrinting: true);
            return printing.RenderBasePage(0).Pixels[..4];
        }
        using var session = PdfPageRenderSession.OpenEngineFirst(_path, 8, 8, null);
        return surface switch
        {
            "viewer" => session.RenderFittedPage(0, 8, 8).Pixels[..4],
            "thumbnail" => session.RenderPreview(0).Pixels[..4],
            _ => session.RenderPage(0).Pixels[..4]
        };
    }

    public void Dispose()
    {
        PdfPageRenderSession.WorkingCmykSource = _original;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
