using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Trash Polka: the footer sits under the content grid, which carries the window's Chrome ink. A footer that only
/// paints FooterBackgroundBrush cuts the hexagons and dots off at its top edge and reads as a flat black bar, so it
/// must join the same Chrome piece on the same base color, pinned to the window, to read as one surface.
/// </summary>
public sealed class FooterMotifTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XNamespace Ctrl = "clr-namespace:KillerPDF.Controls";

    [Fact]
    public void FooterCarriesTheSameChromeInkAsTheContentAboveIt()
    {
        var main = XDocument.Load(Path.Combine(RepoRoot(), "Shell", "MainWindow.xaml"));
        var footer = main.Descendants(Wpf + "Border").Single(e => (string?)e.Attribute(Xaml + "Name") == "FooterBorder");
        var content = main.Descendants(Wpf + "Grid").Single(e => (string?)e.Attribute(Xaml + "Name") == "MainContentGrid");

        Assert.Equal("Chrome", (string?)content.Attribute(Ctrl + "ThemeMotif.Part"));
        Assert.Equal((string?)content.Attribute(Ctrl + "ThemeMotif.Part"), (string?)footer.Attribute(Ctrl + "ThemeMotif.Part"));
        // The footer restores its own brush under every other theme.
        Assert.Equal("FooterBackgroundBrush", (string?)footer.Attribute(Ctrl + "ThemeMotif.BaseBrush"));
    }

    [Fact]
    public void TrashPolkaFooterBaseMatchesTheContentBase()
    {
        var theme = XDocument.Load(Path.Combine(RepoRoot(), "Themes", "TrashPolka.xaml"));
        string Color(string key) => (string)theme.Descendants(Wpf + "SolidColorBrush")
            .Single(e => (string?)e.Attribute(Xaml + "Key") == key).Attribute("Color")!;
        static string Opaque(string c) => c.Length == 9 ? "#" + c[3..] : c;

        Assert.Equal(Opaque(Color("BackgroundBrush")).ToUpperInvariant(), Opaque(Color("FooterBackgroundBrush")).ToUpperInvariant());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Shell", "MainWindow.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
