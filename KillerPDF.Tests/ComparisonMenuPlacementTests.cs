using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// Alt+Shift+V passed the window as the menu's anchor, so the Compare PDFs menu hung off the window's bottom edge: off
// the bottom of the screen when maximized. The shortcut anchors to the toolbar button, and a button hidden in the More
// menu gives way to the middle of the page.
public sealed class ComparisonMenuPlacementTests
{
    private static string Read(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine([dir!.FullName, .. parts]));
    }

    [Fact]
    public void TheShortcutOpensTheMenuAtTheToolbarButtonOrThePage()
    {
        Assert.Contains("Key.V => () => ComparePdf_Click(ComparePdfBtn, new RoutedEventArgs()),", Read("Shell", "ExpandedShortcuts.cs"));
        string menu = Read("Shell", "PdfComparison.cs");
        Assert.DoesNotContain("sender as FrameworkElement ?? ComparePdfBtn", menu);
        Assert.Contains("if (!anchor.IsVisible)", menu);
    }
}
