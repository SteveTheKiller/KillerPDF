using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using KillerFamily.Layout;
using Xunit;

namespace KillerPDF.Tests;

public sealed class TabPlacementTests
{
    [Fact]
    public void BottomTabFaceFlipsWhileItsLabelAndCloseTargetStayUpright()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            Window? window = null;
            try
            {
                var markup = new XElement(Named(ViewerMarkup(), "tabBd"));
                string[] events = ["MouseDown", "MouseRightButtonUp", "PreviewMouseLeftButtonDown",
                    "PreviewMouseMove", "PreviewMouseLeftButtonUp", "Click", "MouseEnter", "MouseLeave", "SizeChanged"];
                foreach (var attribute in markup.DescendantsAndSelf().Attributes().Where(item => events.Contains(item.Name.LocalName)).ToList())
                    attribute.Remove();
                // The face is the app's RetroTabBorder; this assembly reads it as the Border it derives from.
                markup.Name = XName.Get("Border", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                markup.Attribute("RetroBottomEdges")?.Remove();
                markup.Attribute("DividerBrush")?.Remove();
                var context = new ParserContext();
                context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
                var face = (Border)XamlReader.Parse(markup.ToString(), context);
                face.DataContext = new { TabLabel = "Example.pdf", TabTip = "Example.pdf" };
                face.Width = 200;
                face.Height = 36;
                var host = new Grid { Width = 220, Height = 48 };
                host.Children.Add(face);
                window = new Window { Content = host, Width = 240, Height = 80,
                    Opacity = 0, ShowInTaskbar = false, ShowActivated = false };
                window.Show();
                foreach (bool bottom in new[] { false, true, false })
                {
                    host.Resources["TabPlacementTransform"] = bottom ? new ScaleTransform(1, -1) : Transform.Identity;
                    host.Measure(new Size(220, 48));
                    host.Arrange(new Rect(0, 0, 220, 48));
                    host.UpdateLayout();
                    Assert.Equal(bottom, face.TranslatePoint(new Point(), host).Y
                        > face.TranslatePoint(new Point(0, face.ActualHeight), host).Y);
                    var contents = (DockPanel)((Grid)face.Child).Children.OfType<DockPanel>().Single();
                    foreach (var item in contents.Children.OfType<FrameworkElement>())
                        Assert.True(item.TranslatePoint(new Point(0, item.ActualHeight), host).Y
                            > item.TranslatePoint(new Point(), host).Y);
                    var close = contents.Children.OfType<Button>().Single();
                    var hit = host.InputHitTest(close.TranslatePoint(new Point(close.ActualWidth / 2, close.ActualHeight / 2), host))
                        as DependencyObject;
                    while (hit != null && !ReferenceEquals(hit, close)) hit = VisualTreeHelper.GetParent(hit);
                    Assert.Same(close, hit);
                }
            }
            catch (Exception error) { failure = error; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Tab geometry check timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Fact]
    public void AboutPreferenceRemovesMenuShortcutSpaceAndRestoresTheSavedChoice()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var document = XDocument.Load(Path.Combine(Root(), "Shell", "MainWindow.xaml"));
                var menuStyle = document.Descendants().Single(element => element.Name.LocalName == "Style"
                    && (string?)element.Attribute("TargetType") == "MenuItem"
                    && element.Attribute(XName.Get("Key", "http://schemas.microsoft.com/winfx/2006/xaml")) == null
                    && element.Parent?.Parent?.Name.LocalName == "Window.Resources");
                var optionMarkup = new XElement(document.Descendants().Single(element => element.Name.LocalName == "CheckBox"
                    && ((string?)element.Attribute("IsChecked"))?.Contains("ShortcutHints.Current") == true));
                optionMarkup.Attribute("Style")?.Remove();
                var context = new ParserContext();
                context.XmlnsDictionary.Add("", "http://schemas.microsoft.com/winfx/2006/xaml/presentation");
                context.XmlnsDictionary.Add("x", "http://schemas.microsoft.com/winfx/2006/xaml");
                context.XmlnsDictionary.Add("kl", "clr-namespace:KillerFamily.Layout;assembly=KillerPDF.Tests");
                var saved = new Dictionary<string, string>();
                string? Read(string key) => saved.TryGetValue(key, out string? value) ? value : null;
                ShortcutHints.Initialize(Read, (key, value) => saved[key] = value);
                var option = (CheckBox)XamlReader.Parse(optionMarkup.ToString(), context);
                var item = new MenuItem { Header = "Open", InputGestureText = "Ctrl+O",
                    Style = (Style)XamlReader.Parse(menuStyle.ToString(), context) };
                item.ApplyTemplate();
                var gesture = (TextBlock)item.Template.FindName("Gesture", item);
                Assert.True(option.IsChecked);
                Assert.Equal(Visibility.Visible, gesture.Visibility);
                item.Measure(new Size(1000, 1000));
                double shownWidth = item.DesiredSize.Width;
                option.SetCurrentValue(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty, false);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.Equal(Visibility.Collapsed, gesture.Visibility);
                Assert.Equal("0", saved[ShortcutHints.SettingName]);
                item.Measure(new Size(1000, 1000));
                Assert.True(shownWidth > item.DesiredSize.Width + 24);
                saved[ShortcutHints.SettingName] = "1";
                ShortcutHints.Initialize(Read, (key, value) => saved[key] = value);
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.True(option.IsChecked);
                Assert.Equal(Visibility.Visible, gesture.Visibility);
                Assert.Equal("Ctrl+O", item.InputGestureText);
            }
            catch (Exception error) { failure = error; }
            finally { ShortcutHints.Initialize(_ => null, (_, _) => { }); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(15)), "Menu preference runtime check timed out.");
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    [Theory]
    [InlineData("Str_Layout_TabsTop", "Up")]
    [InlineData("Str_Layout_TabsBottom", "Down")]
    public void PlacementIsAvailableInTheShortcutListAndMap(string label, string key)
    {
        var binding = Assert.Single(ShortcutTable.KsAll, item => item.LabelKey == label);
        Assert.Contains(binding.Caps, cap => cap.Layer == KbLayer.AltShift && cap.Id == key);
    }

    [Fact]
    public void TabLabelsKeepOneSizeAndWeightWhenActivated()
    {
        var document = ViewerMarkup();
        var label = Named(document, "tabLbl");
        Assert.Equal("{DynamicResource MonoFont}", (string?)label.Attribute("FontFamily"));
        Assert.Equal("{DynamicResource TabTitleFontSize}", (string?)label.Attribute("FontSize"));
        Assert.DoesNotContain(document.Descendants(), item => item.Name.LocalName == "Setter"
            && (string?)item.Attribute("TargetName") == "tabLbl"
            && (string?)item.Attribute("Property") is "FontSize" or "FontWeight");
        Assert.Equal("160", (string?)Named(document, "tabBd").Attribute("MinWidth"));
        Assert.Equal("165", (string?)Named(document, "tabBd").Attribute("MaxWidth"));
    }

    [Fact]
    public void NewTabUsesTheMenuButtonHoverAndAnUnscaledVectorPlus()
    {
        var button = Named(ViewerMarkup(), "NewTabBtn");
        Assert.Equal("{DynamicResource ToolbarButton}", (string?)button.Attribute("Style"));
        Assert.Equal("NewTab_Click", (string?)button.Attribute("Click"));
        Assert.Equal("{DynamicResource Str_TT_NewBlank}", (string?)button.Attribute("ToolTip"));
        var path = Assert.Single(button.Elements(), item => item.Name.LocalName == "Path");
        Assert.Equal("11", (string?)path.Attribute("Width"));
        Assert.Equal("11", (string?)path.Attribute("Height"));
        Assert.Equal("None", (string?)path.Attribute("Stretch"));
    }

    [Fact]
    public void BlankBandAndItsButtonsShareThePlacementMenu()
    {
        var document = ViewerMarkup();
        var strip = Named(document, "TabStripBorder");
        Assert.Contains(strip.Elements(), item => item.Name.LocalName == "Border.ContextMenu"
            && item.Elements().Any(menu => (string?)menu.Attribute("Opened") == "TabPlacementMenu_Opened"));
        var grid = strip.Parent!;
        Assert.Equal(3, grid.Elements().Single(item => item.Name.LocalName == "Grid.RowDefinitions").Elements().Count());
        Assert.Equal("1", (string?)Named(document, "CardRow").Attribute(XName.Get("Row", "http://schemas.microsoft.com/winfx/2006/xaml/presentation"))
            ?? (string?)Named(document, "CardRow").Attribute("Grid.Row"));
    }

    private static XElement Named(XDocument document, string name) => document.Descendants().Single(item =>
        (string?)item.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml")) == name);

    private static XDocument ViewerMarkup() => XDocument.Load(Path.Combine(Root(), "Controls", "Viewer", "PdfViewer.xaml"));

    private static string Root()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
        throw new DirectoryNotFoundException("Could not locate the KillerPDF repository root.");
    }

}
