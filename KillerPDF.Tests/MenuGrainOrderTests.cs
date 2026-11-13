using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// The film grain is the last child of a menu panel's Grid, so it lies over the rows and never hides under a
// hovered row, and it is never hit-testable. Two panels in the window drew it first.
public sealed class MenuGrainOrderTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Window() => XDocument.Parse(ShellSource.Read("Shell", "MainWindow.xaml")).Root!;

    private static void AssertGrainIsLast(XElement grid, string what)
    {
        Assert.Equal(Presentation + "Grid", grid.Name);
        XElement last = grid.Elements().Last();
        string background = (string?)last.Attribute("Background") ?? "";
        Assert.True(last.Name == Presentation + "Border" && background.Contains("GrainBrushShared"),
            $"{what}: the last child of its Grid is {last.Name.LocalName}, not the grain Border, so the grain is drawn under the rows");
        Assert.Equal("False", (string?)last.Attribute("IsHitTestVisible"));
    }

    [Fact]
    public void TheOverflowPopupDrawsItsGrainOverTheRows()
    {
        XElement rows = Window().Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "OverflowMenuPanel");
        AssertGrainIsLast(rows.Parent!, "Overflow popup");
    }

    [Fact]
    public void TheWindowsSubmenuTemplateDrawsItsGrainOverTheRows()
    {
        XElement items = Window().Descendants(Presentation + "ItemsPresenter")
            .Single(e => (string?)e.Attribute(Grid_IsSharedSizeScope) == "True");
        AssertGrainIsLast(items.Parent!, "Submenu template");
    }

    private static readonly XName Grid_IsSharedSizeScope = "Grid.IsSharedSizeScope";
}
