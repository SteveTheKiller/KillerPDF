using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// Source pins for the tab right-click card: the old flat row list became the card style the page and
// gray-area menus use, built by the host's QuickStrip through IViewerHost.FillCardMenu. Three full rows
// of three; nothing that was on the old menu is gone.
public sealed class TabCardMenuTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([FindRepositoryRoot(), .. parts]));

    private static string TabMenuBody()
    {
        string source = Read("Controls", "Viewer", "PdfViewer.TabStrip.cs");
        int start = source.IndexOf("private void Tab_RightClick(", System.StringComparison.Ordinal);
        int end = source.IndexOf("private static string GlyphText(", start, System.StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start);
        return source[start..end];
    }

    [Fact]
    public void TabMenuIsACardOfThreeFullRows()
    {
        string body = TabMenuBody();
        Assert.Contains("host.FillCardMenu(menu, 3,", body);
        Assert.DoesNotContain("new Separator()", body);
        Assert.DoesNotContain("AddTabPlacementMenuItems", body);
        var tiles = Regex.Matches(body, @"^\s*new\(GlyphText\(0x[0-9A-F]{4}\)", RegexOptions.Multiline);
        Assert.Equal(9, tiles.Count);
        Assert.Equal(0, tiles.Count % 3);
    }

    [Theory]
    [InlineData("Str_Ctx_CloseTab", "\"Ctrl:W\"")]
    [InlineData("Str_Ctx_CloseOthers", "\"CtrlShift:W\"")]
    [InlineData("Str_Ctx_CloseAllTabs", "\"CtrlShift:Q\"")]
    [InlineData("Str_Ctx_ReopenClosedTab", "\"CtrlShift:T\"")]
    [InlineData("Str_Ctx_OpenContainingFolder", "null")]
    [InlineData("Str_Ctx_MoveToNewWindow", "\"CtrlShift:N\"")]
    [InlineData("Str_Card_Move", "null")]
    [InlineData("Str_Layout_TabsTop", "\"AltShift:Up\"")]
    [InlineData("Str_Layout_TabsBottom", "\"AltShift:Down\"")]
    public void EveryTabActionIsAGlyphTileWithItsShortcut(string key, string gesture)
        => Assert.Matches(@"new\(GlyphText\(0x[0-9A-F]{4}\), Loc\(""" + key + @"""\), " + Regex.Escape(gesture) + ",", TabMenuBody());

    [Fact]
    public void OtherWindowsSitBehindAFlyoutTileAndPlacementIsAnOnOffPair()
    {
        string body = TabMenuBody();
        Assert.Contains("Loc(\"Str_Ctx_MoveToWindow\")", body);
        Assert.Contains("host.OtherDocumentWindows()", body);
        Assert.Contains("{ Flyout = moveTo }", body);
        Assert.Contains("moveTo.IsEnabled = moveTo.Items.Count > 0;", body);
        Assert.Contains("{ Checked = !TabsAtBottom }", body);
        Assert.Contains("{ Checked = TabsAtBottom }", body);
    }

    [Fact]
    public void HostBuildsTheTilesWithTheSharedCardHelpers()
    {
        string host = Read("Features", "Viewer", "MainWindowViewerHost.cs");
        int start = host.IndexOf("void IViewerHost.FillCardMenu(", System.StringComparison.Ordinal);
        Assert.True(start >= 0);
        string fill = host[start..host.IndexOf("void IViewerHost.CloseSearchBar()", start, System.StringComparison.Ordinal)];
        Assert.Contains("TryFindResource(\"CardMenu\")", fill);
        Assert.Contains("QuickStrip(menu, columns,", fill);
        Assert.Contains("FlyoutTile(", fill);
        Assert.Contains("Checked = t.Checked", fill);
        Assert.Contains("CardSlot(grid, new Thickness(0))", fill);
        Assert.Contains("FocusFirstActionFromKeyboard(menu, grid)", fill);
        Assert.Contains("void FillCardMenu(ContextMenu menu, int columns, IReadOnlyList<ViewerCardTile> tiles);",
            Read("Features", "Viewer", "IViewerHost.cs"));
    }

    [Fact]
    public void TabMenuWritesNoPrivateUseGlyphs()
        => Assert.DoesNotContain(TabMenuBody(), c => c >= 0xE000 && c <= 0xF8FF);

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj not found.");
    }
}
