using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Grid built a page's tile only when its bitmap arrived, in page order from page 2, at about 4.5 pages a second on the lab
// VM. A jump to a page past the ones drawn so far found no tile to scroll to and was dropped: the page box said 1500 while
// the grid stayed on its first row (a 2,000-page PDF, and War and Peace, on SN01). The viewer cannot be built without a
// window here, so these pin the two halves of the fix; the jump itself was checked on SN01.
public sealed class GridJumpTests
{
    [Fact]
    public void GridLaysOutEveryPagesSlotBeforeDrawingThemOutwardFromTheCurrentPage()
    {
        string pass = Between(Viewport(), "private async void RenderAdditionalPages(int primaryPageIdx)", "private void AddSecondaryTile(");
        int slots = pass.IndexOf("AddGridPlaceholders(", StringComparison.Ordinal);
        int stream = pass.IndexOf("await System.Threading.Tasks.Task.Run(", StringComparison.Ordinal);

        Assert.True(slots >= 0 && slots < stream, "Grid slots are laid out before the stream starts.");
        Assert.Contains("ContinuousRenderOrder.Around(renderCenter, renderFirst, renderLast)", pass, StringComparison.Ordinal);
        Assert.DoesNotContain("for (int i = primaryPageIdx + 1; i < limit; i++)\n                        {\n                            if (cts.IsCancellationRequested) break;", pass.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void AGridJumpToAnUndrawnPageDrawsThatPageFirst()
    {
        string selection = File.ReadAllText(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.PageSelection.cs"));
        string grid = Between(selection, "if (_viewMode == ViewMode.Grid)", "// Clicking either page of the spread");

        Assert.Contains("gridTile.BringIntoView();", grid, StringComparison.Ordinal);
        Assert.Contains("if (GridNeedsFillAroundCurrentPage()) FillGridAroundCurrentPage();", grid, StringComparison.Ordinal);
    }

    // Grid drew and kept every page of the document: War and Peace at 98% held 3.7 to 4.2 GB on SN01 with two cores busy
    // drawing pages nobody was looking at, and a typed jump was lost under that load. A pass draws the pages within two
    // screens of the current page and lets go of the bitmaps beyond four; a settled scroll fills in what it uncovers.
    [Fact]
    public void GridDrawsAndKeepsOnlyThePagesNearTheViewport()
    {
        string viewport = Viewport();
        string pass = Between(viewport, "private async void RenderAdditionalPages(int primaryPageIdx)", "private void AddSecondaryTile(");

        Assert.Contains("int reach = GridPagesPerScreen() * 2;", pass, StringComparison.Ordinal);
        Assert.Contains("renderFirst = Math.Max(primaryPageIdx + 1, renderCenter - reach);", pass, StringComparison.Ordinal);
        Assert.Contains("renderLast = Math.Min(limit - 1, renderCenter + reach);", pass, StringComparison.Ordinal);
        Assert.Contains("ReleaseGridTilesOutside(renderCenter - reach * 2, renderCenter + reach * 2);", pass, StringComparison.Ordinal);
        Assert.Contains("if (GridNeedsFillAroundCurrentPage()) FillGridAroundCurrentPage();",
            Between(viewport, "internal void StartRerenderTimer()", "protected override void OnDpiChanged("), StringComparison.Ordinal);
    }

    // A book has no PDF working document, so a column count taken from it gave an EPUB one column: Grid opened War and
    // Peace at 294%, one page across. The first Grid display counts the pages of whatever the tab shows.
    [Fact]
    public void GridOpensABookWithTheSameColumnsAsAPdf()
    {
        string selection = File.ReadAllText(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.PageSelection.cs"));
        string grid = Between(selection, "if (_viewMode == ViewMode.Grid)", "// Clicking either page of the spread");

        Assert.Contains("SetZoom(GridZoomForN(Math.Min(ReadPageCount, 3)))", grid, StringComparison.Ordinal);
        Assert.DoesNotContain("_doc?.PageCount", grid, StringComparison.Ordinal);
    }

    // A tab that opens straight into Grid (the last view used) sets its columns where the document is first shown. That
    // path also counted the PDF working document's pages, so a book reopened in Grid showed one page across at 294%.
    [Fact]
    public void ABookOpenedStraightIntoGridHasThreeColumns()
    {
        string bootstrap = Between(Viewport(), "internal void BootstrapDocumentView(", "internal void RefreshPageView(int pageIndex)");

        Assert.Equal(2, bootstrap.Split("_gridColumns = Math.Min(ReadPageCount, 3);").Length - 1);
        Assert.DoesNotContain("_doc?.PageCount", bootstrap, StringComparison.Ordinal);
    }

    private static string Viewport() => File.ReadAllText(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.Viewport.cs"));

    private static string Between(string source, string start, string end)
    {
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "Not found: " + start);
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "Not found: " + end);
        return source[from..to];
    }

    private static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory!.FullName;
    }
}
