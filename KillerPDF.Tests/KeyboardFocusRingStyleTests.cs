using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class KeyboardFocusRingStyleTests
{
    // A style that sets FocusVisualStyle to null switches the family keyboard ring off for every
    // control that uses it (the toolbar buttons, the rail, the folder tree, check rows and the danger
    // close button) once the style is applied again, as it is on a theme change. WPF only draws
    // FocusVisualStyle for keyboard focus, so leaving it to the family ring keeps clicks ring-free.
    [Fact]
    public void NoSharedStyleSwitchesTheKeyboardFocusRingOff()
    {
        var xaml = XDocument.Load(Path.Combine(RepositoryRoot(), "Controls", "Controls.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var off = xaml.Descendants()
            .Where(e => e.Name.LocalName == "Setter" && (string?)e.Attribute("Property") == "FocusVisualStyle"
                && (string?)e.Attribute("Value") == "{x:Null}")
            .Select(e => (string?)e.Parent?.Attribute(x + "Key") ?? "(implicit)")
            .ToList();
        Assert.Empty(off);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
