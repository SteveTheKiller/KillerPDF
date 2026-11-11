using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// The Extract Image dialog is not linked into this project, so these pin how it is set up. Its
// behavior is exercised by the window steps on a UI VM.
public sealed class ExtractImageDialogSourceTests
{
    private static string Source()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Controls", "ExtractImageDialog.cs"));
    }

    // image BF-011: the box was scanned from every pixel up to three times per refresh and the overlay
    // was allocated again each time.
    [Fact]
    public void RefreshReusesTheSelectionsBoxAndTheOverlayBuffer()
    {
        string source = Source();
        Assert.Contains("ReferenceEquals(_boundsOf, _selection)", source, StringComparison.Ordinal);
        Assert.Contains("_overlayPixels ??= new byte[", source, StringComparison.Ordinal);
        Assert.DoesNotContain("var pixels = new byte[", source, StringComparison.Ordinal);
    }

    // image BF-011: a slider drag does the work once it pauses, and Apply does it first.
    [Fact]
    public void SliderDragsWaitForAPauseAndApplyFlushesThem()
    {
        string source = Source();
        Assert.Contains("_tolerance.ValueChanged += (_, _) => { _toleranceValue.Text = $\"{_tolerance.Value:0}%\"; AfterSliderPause(reseed: true); };",
            source, StringComparison.Ordinal);
        int commit = source.IndexOf("private void Commit()", StringComparison.Ordinal);
        Assert.True(commit >= 0 && source.IndexOf("FlushSliderWork();", commit, StringComparison.Ordinal) > commit);
    }

    // image BF-012: the too-large check measures the feathered image, and the feather controls refresh.
    [Fact]
    public void TheTooLargeCheckCountsTheFeatherAndTheFeatherControlsRefresh()
    {
        string source = Source();
        Assert.Contains("var bounds = ExtractedBounds();", source, StringComparison.Ordinal);
        Assert.Contains("_feather.Checked += (_, _) => { _featherRadius.IsEnabled = true; Refresh(); };", source, StringComparison.Ordinal);
        Assert.Contains("_featherRadius.ValueChanged +=", source, StringComparison.Ordinal);
    }
}
