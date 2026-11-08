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

    public void Dispose()
    {
        PdfPageRenderSession.WorkingCmykSource = _original;
        PdfPageRenderSession.InvalidateWorkingCmyk();
        if (File.Exists(_path)) File.Delete(_path);
    }
}
