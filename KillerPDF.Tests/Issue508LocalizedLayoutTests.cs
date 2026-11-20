using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// #508: a German user found labels that repeat their shortcut key, a keyboard-map hint that left out the
// combined layers, and dialogs whose fixed widths cut off longer translations.
public sealed class Issue508LocalizedLayoutTests
{
    private static readonly string StringsDirectory = FindStringsDirectory();
    private static readonly string RepoRoot = Directory.GetParent(StringsDirectory)!.FullName;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    // The toolbar and menus already show the shortcut next to a label, so "Forms (F)" read as "Forms (F)  F".
    [Fact]
    public void ToolbarLabelsDoNotCarryTheirShortcutKeyInParentheses()
    {
        foreach (string file in Directory.GetFiles(StringsDirectory, "*.xaml"))
        {
            foreach (var (key, value) in ReadStrings(file).Where(p => p.Key.StartsWith("Str_Lbl_", StringComparison.Ordinal)))
            {
                Assert.False(Regex.IsMatch(value, @"\(\w\)\s*$"),
                    $"{Path.GetFileName(file)} {key} ends with a shortcut key in parentheses: \"{value}\".");
            }
        }
    }

    // The keyboard map has Ctrl, Ctrl+Shift, Shift, Alt, Alt+Shift and Ctrl+Alt+Shift layers. The hint has to name
    // every one of them with the key names that locale shows on the layer buttons.
    [Fact]
    public void KeyboardHoldHintNamesEveryModifierLayerInEachLocale()
    {
        foreach (string file in Directory.GetFiles(StringsDirectory, "*.xaml"))
        {
            var strings = ReadStrings(file);
            string ctrl = strings["Str_Key_Ctrl"], shift = strings["Str_Key_Shift"], alt = strings["Str_Key_Alt"];
            string hint = strings["Str_KS_HoldHint"];
            string[] layers = [$"{ctrl}+{shift}", $"{alt}+{shift}", $"{ctrl}+{alt}+{shift}"];
            foreach (string layer in layers)
                Assert.True(hint.Contains(layer, StringComparison.Ordinal),
                    $"{Path.GetFileName(file)} Str_KS_HoldHint does not name the {layer} layer: \"{hint}\".");
            foreach (string single in new[] { ctrl, shift, alt })
                Assert.True(Regex.IsMatch(hint, $@"(^|[^+\w]){Regex.Escape(single)}($|[^+\w])"),
                    $"{Path.GetFileName(file)} Str_KS_HoldHint does not name the {single} layer on its own: \"{hint}\".");
        }
    }

    // The hint sat at the end of the layer button row, which never wraps, so long translations ran off the card.
    [Fact]
    public void KeyboardHoldHintWrapsOnItsOwnRow()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "Shell", "KeyboardMapOverlay.cs"));
        Match hint = Regex.Match(source, @"var hint = new TextBlock.*?""Str_KS_HoldHint""", RegexOptions.Singleline);
        Assert.True(hint.Success, "The hold hint TextBlock was not found.");
        Assert.Contains("TextWrapping = TextWrapping.Wrap", hint.Value);
        Assert.DoesNotContain("layerRow.Children.Add(hint", source);
    }

    // Crash dialog buttons had fixed widths, so "Protokolle öffnen" and "Bericht kopieren" were cut off.
    [Fact]
    public void CrashDialogButtonsGrowWithTheirLabel()
    {
        string source = File.ReadAllText(Path.Combine(RepoRoot, "App.xaml.cs"));
        Match maker = Regex.Match(source, @"MakeCrashButton\(string label[^)]*\)\s*\{.*?\n        \}", RegexOptions.Singleline);
        Assert.True(maker.Success, "MakeCrashButton was not found.");
        Assert.DoesNotContain("btn.Width =", maker.Value);
        Assert.Contains("MinWidth", maker.Value);
    }

    private static Dictionary<string, string> ReadStrings(string path) =>
        XDocument.Load(path).Root!.Elements()
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => e.Attribute(Xaml + "Key")!.Value, e => e.Value);

    private static string FindStringsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Strings")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Strings");
    }
}
