using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Input;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Small shortcut gaps: keys and menus that disagreed with the buttons they mirror when a book is open, a text bar
// that named no keys, a status line that named the wrong one, and menu rows that borrowed another command's key.
// The shell needs a window to run, so where only the source can be reached it is pinned.
public sealed class BookShortcutGateTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Theory]
    [InlineData(Key.D1)]
    [InlineData(Key.D6)]
    public void AltDigitRunsForABookWhenItsRailButtonIsEnabled(Key key)
        => Assert.True(ExpandedShortcutGate.Runs(hasPdf: false, hasReadingDocument: true, railButtonEnabled: true, key, ModifierKeys.Alt));

    [Fact]
    public void AltDigitStaysInertForABookWhenItsRailButtonIsDisabled()
        => Assert.False(ExpandedShortcutGate.Runs(false, true, false, Key.D2, ModifierKeys.Alt));

    [Fact]
    public void AltDigitStaysInertWithNothingOpen()
        => Assert.False(ExpandedShortcutGate.Runs(false, false, true, Key.D1, ModifierKeys.Alt));

    [Fact]
    public void OnlyThePlainAltDigitChordsReachABook()
    {
        Assert.False(ExpandedShortcutGate.Runs(false, true, true, Key.P, ModifierKeys.Alt | ModifierKeys.Shift));
        Assert.False(ExpandedShortcutGate.Runs(false, true, true, Key.D1, ModifierKeys.Alt | ModifierKeys.Shift));
        Assert.False(ExpandedShortcutGate.Runs(false, true, true, Key.D1, ModifierKeys.Alt | ModifierKeys.Control));
    }

    [Fact]
    public void APdfAndTheNoDocumentChordsBehaveAsBefore()
    {
        Assert.True(ExpandedShortcutGate.Runs(true, true, false, Key.D2, ModifierKeys.Alt));
        Assert.True(ExpandedShortcutGate.Runs(true, false, false, Key.P, ModifierKeys.Alt | ModifierKeys.Shift));
        Assert.True(ExpandedShortcutGate.Runs(false, false, false, Key.W, ModifierKeys.Alt | ModifierKeys.Shift));
        Assert.True(ExpandedShortcutGate.Runs(false, false, false, Key.N, ModifierKeys.Alt | ModifierKeys.Shift));
    }

    [Fact]
    public void TheExpandedShortcutHandlerAsksTheGateAndFollowsTheRailButton()
    {
        string source = ShellSource.Read("Shell", "ExpandedShortcuts.cs");
        Assert.Contains("ExpandedShortcutGate.Runs(", source);
        Assert.Contains("RailPanelButtons().First(", source);
        Assert.Contains(".Button.IsEnabled", source);
    }

    [Fact]
    public void TheBoldItalicAndUnderlineButtonsNameTheirKeysAndStrikethroughNamesNone()
    {
        string bar = ShellSource.Read("Shell", "TextSettingsBar.cs");
        string toggles = ShellSource.Between(bar, "bar.Add(Controls.ToolOptionGroup.Style", "// Size:");
        string[] calls = toggles.Split("StyleToggle(", StringSplitOptions.None);
        foreach (var (label, cap) in new[] { ("Str_Lbl_Bold", "Ctrl:B"), ("Str_Lbl_Italic", "Ctrl:I"), ("Str_Lbl_Underline", "Ctrl:U") })
        {
            string call = calls.SingleOrDefault(c => c.Contains("\"" + label + "\"")) ?? "";
            Assert.True(call.Length > 0, "No StyleToggle call for " + label);
            Assert.Contains("\"" + cap + "\"", call);
        }
        string strike = calls.SingleOrDefault(c => c.Contains("\"Str_Lbl_Strike\"")) ?? "";
        Assert.True(strike.Length > 0);
        Assert.DoesNotContain("\"Ctrl:", strike);
        Assert.Contains("ShortcutTip(tipKey, cap)", bar);
    }

    [Fact]
    public void EveryLocaleStatusLineNamesCtrlEnterAsTheSaveKey()
    {
        string root = Path.GetDirectoryName(RepoFile("KillerPDF.csproj"))!;
        var files = Directory.EnumerateFiles(Path.Combine(root, "Strings"), "*.xaml").ToList();
        Assert.Equal(22, files.Count);
        foreach (string file in files)
        {
            var strings = XDocument.Parse(File.ReadAllText(file)).Descendants()
                .Where(e => e.Attribute(Xaml + "Key") is not null)
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value);
            string ctrl = strings["Str_Key_Ctrl"];
            string enter = strings["Str_Key_Enter"];
            string status = strings["Str_St_EditingText"];
            Assert.True(status.Contains(ctrl + "+" + enter, StringComparison.Ordinal),
                $"{Path.GetFileName(file)}: Str_St_EditingText does not name {ctrl}+{enter}: {status}");
            Assert.True(status.IndexOfAny([(char)0x2013, (char)0x2014]) < 0, $"{Path.GetFileName(file)}: the status line has a long dash");
        }
    }

    [Fact]
    public void ThePageSubmenuAndTheMeasurementsRowPassAnExplicitGestureInsteadOfBorrowingOne()
    {
        string pageTools = Regex.Match(ShellSource.Read("Shell", "ContextMenu.cs"), "var tools = MakeMenuItem\\(Loc\\(\"Str_Card_PageTools\"\\)[^\\n]*").Value;
        Assert.Contains("gesture: \"\"", pageTools);
        string panels = ShellSource.Read("Shell", "SidebarPanels.cs");
        string measurements = Regex.Match(panels, "[^\\n]*Str_DocumentTools_Measurements\"[^\\n]*\\n[^\\n]*").Value;
        Assert.Contains("MakeMenuItem(Loc(\"Str_DocumentTools_Measurements\")", measurements);
        Assert.Contains("ShortcutTable.DocumentToolKey(\"Str_DocumentTools_Measurements\")", measurements);
    }

    [Fact]
    public void EveryMenuRowWhoseTextMatchesAnotherCommandInSomeLanguageHasAnExplicitGesture()
    {
        string root = Path.GetDirectoryName(RepoFile("KillerPDF.csproj"))!;
        string[] rows = ["Str_Card_PageTools", "Str_DocumentTools_Measurements", "Str_Btn_Save"];
        var collisions = new List<string>();
        foreach (string file in Directory.EnumerateFiles(Path.Combine(root, "Strings"), "*.xaml"))
        {
            var strings = XDocument.Parse(File.ReadAllText(file)).Descendants()
                .Where(e => e.Attribute(Xaml + "Key") is not null)
                .ToDictionary(e => (string)e.Attribute(Xaml + "Key")!, e => e.Value);
            foreach (string row in rows)
                foreach (var binding in ShortcutTable.KsAll)
                    if (binding.LabelKey != row && binding.Caps.Length > 0 && strings.TryGetValue(binding.LabelKey, out string? text) && text == strings[row])
                        collisions.Add($"{Path.GetFileName(file)}: {row} reads like {binding.LabelKey}");
        }
        Assert.NotEmpty(collisions);   // the collisions are real, which is why the rows must not rely on the text lookup
        string menus = ShellSource.Read("Shell", "ContextMenu.cs") + ShellSource.Read("Shell", "SidebarPanels.cs");
        Assert.DoesNotContain("Add(\"Str_DocumentTools_Measurements\"", menus);
        Assert.DoesNotContain("Add(\"Str_Card_PageTools\"", menus);
    }

    [Fact]
    public void OpenFirstIsQuietOnlyForABookWithNoPdf()
    {
        Assert.False(OpenFirstPrompt.Shows(hasPdf: false, hasBook: true));
        Assert.True(OpenFirstPrompt.Shows(hasPdf: false, hasBook: false));
        Assert.True(OpenFirstPrompt.Shows(hasPdf: true, hasBook: false));
    }

    [Fact]
    public void SaveSaveAsAndPrintAskTheOpenFirstRuleBeforeTheirDialog()
    {
        string source = ShellSource.Read("Shell", "FileOperations.cs");
        foreach (string head in new[] { "private void SaveInPlace(bool otherPaneConfirmed","internal void SaveAsWithName(", "private void Print_Click(" })
        {
            string body = ShellSource.Between(source, head, "return;");
            Assert.Contains("OpenFirstPrompt.Shows(", body);
            Assert.Contains("NativeDocumentRef is not null", body);
        }
    }

    [Fact]
    public void SearchToolsAdmitsOnlyReadingCommandsForABook()
    {
        Assert.True(ToolSearchReadingCommands.Admits(true, "Str_View_TwoPage"));
        Assert.True(ToolSearchReadingCommands.Admits(true, "Str_Rail_Pages"));
        Assert.False(ToolSearchReadingCommands.Admits(true, "Str_Lbl_Highlight"));
        Assert.False(ToolSearchReadingCommands.Admits(false, "Str_View_TwoPage"));
    }

    [Fact]
    public void EveryReadingCommandIsARealNavigationFileOrViewCommand()
    {
        Assert.NotEmpty(ToolSearchReadingCommands.Labels);
        foreach (string label in ToolSearchReadingCommands.Labels)
        {
            var bindings = ShortcutTable.KsAll.Where(b => b.LabelKey == label || b.Caps.Any(c => c.LabelKey == label)).ToList();
            Assert.True(bindings.Count > 0, label + " is not in the shortcut table");
            Assert.All(bindings, b => Assert.DoesNotContain(b.Cat, new[] { "Tools", "Edit" }));
        }
        string search = ShellSource.Read("Shell", "ToolSearch.cs");
        Assert.Contains("ToolSearchReadingCommands.Admits(HasReadingDocument, labelKey)", search);
    }

    [Fact]
    public void SearchToolsLeavesOutCutBesideCopyAndPaste()
    {
        string search = ShellSource.Read("Shell", "ToolSearch.cs");
        string skipped = ShellSource.Between(search, "ToolSearchSkippedLabels =", "];");
        Assert.Contains("\"Str_KS_CopyText\"", skipped);
        Assert.Contains("\"Str_KS_Paste\"", skipped);
        Assert.Contains("\"Str_Ctx_Cut\"", skipped);
    }

    private static string RepoFile(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, name))) directory = directory.Parent;
        return Path.Combine(directory!.FullName, name);
    }
}
