using System.IO;
using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPDF.Tests;

// The working CMYK source is process-wide, so these tests share the working CMYK collection.
[Collection(nameof(WorkingCmykRenderTests))]
public sealed class WatchedFolderWorkingCmykTests : IDisposable
{
    // Same synthetic affine CMYK profile as WorkingCmykRenderTests.
    private const string AffineCmyk =
        "AAABQAAAAAAEAAAAcHJ0ckNNWUtYWVogAAAAAAAAAAAAAAAAYWNzcAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQTJCMQAAAJAAAACwbWZ0MgAAAAAEAwIAAAEAAAAAAAAAAAAAAAAAAAABAAAAAAAAAAAAAAAAAAAAAQAAAAIAAgAA//8AAP//AAD//wAA//9AAEAAQAAgACAAIABAAEAAIAAgACAAAABAACAAQAAgAAAAIABAACAAIAAgAAAAAAAgAEAAQAAAACAAIAAgAEAAIAAAACAAAAAgACAAQAAAAAAAIAAgACAAIAAAAAAAAAAAAP//AAD//wAA//8=";
    private readonly Func<PdfColorProfile?> _source = WorkingCmykProfiles.Source;
    private readonly Func<long> _stamp = WorkingCmykProfiles.Stamp;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "killerpdf-watched-folders",
        "working-cmyk-" + Guid.NewGuid().ToString("N"));

    // A watched folder's color conversion step reads untagged CMYK through the chosen working
    // profile with the service's own operations.
    [Fact]
    public async Task AWatchedFolderConvertsColorThroughTheChosenWorkingProfile()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(1, 1, "1 0 0 0 k 0 0 1 1 re f"u8.ToArray()).Build();
        var chosen = PdfColorProfile.Load(Convert.FromBase64String(AffineCmyk));
        int expected = Gray(source, chosen);
        Assert.NotEqual(Gray(source, null), expected);
        WorkingCmykProfiles.Source = () => chosen;
        WorkingCmykProfiles.Invalidate();
        string input = Path.Combine(_root, "input"), output = Path.Combine(_root, "output"), storage = Path.Combine(_root, "state");
        Directory.CreateDirectory(input);
        Directory.CreateDirectory(output);
        File.WriteAllBytes(Path.Combine(input, "cyan.pdf"), source);
        var clock = new TestClock();
        using var service = new WatchedFolderService(storage, clock: clock);
        var macro = new PdfMacro("Gray", [PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            new(PdfMacroOperation.Save)]);
        service.Configure(new WatchedFolderOptions(input, output, "*.pdf", macro.ToJson()));
        service.Resume();

        await service.ReconcileAsync();
        clock.Advance(TimeSpan.FromSeconds(3));
        await service.ReconcileAsync();

        WatchedFolderJob job = Assert.Single(service.GetSnapshot().Jobs);
        Assert.Equal(WatchedFolderJobStatus.Completed, job.Status);
        byte gray = new PdfPageRenderer(PdfDocument.Open(File.ReadAllBytes(job.OutputPath)))
            .Render(0, new PdfRenderOptions(1, 1)).Pixels.Span[0];
        Assert.True(Math.Abs(gray - expected) <= 1, $"gray {gray}, expected {expected}, built-in {Gray(source, null)}");
    }

    // The watched-folder host and the Explorer preview pane can be other processes than the one
    // where the choice changes, so nothing invalidates their cache. A changed choice on disk must
    // still reach them, and an unchanged one must not be read again.
    [Fact]
    public void AWorkingCmykChoiceSavedElsewhereIsPickedUpWithoutInvalidation()
    {
        var library = new ProofProfileLibrary(Path.Combine(_root, "profiles"));
        ProofProfileEntry affine = library.Import("Affine", Convert.FromBase64String(AffineCmyk));
        int reads = 0;
        WorkingCmykProfiles.Source = () => { reads++; return library.ResolveWorkingCmykProfile(); };
        WorkingCmykProfiles.Stamp = library.GetWorkingCmykChoiceStamp;
        WorkingCmykProfiles.Invalidate();
        Assert.Null(WorkingCmykProfiles.Current());

        new ProofProfileLibrary(Path.Combine(_root, "profiles")).SetWorkingCmykProfile(affine.ContentSha256);
        PdfColorProfile? changed = WorkingCmykProfiles.Current();
        reads = 0;
        PdfColorProfile? unchanged = WorkingCmykProfiles.Current();

        Assert.Equal(affine.ContentSha256, changed?.ContentSha256);
        Assert.Same(changed, unchanged);
        Assert.Equal(0, reads);
    }

    // The gray the conversion should produce: the weighted sum of the page's managed color.
    private static int Gray(byte[] source, PdfColorProfile? working)
    {
        var options = new PdfRenderOptions(1, 1);
        if (working is not null) options = options with { WorkingCmykProfile = working };
        byte[] shown = new PdfPageRenderer(PdfDocument.Open(source)).RenderFrame(0, options).ToSrgbBgra32();
        return (int)Math.Round(0.3 * shown[2] + 0.59 * shown[1] + 0.11 * shown[0], MidpointRounding.AwayFromZero);
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount) => _now += amount;
    }

    public void Dispose()
    {
        WorkingCmykProfiles.Source = _source;
        WorkingCmykProfiles.Stamp = _stamp;
        WorkingCmykProfiles.Invalidate();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
