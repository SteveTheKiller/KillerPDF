using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

// A menu row shows the shortcut of the command it runs and no other. Rows that go through MakeMenuItem without a
// gesture borrow the first shortcut whose label has the same text, so a row that runs something else, or a tooltip
// that is built by hand, must say what it shows. The menus need a window to build, so the source is pinned.
public sealed class MenuShortcutDisplayTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Line(string text, string anchor)
    {
        var lines = text.Split('\n').Where(l => l.Contains(anchor, StringComparison.Ordinal)).ToList();
        Assert.True(lines.Count == 1, $"Expected one line with {anchor} but found {lines.Count}");
        return lines[0];
    }

    private static KsBinding Binding(string labelKey)
    {
        var found = ShortcutTable.KsAll.Where(b => b.LabelKey == labelKey).ToList();
        Assert.True(found.Count == 1, $"Expected one shortcut bound to {labelKey} but found {found.Count}");
        return found[0];
    }

    [Fact]
    public void TheCardMergeRowShowsNoShortcutBecauseTheGestureRunsAnotherMerge()
    {
        // Alt+Shift+B merges at the end; the card's tile inserts after its own page, so its tooltip names no shortcut.
        string row = Line(ShellSource.Read("Shell", "PageCard.cs"), "Loc(\"Str_Lbl_Merge\")");
        Assert.Contains("Loc(\"Str_Lbl_Merge\"), null, async () => await MergeAtIndex(page + 1)", row);
    }

    [Fact]
    public void NoSidebarMenuRowBorrowsAnotherCommandsShortcutThroughItsText()
    {
        var english = XDocument.Parse(ShellSource.Read("Strings", "en-US.xaml"))
            .Descendants().Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value);
        string panels = ShellSource.Read("Shell", "SidebarPanels.cs");
        foreach (Match add in Regex.Matches(panels, "Add\\(\"(Str_[A-Za-z0-9_]+)\""))
        {
            string key = add.Groups[1].Value;
            foreach (var binding in ShortcutTable.KsAll)
                if (binding.LabelKey != key && binding.Caps.Length > 0 && english.TryGetValue(binding.LabelKey, out string? text) && text == english[key])
                    Assert.Fail($"The sidebar row {key} reads \"{english[key]}\", the text of {binding.LabelKey}, so the menu would show that command's shortcut on it.");
        }
        // The attachment's Save is a strip button that names no shortcut, so it cannot show Ctrl+S.
        string save = Line(panels, "Loc(\"Str_Btn_Save\")");
        Assert.Contains("Loc(\"Str_Btn_Save\"), null, () => SaveAttachmentCopy(attachment)", save);
    }

    [Fact]
    public void TheExtractTileTooltipNamesItsShortcutFromTheKeyboardMap()
    {
        string tile = Line(ShellSource.Read("Shell", "PageCard.cs"), "Loc(\"Str_Ctx_ExtractPages\")");
        var cap = Binding("Str_Ctx_ExtractPages").Caps[0];
        Assert.Contains($"\"{cap.Layer}:{cap.Id}\"", tile);
    }

    [Fact]
    public void TheCompareRootRowShowsItsShortcutFromTheKeyboardMap()
    {
        string compare = ShellSource.Between(ShellSource.Read("Shell", "PdfComparison.cs"), "private MenuItem BuildComparisonContextItem()", "PdfViewer.ComparisonDocument?");
        var cap = Binding("Str_TT_ComparePDFs").Caps[0];
        Assert.Contains($"InputGestureText = GestureFor(\"{cap.Layer}:{cap.Id}\")", compare);
    }
}
