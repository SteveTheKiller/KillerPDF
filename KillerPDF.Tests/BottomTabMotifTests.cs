using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Xml.Linq;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Trash Polka with tabs below the page: the strip flips each tab with ScaleTransform(1,-1), so the active tab's
/// Window motif must be read from the tab's on-screen bounds and flipped back, or its art comes from the wrong patch,
/// upside down, and breaks at the seam with the pane above. Windowless: layout only.
/// </summary>
public sealed class BottomTabMotifTests
{
    private static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void ActiveTabCarriesThePaneMotifAndInactiveTabsShowTheWindow()
    {
        var viewer = XDocument.Load(Path.Combine(RepoRoot(), "Controls", "Viewer", "PdfViewer.xaml"));
        var pane = viewer.Descendants(Wpf + "Border").Single(e => (string?)e.Attribute(Xaml + "Name") == "PaneBorder");
        Assert.Equal("Window", (string?)pane.Attribute("{clr-namespace:KillerPDF.Controls}ThemeMotif.Part"));
        Assert.Equal("ContentPaneBrush", (string?)pane.Attribute("{clr-namespace:KillerPDF.Controls}ThemeMotif.BaseBrush"));

        var tab = viewer.Descendants().Single(e => (string?)e.Attribute(Xaml + "Name") == "tabBd");
        Assert.Equal("Transparent", (string?)tab.Attribute("Background"));
        Assert.Null(tab.Attribute("{clr-namespace:KillerPDF.Controls}ThemeMotif.Part"));
        Assert.Equal("{DynamicResource TabPlacementTransform}", (string?)tab.Attribute("RenderTransform"));

        var active = viewer.Descendants(Wpf + "DataTrigger").Single(t =>
            (string?)t.Attribute("Binding") == "{Binding IsActive}" && (string?)t.Attribute("Value") == "True"
            && t.Elements(Wpf + "Setter").Any(s => (string?)s.Attribute("Property") == "local:ThemeMotif.Part"));
        string? Set(string property) => active.Elements(Wpf + "Setter").SingleOrDefault(s =>
            (string?)s.Attribute("TargetName") == "tabBd" && (string?)s.Attribute("Property") == property)?.Attribute("Value")?.Value;
        Assert.Equal("Window", Set("local:ThemeMotif.Part"));
        Assert.Equal("ContentPaneBrush", Set("local:ThemeMotif.BaseBrush"));
    }

    [Fact]
    public void FlippedBottomTabReadsTheWindowPatchBelowThePaneUpright()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var root = new Grid { Width = 400, Height = 200 };
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(36) });
                var pane = new Border();
                var tab = new Border { Width = 160, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(20, 0, 0, 0),
                    RenderTransform = new ScaleTransform(1, -1), RenderTransformOrigin = new Point(0.5, 0.5) };
                Grid.SetRow(tab, 1);
                root.Children.Add(pane);
                root.Children.Add(tab);
                root.Measure(new Size(400, 200));
                root.Arrange(new Rect(0, 0, 400, 200));

                var (paneBounds, paneUnflip) = MotifPlacement.PlaceInWindow(pane, root);
                Assert.Null(paneUnflip);
                var (bounds, unflip) = MotifPlacement.PlaceInWindow(tab, root);

                // Anchor: the tab's patch starts where the pane's ends, so the art runs across the boundary.
                Assert.Equal(paneBounds.Bottom, bounds.Top, 3);
                Assert.Equal(new Rect(20, 164, 160, 36), bounds);
                // Upright: a brush point p, flipped back, lands at the tab's on-screen top-left plus p.
                Assert.NotNull(unflip);
                var toRoot = tab.TransformToAncestor(root);
                foreach (var p in new[] { new Point(0, 0), new Point(10, 5), new Point(160, 36) })
                {
                    var screen = toRoot.Transform(unflip!.Transform(p));
                    Assert.Equal(bounds.X + p.X, screen.X, 3);
                    Assert.Equal(bounds.Y + p.Y, screen.Y, 3);
                }

                // Top placement keeps the plain path.
                tab.RenderTransform = Transform.Identity;
                root.UpdateLayout();
                Assert.Null(MotifPlacement.PlaceInWindow(tab, root).Unflip);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Controls", "Viewer", "PdfViewer.xaml"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF repo root");
    }
}
