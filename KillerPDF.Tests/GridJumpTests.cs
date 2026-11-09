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
        Assert.Contains("ContinuousRenderOrder.Around(renderCenter, primaryPageIdx + 1, limit - 1)", pass, StringComparison.Ordinal);
        Assert.DoesNotContain("for (int i = primaryPageIdx + 1; i < limit; i++)\n                        {\n                            if (cts.IsCancellationRequested) break;", pass.Replace("\r\n", "\n"), StringComparison.Ordinal);
    }

    [Fact]
    public void AGridJumpToAnUndrawnPageDrawsThatPageFirst()
    {
        string selection = File.ReadAllText(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.PageSelection.cs"));
        string grid = Between(selection, "if (_viewMode == ViewMode.Grid)", "// Clicking either page of the spread");

        Assert.Contains("gridTile.BringIntoView();", grid, StringComparison.Ordinal);
        Assert.Contains("if (GridTileIsBlank(_currentPage)) RenderAdditionalPages(0);", grid, StringComparison.Ordinal);
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
