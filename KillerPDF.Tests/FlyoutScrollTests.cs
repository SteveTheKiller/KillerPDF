using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The theme and language menus carry their own templates. In a short window FlyoutPlacement caps
/// their height, so their items have to sit in a scroller: with a bare ItemsPresenter a bottom
/// sidebar's menus were cut off with no way to reach the last rows. The comparison report's list
/// scrolls with its scrollbar against the card's right edge.
/// </summary>
public sealed class FlyoutScrollTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData("ThemeMenu")]
    [InlineData("LangMenu")]
    public void ABigRailMenuScrollsWhenTheWindowCapsIt(string name)
    {
        var menu = MainWindow().Descendants(Wpf + "ContextMenu").Single(m => (string?)m.Attribute(Xaml + "Name") == name);
        var presenter = menu.Descendants(Wpf + "ItemsPresenter").Single();
        var scroller = presenter.Parent!;
        Assert.Equal(Wpf + "ScrollViewer", scroller.Name);
        Assert.Equal("Auto", (string?)scroller.Attribute("VerticalScrollBarVisibility"));
        Assert.Equal("False", (string?)scroller.Attribute("CanContentScroll"));
    }

    [Fact]
    public void TheComparisonReportScrollbarRunsAgainstTheCardsRightEdge()
    {
        var popup = MainWindow().Descendants(Wpf + "Popup").Single(p => (string?)p.Attribute(Xaml + "Name") == "ComparisonDetailsPopup");
        var card = popup.Elements(Wpf + "Border").Single();
        Assert.Equal("12,12,0,12", (string?)card.Attribute("Padding"));
        var text = popup.Descendants(Wpf + "TextBlock").Single(t => (string?)t.Attribute(Xaml + "Name") == "ComparisonDetailsText");
        Assert.Equal("0,0,12,0", (string?)text.Attribute("Margin"));
    }

    private static XDocument MainWindow() => XDocument.Load(Path.Combine(RepoRoot(), "Shell", "MainWindow.xaml"));

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Controls.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
