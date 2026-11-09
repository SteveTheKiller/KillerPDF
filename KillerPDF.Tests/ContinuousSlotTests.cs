using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Continuous view built a page image, an annotation overlay and their grid for every page before the first page showed:
// about 1.6 s of a 10,000-page PDF's open on SN01, and WPF then measured all of them. A slot is now a sized placeholder
// until the page is drawn, and a paginating book's new pages are added by the same builder. The viewer cannot be built
// without a window here, so this pins the call sites; the open itself was measured on SN01.
public sealed class ContinuousSlotTests
{
    [Fact]
    public void SlotsAreSizedPlaceholdersUntilTheirPageIsDrawn()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string setup = Between(viewport, "internal void SetupContinuousView(", "private void AddContinuousSlot(int i)");
        string add = Between(viewport, "private void AddContinuousSlot(int i)", "private Grid EnsureContinuousSlotContent(int i)");
        string slot = Between(viewport, "private void SetContinuousSlot(", "LayoutContinuousSlots();");

        Assert.Contains("for (int i = 0; i < ReadPageCount; i++) AddContinuousSlot(i);", setup);
        Assert.DoesNotContain("BuildPageOverlay(", setup);
        Assert.DoesNotContain("BuildPageOverlay(", add);
        Assert.DoesNotContain("new Image", add);
        Assert.Contains("EnsureContinuousSlotContent(fi)", slot);
    }

    [Fact]
    public void APaginatingBookAddsItsPagesThroughTheSameSlotBuilder()
    {
        string pagination = Source("Controls", "Viewer", "PdfViewer.NativePagination.cs");

        Assert.Contains("AddContinuousSlot(page);", pagination);
        Assert.DoesNotContain("private void AddContinuousSlot(", pagination);
    }

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string Source(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine([directory!.FullName, .. parts]));
    }
}
