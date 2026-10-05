using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class ComparisonBarLayoutTests
{
    [Fact]
    public void ComparisonCloseButtonIsClickableAcrossItsWholeBounds()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement style = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Key") == "ComparisonCloseButton");
        XElement template = style.Descendants().Single(element =>
            element.Name.LocalName == "ControlTemplate");
        XElement close = document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ComparisonCloseButtonControl");
        XElement content = close.Elements().Single(element => element.Name.LocalName == "Grid");

        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var button = new Button
                {
                    Width = (double)close.Attribute("Width")!,
                    Height = (double)close.Attribute("Height")!,
                    Background = Brushes.Transparent,
                    Foreground = Brushes.White,
                    Template = (ControlTemplate)XamlReader.Parse(template.ToString()),
                    Content = XamlReader.Parse(content.ToString())
                };
                button.Measure(new Size(button.Width, button.Height));
                button.Arrange(new Rect(0, 0, button.Width, button.Height));
                button.UpdateLayout();

                for (double y = 0.5; y < button.Height; y++)
                for (double horizontal = 0.5; horizontal < button.Width; horizontal++)
                {
                    DependencyObject? hit = VisualTreeHelper.HitTest(button, new Point(horizontal, y))?.VisualHit;
                    while (hit is not null && !ReferenceEquals(hit, button))
                        hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(button, hit);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Comparison close-button layout timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData(648, true)]
    [InlineData(800, true)]
    [InlineData(1000, true)]
    [InlineData(1000, false)]
    public void ComparisonBarKeepsCloseVisibleAndClickable(int width, bool longFileNames)
    {
        var document = XDocument.Load(Path.Combine(FindRepositoryRoot(), "MainWindow.xaml"));
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Grid root = LoadComparisonBar(document);
                var files = (TextBlock)root.FindName("ComparisonFilesText");
                files.Text = longFileNames
                    ? "Original document with a long descriptive filename.pdf  <->  Comparison document with a long descriptive filename.pdf"
                    : "A.pdf  <->  B.pdf";
                ((TextBlock)root.FindName("ComparisonResultText")).Text = "Page 1: 15.25% changed";
                ((FrameworkElement)root.FindName("ComparisonRegionNavigation")).Visibility = Visibility.Visible;
                ((TextBlock)root.FindName("ComparisonRegionText")).Text = "1/20";

                // InputHitTest needs a presentation source. WS_POPUP without WS_VISIBLE
                // creates an isolated source that never displays or activates a window.
                using var presentation = new HwndSource(new HwndSourceParameters("ComparisonBarLayoutTest")
                {
                    WindowStyle = unchecked((int)0x80000000),
                    Width = width,
                    Height = 40
                });
                presentation.RootVisual = root;
                root.Measure(new Size(width, 40));
                root.Arrange(new Rect(0, 0, width, 40));
                root.UpdateLayout();

                var bar = (Border)root.FindName("ComparisonBar");
                var close = (Button)root.FindName("ComparisonCloseButtonControl");
                Point origin = close.TransformToAncestor(bar).Transform(new Point());
                Assert.InRange(origin.X, 0, bar.ActualWidth - close.ActualWidth);
                Assert.InRange(origin.Y, 0, bar.ActualHeight - close.ActualHeight);
                Assert.True(close.IsVisible);

                for (double y = 0.5; y < close.ActualHeight; y++)
                for (double horizontal = 0.5; horizontal < close.ActualWidth; horizontal++)
                {
                    Point point = close.TransformToAncestor(root).Transform(new Point(horizontal, y));
                    DependencyObject? hit = root.InputHitTest(point) as DependencyObject;
                    while (hit is not null && !ReferenceEquals(hit, close))
                        hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(close, hit);
                }

                if (!longFileNames)
                {
                    Grid controls = ((Grid)bar.Child).Children.OfType<Grid>().Single();
                    Point controlsOrigin = controls.TransformToAncestor(bar).Transform(new Point());
                    Assert.True(controls.ActualWidth < bar.ActualWidth);
                    Assert.Equal((bar.ActualWidth - controls.ActualWidth) / 2, controlsOrigin.X, 3);
                    Assert.InRange(Math.Abs(files.ActualWidth - files.DesiredSize.Width), 0, 0.01);
                }
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "Comparison bar layout timed out.");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Grid LoadComparisonBar(XDocument document)
    {
        XNamespace p = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var resources = new XElement(p + "ResourceDictionary",
            new XElement(p + "SolidColorBrush", new XAttribute(x + "Key", "ComparisonBarHoverBrush"),
                new XAttribute("Color", "#26FFFFFF")),
            new XElement(p + "SolidColorBrush", new XAttribute(x + "Key", "GrainBrushShared"),
                new XAttribute("Color", "Transparent")));
        foreach (string key in new[] { "ToolbarButton", "ComparisonBarButton", "OverlayCloseButton", "ComparisonCloseButton" })
            resources.Add(new XElement(document.Descendants().Single(element =>
                (string?)element.Attribute(x + "Key") == key)));

        var bar = new XElement(document.Descendants().Single(element =>
            (string?)element.Attribute(x + "Name") == "ComparisonBar"));
        foreach (XAttribute handler in bar.DescendantsAndSelf().Attributes("Click").ToList())
            handler.Remove();
        bar.SetAttributeValue("Visibility", "Visible");
        bar.SetAttributeValue("Grid.Row", null);
        bar.SetAttributeValue("Grid.ColumnSpan", null);
        var markup = new XElement(p + "Grid", new XAttribute(XNamespace.Xmlns + "x", x),
            new XAttribute("Background", "Transparent"),
            new XElement(p + "Grid.Resources", resources), bar);
        var root = (Grid)XamlReader.Parse(markup.ToString());
        root.Resources["ComparisonBarForegroundBrush"] = Brushes.White;
        root.Resources["ComparisonBarCloseHoverBrush"] = Brushes.Red;
        root.Resources["MenuFontFamily"] = new FontFamily("Segoe UI");
        root.Resources["MenuFontSize"] = 12.0;
        root.Resources["Str_Compare_Bar"] = "COMPARISON";
        return root;
    }

    [Fact]
    public void ComparisonBarUsesReservedBottomRowAndDetailsOpenUpward()
    {
        string root = FindRepositoryRoot();
        var document = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        XElement splitHost = document.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "SplitHost");
        XElement rowDefinitions = splitHost.Elements()
            .Single(element => element.Name.LocalName == "Grid.RowDefinitions");
        Assert.Equal(new[] { "*", "Auto" }, rowDefinitions.Elements()
            .Select(element => (string?)element.Attribute("Height")));

        XElement comparisonBar = splitHost.Elements()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonBar");
        Assert.Equal("1", (string?)comparisonBar.Attribute("Grid.Row"));
        Assert.Equal("Stretch", (string?)comparisonBar.Attribute("HorizontalAlignment"));
        Assert.Null(comparisonBar.Attribute("VerticalAlignment"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("Margin"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("BorderThickness"));
        Assert.Equal("0", (string?)comparisonBar.Attribute("CornerRadius"));
        Assert.DoesNotContain(comparisonBar.Elements(), element => element.Name.LocalName == "Border.Effect");

        Assert.DoesNotContain(splitHost.Elements(),
            element => (string?)element.Attribute(x + "Name") == "ComparisonDividerStem");

        XElement dockContents = comparisonBar.Elements().Single(element => element.Name.LocalName == "Grid");
        XElement accentFace = dockContents.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonAccentFace");
        Assert.Equal("{DynamicResource ComparisonBarBrush}", (string?)accentFace.Attribute("Background"));
        Assert.Equal("{DynamicResource ComparisonBarVerticalMask}",
            (string?)accentFace.Parent?.Attribute("OpacityMask"));
        Assert.Single(comparisonBar.Descendants(), element =>
            element.Name.LocalName == "TextBlock" &&
            (string?)element.Attribute("Text") == "{DynamicResource Str_Compare_Bar}");

        XElement comparisonButtonStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ComparisonBarButton");
        Assert.DoesNotContain(comparisonButtonStyle.Descendants(), element =>
            element.Name.LocalName == "DropShadowEffect");
        XElement comparisonHoverTrigger = comparisonButtonStyle.Descendants()
            .Single(element => element.Name.LocalName == "Trigger" &&
                (string?)element.Attribute("Property") == "IsMouseOver");
        Assert.Contains(comparisonHoverTrigger.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Background" &&
            (string?)element.Attribute("Value") == "{DynamicResource ComparisonBarHoverBrush}");

        XElement comparisonCloseStyle = document.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute(x + "Key") == "ComparisonCloseButton");
        Assert.Equal("{StaticResource OverlayCloseButton}", (string?)comparisonCloseStyle.Attribute("BasedOn"));
        XElement comparisonCloseHover = comparisonCloseStyle.Descendants()
            .Single(element => element.Name.LocalName == "Trigger" &&
                (string?)element.Attribute("Property") == "IsMouseOver");
        Assert.Contains(comparisonCloseHover.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Foreground" &&
            (string?)element.Attribute("Value") == "{DynamicResource ComparisonBarCloseHoverBrush}");
        Assert.DoesNotContain(comparisonCloseHover.Descendants(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Background");

        XElement comparisonClose = comparisonBar.Descendants()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonCloseButtonControl");
        Assert.Equal("{StaticResource ComparisonCloseButton}", (string?)comparisonClose.Attribute("Style"));
        Assert.Equal("10,0,0,0", (string?)comparisonClose.Attribute("Margin"));
        Assert.Contains(comparisonClose.Descendants(), element =>
            element.Name.LocalName == "Path" &&
            (string?)element.Attribute("Data") == "M 1,1 L 10,10 M 10,1 L 1,10");
        Assert.Contains(comparisonClose.Descendants(), element =>
            element.Name.LocalName == "Run" &&
            (string?)element.Attribute("Text") == "{DynamicResource Str_Key_Esc}");

        XElement comparisonTextStyle = comparisonBar.Descendants()
            .Single(element => element.Name.LocalName == "Style" &&
                (string?)element.Attribute("TargetType") == "TextBlock");
        Assert.Contains(comparisonTextStyle.Elements(), element =>
            element.Name.LocalName == "Setter" &&
            (string?)element.Attribute("Property") == "Effect" &&
            (string?)element.Attribute("Value") == "{DynamicResource ComparisonBarTextEffect}");

        Assert.DoesNotContain(comparisonBar.Descendants(), element =>
            (string?)element.Attribute("Foreground") == "White" ||
            (string?)element.Attribute("Foreground") == "{StaticResource ComparisonBarForegroundBrush}");

        string themeManager = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        Assert.Contains("ApplyComparisonBarPalette(liveResources, theme);", themeManager);
        Assert.Contains("case Theme.Dark:", themeManager);
        Assert.Contains("case Theme.Light:", themeManager);
        Assert.Contains("case Theme.Black:", themeManager);
        Assert.Contains("else foreground = Solid(0x24, 0x21, 0x2b);", themeManager);
        Assert.Contains("case Theme.Blood:", themeManager);
        Assert.Contains("case Theme.Greed:", themeManager);
        Assert.Contains("case Theme.Cyanotic:", themeManager);
        Assert.Contains("case Theme.Ectoplasm:", themeManager);
        Assert.Contains("background = resources[\"SelectionBg\"];", themeManager);
        Assert.Contains("object foreground = resources[\"OnPrimaryBrush\"];", themeManager);
        Assert.Contains("resources[\"ComparisonBarCloseHoverBrush\"] = foreground;", themeManager);
        Assert.Contains("resources[\"ComparisonBarHoverBrush\"] =", themeManager);
        Assert.Contains("resources[\"ComparisonBarVerticalMask\"] = BuildComparisonBarVerticalMask(theme);", themeManager);
        Assert.Contains("if (theme == Theme.SE98) return Brushes.Black;", themeManager);

        XElement detailsPopup = splitHost.Elements()
            .Single(element => (string?)element.Attribute(x + "Name") == "ComparisonDetailsPopup");
        Assert.Equal("Top", (string?)detailsPopup.Attribute("Placement"));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }
}
