using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A book's links come from its page layout. Continuous view built every page's slot and read every page's links on the
// UI thread before the first page showed, which laid out a whole book: the Complete Works of Shakespeare sat on a blank
// view for about 35 seconds after "Opened" on the lab VM. The viewer cannot be built without a window here, so this pins
// the two call sites; the open itself was measured on SN01.
public sealed class NativeContinuousLinkTests
{
    [Fact]
    public void ContinuousViewReadsABooksLinksOnlyForPagesItHasDrawn()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string setup = Between(viewport, "internal void SetupContinuousView(", "internal async System.Threading.Tasks.Task RenderContinuousPages(");
        string slot = Between(viewport, "private void SetContinuousSlot(", "LayoutContinuousSlots();");

        Assert.Contains("if (_nativeDoc is null) AddSecondaryPageLinks(i, rdW, rdH);", setup);
        Assert.DoesNotContain("                AddSecondaryPageLinks(i, rdW, rdH);", setup);
        Assert.Contains("if (_nativeDoc is not null && !_continuousLinks.ContainsKey(fi)) AddSecondaryPageLinks(fi, rdW, rdH);", slot);
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
