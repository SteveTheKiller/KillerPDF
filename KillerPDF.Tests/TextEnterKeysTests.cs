using System.IO;
using System.Windows.Input;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #486: the text tool's line and save keys can be chosen, and clicking away from a box saves it.
public sealed class TextEnterKeysTests
{
    [Theory]
    // Default: Enter adds a line, Ctrl+Enter saves.
    [InlineData(ModifierKeys.None, false, "NewLine")]
    [InlineData(ModifierKeys.Shift, false, "NewLine")]
    [InlineData(ModifierKeys.Control, false, "Commit")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, false, "Commit")]
    // Enter saves: Shift+Enter adds a line, Ctrl+Enter still saves.
    [InlineData(ModifierKeys.None, true, "Commit")]
    [InlineData(ModifierKeys.Shift, true, "NewLine")]
    [InlineData(ModifierKeys.Control, true, "Commit")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Shift, true, "Commit")]
    // AltGr arrives as Ctrl+Alt and types on some layouts, so it is never a save or a line.
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, false, "None")]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt, true, "None")]
    [InlineData(ModifierKeys.Alt, true, "None")]
    public void EnterFollowsTheChosenMode(ModifierKeys modifiers, bool enterCommits, string expected)
    {
        Assert.Equal(Enum.Parse<TextEnterAction>(expected), TextEnterKeys.Decide(Key.Enter, modifiers, enterCommits));
    }

    [Theory]
    [InlineData(Key.ImeProcessed)]
    [InlineData(Key.A)]
    [InlineData(Key.Tab)]
    [InlineData(Key.Escape)]
    public void OnlyEnterItselfIsALineOrSaveKey(Key key)
    {
        Assert.Equal(TextEnterAction.None, TextEnterKeys.Decide(key, ModifierKeys.None, enterCommits: true));
        Assert.Equal(TextEnterAction.None, TextEnterKeys.Decide(key, ModifierKeys.Control, enterCommits: false));
    }

    // The old policy and the new decision agree on the default, so the box's other callers see no change.
    [Theory]
    [InlineData(ModifierKeys.None)]
    [InlineData(ModifierKeys.Shift)]
    [InlineData(ModifierKeys.Control)]
    [InlineData(ModifierKeys.Control | ModifierKeys.Alt)]
    public void TheDefaultModeMatchesTheExistingCommitPolicy(ModifierKeys modifiers)
    {
        Assert.Equal(EditableTextShortcutPolicy.CommitsTextBox(Key.Enter, modifiers),
            TextEnterKeys.Decide(Key.Enter, modifiers, enterCommits: false) == TextEnterAction.Commit);
    }

    [Fact]
    public void TheChoiceIsSavedAndAnnounced()
    {
        string? stored = null;
        int changes = 0;
        var store = new TextEnterModeStore(() => stored, value => stored = value);
        store.Changed += () => changes++;

        Assert.False(store.EnterCommits);
        store.Set(true);
        Assert.True(store.EnterCommits);
        Assert.Equal("1", stored);
        store.Set(true);
        Assert.Equal(1, changes);
        store.Set(false);
        Assert.Equal("0", stored);
        Assert.Equal(2, changes);

        Assert.True(new TextEnterModeStore(() => "1", _ => { }).EnterCommits);
    }

    [Theory]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    public void LeavingABoxSavesTypedText(bool hasText, bool cover, bool direct, bool expected)
    {
        Assert.Equal(expected, TextEnterKeys.CommitsOnLeave(hasText, cover, direct));
    }

    [Fact]
    public void APressOutsideTheEditLeavesIt()
    {
        Assert.True(TextEnterKeys.PressLeavesEdit(false, false, false, false, false, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(onBox: true, false, false, false, false, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(false, onResizeCorner: true, false, false, false, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(false, false, onOptionsBar: true, false, false, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(false, false, false, inPopup: true, false, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(false, false, false, false, optionsDropDownOpen: true, false));
        Assert.False(TextEnterKeys.PressLeavesEdit(false, false, false, false, false, resizing: true));
    }

    // A Text-tool click away from box A saves A and opens box B in the same press. A's LostFocus
    // commit runs after that and used to save whatever box was open, which closed B at once.
    [Fact]
    public void ALateCommitOnlySavesTheBoxThatLostFocus()
    {
        object first = new(), second = new();
        Assert.True(TextEnterKeys.LateCommitStillApplies(first, first));
        Assert.False(TextEnterKeys.LateCommitStillApplies(second, first));
        Assert.False(TextEnterKeys.LateCommitStillApplies(null, first));
    }

    [Fact]
    public void TheLostFocusCommitIsBoundToItsBox()
    {
        string editing = ShellSource.Read("Controls", "Viewer", "PdfViewer.TextEditing.cs");
        string lostFocus = ShellSource.Between(editing, "private void TextBox_LostFocus", "private static bool ContainsOpenComboBox");
        Assert.Contains("TextEnterKeys.LateCommitStillApplies(_activeTextBox, sender)", lostFocus, StringComparison.Ordinal);
        Assert.Contains("TextEnterKeys.CommitsOnLeave(", lostFocus, StringComparison.Ordinal);
    }

    // Clicks that move no keyboard focus (the page in the Select tool, scroll bars, toolbar buttons
    // that take no focus) never raised LostFocus, so the box stayed open and a later Escape threw
    // the typing away. Every box listens for a press anywhere in its window instead.
    [Fact]
    public void EveryEditorListensForAPressAnywhereInItsWindow()
    {
        string editing = ShellSource.Read("Controls", "Viewer", "PdfViewer.TextEditing.cs");
        int boxes = editing.Split("LostFocus += TextBox_LostFocus;").Length - 1;
        int hooks = editing.Split("ListenForClickAway();").Length - 1;
        Assert.Equal(4, boxes);
        Assert.Equal(boxes, hooks);
        string handler = ShellSource.Between(editing, "private void Window_PreviewMouseDownWhileEditing", "// End of click-away.");
        Assert.Contains("TextEnterKeys.PressLeavesEdit(", handler, StringComparison.Ordinal);
        Assert.Contains("CommitActiveTextBox();", handler, StringComparison.Ordinal);
        Assert.DoesNotContain("CancelActiveTextEdit", handler, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEditorKeysGoThroughTheChosenMode()
    {
        string editing = ShellSource.Read("Controls", "Viewer", "PdfViewer.TextEditing.cs");
        string keys = ShellSource.Between(editing, "private void TextBox_PreviewKeyDown", "private void CancelActiveTextEdit");
        Assert.Contains("TextEnterKeys.Decide(e.Key, Keyboard.Modifiers, TextEnterMode.EnterCommits)", keys, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSaveRowFollowsTheMode()
    {
        KsBinding standard = Assert.Single(ShortcutTable.Rows(enterCommits: false), b => b.LabelKey.StartsWith("Str_KS_CommitText", StringComparison.Ordinal));
        KsBinding enter = Assert.Single(ShortcutTable.Rows(enterCommits: true), b => b.LabelKey.StartsWith("Str_KS_CommitText", StringComparison.Ordinal));

        Assert.Equal("%ctrl%+%enter%", standard.Keys);
        Assert.Equal("Str_KS_CommitText", standard.LabelKey);
        Assert.Equal("%enter% / %ctrl%+%enter%", enter.Keys);
        Assert.Equal("Str_KS_CommitTextEnter", enter.LabelKey);
        Assert.Equal("Edit", enter.Cat);
        // Ctrl+Enter saves either way; base Enter and Shift+Enter belong to other rows.
        Assert.Equal([ShortcutTable.Cap("Ctrl:Enter")], enter.Caps);
        Assert.Equal(ShortcutTable.KsAll.Length, ShortcutTable.Rows(true).Count());
        // The website and the map read KsAll, which keeps the default.
        Assert.Contains(ShortcutTable.KsAll, b => b.LabelKey == "Str_KS_CommitText" && b.Keys == "%ctrl%+%enter%");
        Assert.DoesNotContain(ShortcutTable.KsAll, b => b.LabelKey == "Str_KS_CommitTextEnter");
    }

    [Fact]
    public void TheOverlayBuildsFromTheModeRows()
    {
        string overlay = ShellSource.Read("Shell", "ShortcutsOverlay.cs");
        Assert.Contains("ShortcutTable.Rows(", overlay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Str_KS_CommitTextEnter")]
    [InlineData("Str_Bar_EnterSaves")]
    public void TheNewTextIsInEveryLanguage(string key)
    {
        char enDash = (char)0x2013, emDash = (char)0x2014;
        string strings = Path.GetDirectoryName(EnglishStringsPath())!;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        string[] files = Directory.GetFiles(strings, "*.xaml");
        Assert.Equal(20, files.Length);
        foreach (string file in files)
        {
            XDocument doc = XDocument.Load(file);
            string? value = doc.Root!.Elements().SingleOrDefault(e => (string?)e.Attribute(x + "Key") == key)?.Value;
            Assert.False(string.IsNullOrWhiteSpace(value), key + " missing in " + Path.GetFileName(file));
            Assert.DoesNotContain(enDash, value!);
            Assert.DoesNotContain(emDash, value!);
        }
    }

    private static string EnglishStringsPath()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return Path.Combine(directory!.FullName, "Strings", "en-US.xaml");
    }
}
