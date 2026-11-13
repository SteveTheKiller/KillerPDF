using System.IO;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// Shortcut surfaces that live in the window classes, which need a real window to build. What they
// must do is pinned in the source, the way MenuShortcutDisplayTests does it.
public sealed class ShortcutSurfaceSourceTests
{
    [Fact]
    public void AMovedShortcutRebuildsTheToolbarRightClickMenu()
    {
        string source = ShellSource.Read("Shell", "ShortcutCustomize.cs");
        string refresh = ShellSource.Between(source, "private void RefreshShortcutViews()", "ApplyShortcutTooltips();");

        Assert.Contains("BuildToolbarMenu();", refresh);
    }

    [Fact]
    public void TheBookmarkListDeletesOnTheKeyDeleteWasMovedTo()
    {
        var remap = new ShortcutRemap();
        Assert.Null(remap.Assign(new KsCap(KbLayer.Base, "Del", ""), new KsCap(KbLayer.Base, "Ins", "")));

        // The window's key handler swallows the old key and the bookmark list asks the same translation.
        Assert.Null(remap.Translate(KbLayer.Base, "Del"));
        Assert.Equal<(KbLayer, string)?>((KbLayer.Base, "Del"), remap.Translate(KbLayer.Base, "Ins"));

        string source = ShellSource.Read("Shell", "SidebarOutline.cs");
        string handler = ShellSource.Between(source, "private void OutlineTree_PreviewKeyDown(", "else if (e.Key == Key.F2");
        Assert.Contains("TranslateShortcut(e.Key, Keyboard.Modifiers, e.SystemKey)", handler);
        Assert.DoesNotContain("e.Key == Key.Delete", handler);
        Assert.DoesNotContain("KeyText(\"%del%\")", source);
        Assert.Contains("GestureFor(\"Del\")", source);
    }

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static IEnumerable<(string Locale, Dictionary<string, string> Strings)> Locales()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "KillerPDF.csproj"))) root = root.Parent;
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root!.FullName, "Strings"), "*.xaml").Order(StringComparer.Ordinal))
            yield return (Path.GetFileNameWithoutExtension(file), XDocument.Parse(File.ReadAllText(file)).Root!.Elements()
                .Where(element => element.Attribute(Xaml + "Key") is not null)
                .ToDictionary(element => (string)element.Attribute(Xaml + "Key")!, element => element.Value));
    }

    [Theory]
    [InlineData("SettingsPanel.cs", "Str_St_ToolbarHidden", "Alt:M")]
    [InlineData("SplitPane.cs", "Str_St_SplitOn", "F10")]
    [InlineData("FullScreen.cs", "Str_FullScreen_Hint", "F11")]
    public void AStatusLineThatNamesAMovableKeyIsFormattedWithTheCurrentGesture(string file, string key, string cap)
    {
        string source = ShellSource.Read("Shell", file);

        Assert.Contains($"string.Format(Loc(\"{key}\"), GestureFor(\"{cap}\"))", source);
    }

    // These three statements name keys the user can move, so they carry the current key.
    [Theory]
    [InlineData("Str_St_ToolbarHidden", "Alt+M")]
    [InlineData("Str_St_SplitOn", "F10")]
    [InlineData("Str_FullScreen_Hint", "F11")]
    public void AStatusLineThatNamesAMovableKeyTakesTheKeyAsAPlaceholder(string key, string literal)
    {
        foreach (var (locale, strings) in Locales())
        {
            Assert.True(strings[key].Contains("{0}", StringComparison.Ordinal), $"{locale} {key} has no {{0}}: {strings[key]}");
            Assert.False(strings[key].Contains(literal, StringComparison.Ordinal), $"{locale} {key} still names {literal}: {strings[key]}");
        }
    }
}
