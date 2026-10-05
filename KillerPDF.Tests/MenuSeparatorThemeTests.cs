using System;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class MenuSeparatorThemeTests
{
    private static readonly XNamespace Presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void DeliriumOwnsGrayMenuDividersWithoutRecoloringCardBorders()
    {
        string root = FindRepositoryRoot();
        Assert.Equal("#666666", BrushColor(root, "Delirium", "MenuSeparatorBrush"));
        Assert.Equal("#8e101c", BrushColor(root, "Delirium", "CardBorderBrush"));
        Assert.Equal("#5c527d", BrushColor(root, "Delirium", "MenuBorderBrush"));
        Assert.Equal("#9c630e", BrushColor(root, "Delirium", "MenuHoverBrush"));
    }

    [Fact]
    public void OtherThemesKeepTheirCardBorderFallback()
    {
        string root = FindRepositoryRoot();
        string manager = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        Assert.Contains("Alias(\"MenuSeparatorBrush\", \"CardBorderBrush\");", manager);
        foreach (string themePath in Directory.EnumerateFiles(Path.Combine(root, "Themes"), "*.xaml"))
        {
            if (Path.GetFileNameWithoutExtension(themePath) == "Delirium")
                continue;
            Assert.DoesNotContain(XDocument.Load(themePath).Descendants(),
                element => (string?)element.Attribute(Xaml + "Key") == "MenuSeparatorBrush");
        }
        foreach (string accentPath in Directory.EnumerateFiles(Path.Combine(root, "Themes", "Accents"), "*.xaml", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain(XDocument.Load(accentPath).Descendants(),
                element => (string?)element.Attribute(Xaml + "Key") is "CardBorderBrush" or "MenuSeparatorBrush");
        }
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    [InlineData("98SE")]
    public void ExistingMenuAndDialogSeparatorsFollowResourceChanges(string nextTheme)
    {
        RunSta(() =>
        {
            string root = FindRepositoryRoot();
            var source = XDocument.Load(Path.Combine(root, "MainWindow.xaml"));
            var styles = source.Descendants(Presentation + "Style")
                .Where(element => (string?)element.Attribute("TargetType") == "Separator")
                .Select(element => new XElement(element)).ToArray();
            Assert.Equal(2, styles.Length);
            var dictionaryXml = new XElement(Presentation + "ResourceDictionary",
                new XAttribute(XNamespace.Xmlns + "x", Xaml.NamespaceName), styles);
            var resources = (ResourceDictionary)XamlReader.Parse(dictionaryXml.ToString());
            var menuSeparator = new Separator
            {
                Style = (Style)resources[MenuItem.SeparatorStyleKey]
            };
            var dialogSeparator = new Separator
            {
                Style = (Style)resources[typeof(Separator)]
            };
            var gray = (Brush)new BrushConverter().ConvertFromString(BrushColor(root, "Delirium", "MenuSeparatorBrush"))!;
            menuSeparator.Resources["MenuSeparatorBrush"] = gray;
            dialogSeparator.Resources["MenuSeparatorBrush"] = gray;
            menuSeparator.ApplyTemplate();
            var line = Assert.IsType<Border>(VisualTreeHelper.GetChild(menuSeparator, 0));
            Assert.Same(gray, line.Background);
            Assert.Same(gray, dialogSeparator.Background);

            var restored = (Brush)new BrushConverter().ConvertFromString(BrushColor(root, nextTheme, "CardBorderBrush"))!;
            menuSeparator.Resources["MenuSeparatorBrush"] = restored;
            dialogSeparator.Resources["MenuSeparatorBrush"] = restored;
            Assert.Same(restored, line.Background);
            Assert.Same(restored, dialogSeparator.Background);
            Assert.Same(line, VisualTreeHelper.GetChild(menuSeparator, 0));
        });
    }

    private static string BrushColor(string root, string theme, string key) =>
        XDocument.Load(Path.Combine(root, "Themes", theme + ".xaml")).Descendants()
            .Single(element => (string?)element.Attribute(Xaml + "Key") == key)
            .Attribute("Color")!.Value;

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
            ExceptionDispatchInfo.Capture(error).Throw();
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
