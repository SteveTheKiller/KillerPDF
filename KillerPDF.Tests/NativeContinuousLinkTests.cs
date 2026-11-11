using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// A book's links come from its page layout. Continuous view built every page's slot and read every page's links on the
// UI thread before the first page showed, which laid out a whole book: the Complete Works of Shakespeare sat on a blank
// view for about 35 seconds after "Opened" on the lab VM. A PDF did the same with its link annotations: 2,000 pages with
// ten links each spent about 1.6 s more in that step than plain pages on SN01. The viewer cannot be built without a
// window here, so this pins the call sites; the open itself was measured on SN01.
public sealed class NativeContinuousLinkTests
{
    [Fact]
    public void ContinuousViewReadsLinksOnlyForPagesItHasDrawn()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string setup = Between(viewport, "internal void SetupContinuousView(", "internal async System.Threading.Tasks.Task RenderContinuousPages(");
        string slot = Between(viewport, "private void SetContinuousSlot(", "LayoutContinuousSlots();");

        Assert.DoesNotContain("AddSecondaryPageLinks(", setup);
        Assert.Contains("if (_nativeDoc is null && !_continuousLinks.ContainsKey(fi))", slot);
        Assert.Contains("_continuousLinks[fi] = GetPageLinks(fi, rdW, rdH);", slot);
        // A book's links, and their keyboard overlays, are added once, after the page's annotations are drawn (drawing
        // them clears the canvas), so the first-draw step does not add them a second time.
        Assert.DoesNotContain("AddSecondaryPageLinks(", slot);
    }

    [Fact]
    public void ABooksLinksAreAddedOnceAfterItsAnnotationsAreDrawn()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string slot = Between(viewport, "private void SetContinuousSlot(", "/// <summary>True when part of the page is in the document viewport now.");

        Assert.Single(System.Text.RegularExpressions.Regex.Matches(slot, @"AddSecondaryPageLinks\("));
        Assert.Matches(@"RenderAllAnnotations\(fi\);\s*if \(_nativeDoc is not null\) AddSecondaryPageLinks\(fi, rdW, rdH\);", slot);
    }

    // A Grid rebuilt at another zoom changes which pages are on screen without scrolling. An EPUB search reads a page's
    // highlight areas once the page is on screen, so a layout change that moves pages into view also repaints them; on
    // SN01 a zoomed-out Grid showed highlights only on the page that had them before, until the grid was scrolled.
    [Fact]
    public void ALayoutChangeRepaintsSearchHighlightsForPagesItBringsIntoView()
    {
        string viewport = Source("Controls", "Viewer", "PdfViewer.Viewport.cs");
        string scroll = Between(viewport, "private void PagePreviewPanel_ScrollChanged(", "if (_active is not null)");

        Assert.Contains("e.ExtentHeightChange != 0 || e.ExtentWidthChange != 0 || e.ViewportHeightChange != 0", scroll);
        Assert.Contains("Search.WaitsForPagesOnScreen", scroll);
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
