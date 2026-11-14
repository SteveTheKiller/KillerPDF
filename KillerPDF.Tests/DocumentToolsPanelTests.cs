using System.Windows.Input;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// The Document tools sidebar panel: its sections, how many columns it lays out at a width, the key that runs a
// tool while the panel has the keyboard, and the Alt+7 key that opens it.
public sealed class DocumentToolsPanelTests
{
    private const string Reports = "Str_DocTools_GroupReports";
    private const string Edit = "Str_DocTools_GroupEdit";
    private const string Automation = "Str_DocTools_GroupAutomation";

    [Fact]
    public void APdfListsEveryToolUnderReportsEditAndAutomation()
    {
        string[] tools =
        [
            "Str_DocumentTools_Preflight", "Str_CM_Title", "Str_DocumentTools_PrintProduction",
            "Str_DocumentTools_Separations", "Str_DocumentTools_Accessibility", "Str_DocumentTools_Signatures",
            "Str_CertCatalog_Title", "Str_DocumentTools_Layers", "Str_DocumentTools_EditLayers",
            "Str_DocumentTools_Portfolio", "Str_DocumentTools_EditPortfolio", "Str_DocumentTools_EditPortfolioStructure",
            "Str_DocumentTools_EditAttachments", "Str_DocumentTools_Comments", "Str_DocumentTools_Measurements",
            "Str_DocumentTools_NavigationAudit", "Str_DocumentTools_AutoBookmarks", "Str_DocumentTools_PageLabels",
            "Str_DocumentTools_InitialView", "Str_DocumentTools_StructuralComparison", "Str_Guided_Title",
            "Str_DocumentTools_Macros", "Str_DocumentTools_DataMerge", "Str_DocumentTools_FormData",
            "Str_DocumentTools_BookmarkData", "Str_DocumentTools_ProductionLayouts", "Str_DocumentTools_InsertToc",
        ];

        var groups = DocumentToolsLayout.Group(tools);

        Assert.Equal([Reports, Edit, Automation], groups.Select(g => g.TitleKey));
        Assert.Equal(
            [
                "Str_DocumentTools_Preflight", "Str_CM_Title", "Str_DocumentTools_PrintProduction",
                "Str_DocumentTools_Separations", "Str_DocumentTools_Accessibility", "Str_DocumentTools_Signatures",
                "Str_CertCatalog_Title", "Str_DocumentTools_Layers", "Str_DocumentTools_Portfolio",
                "Str_DocumentTools_Measurements", "Str_DocumentTools_NavigationAudit",
                "Str_DocumentTools_StructuralComparison",
            ], groups[0].Tools);
        Assert.Equal(
            [
                "Str_DocumentTools_EditLayers", "Str_DocumentTools_EditPortfolio",
                "Str_DocumentTools_EditPortfolioStructure", "Str_DocumentTools_EditAttachments",
                "Str_DocumentTools_Comments", "Str_DocumentTools_AutoBookmarks", "Str_DocumentTools_PageLabels",
                "Str_DocumentTools_InitialView", "Str_DocumentTools_InsertToc",
            ], groups[1].Tools);
        Assert.Equal(
            [
                "Str_Guided_Title", "Str_DocumentTools_Macros", "Str_DocumentTools_DataMerge",
                "Str_DocumentTools_FormData", "Str_DocumentTools_BookmarkData", "Str_DocumentTools_ProductionLayouts",
            ], groups[2].Tools);
    }

    [Fact]
    public void WithNothingOpenOnlyCertificatesAndMacrosShowAndTheEmptySectionIsLeftOut()
    {
        var groups = DocumentToolsLayout.Group(["Str_DocumentTools_Macros", "Str_CertCatalog_Title"]);

        Assert.Equal(2, groups.Count);
        Assert.Equal(Reports, groups[0].TitleKey);
        Assert.Equal(["Str_CertCatalog_Title"], groups[0].Tools);
        Assert.Equal(Automation, groups[1].TitleKey);
        Assert.Equal(["Str_DocumentTools_Macros"], groups[1].Tools);
    }

    [Fact]
    public void ABookShowsConvertUnderEditBesideItsShortList()
    {
        var groups = DocumentToolsLayout.Group(
            ["Str_NativePdf_Convert", "Str_CM_Title", "Str_DocumentTools_Macros", "Str_CertCatalog_Title"]);

        Assert.Equal([Reports, Edit, Automation], groups.Select(g => g.TitleKey));
        Assert.Equal(["Str_CM_Title", "Str_CertCatalog_Title"], groups[0].Tools);
        Assert.Equal(["Str_NativePdf_Convert"], groups[1].Tools);
        Assert.Equal(["Str_DocumentTools_Macros"], groups[2].Tools);
    }

    [Fact]
    public void EveryToolWithAChordLetterHasASection()
    {
        var all = ShortcutTable.DocumentToolKeys.Select(t => t.LabelKey).ToArray();
        var grouped = DocumentToolsLayout.Group(all).SelectMany(g => g.Tools).ToArray();
        Assert.Equal(all.OrderBy(k => k, StringComparer.Ordinal), grouped.OrderBy(k => k, StringComparer.Ordinal));
    }

    [Fact]
    public void AToolWithNoSectionIsRefusedRatherThanDropped()
        => Assert.Throws<ArgumentException>(() => DocumentToolsLayout.Group(["Str_Not_A_Tool"]));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(120, 1)]
    [InlineData(236, 2)]
    [InlineData(244, 2)]
    [InlineData(360, 3)]
    [InlineData(480, 4)]
    public void ColumnsFollowThePanelWidth(double width, int columns)
        => Assert.Equal(columns, DocumentToolsLayout.Columns(width));

    [Theory]
    [InlineData(Key.P, ModifierKeys.None, false, 'P')]
    [InlineData(Key.X, ModifierKeys.Shift, false, 'X')]
    [InlineData(Key.D0, ModifierKeys.None, false, '0')]
    [InlineData(Key.NumPad0, ModifierKeys.None, false, '0')]
    [InlineData(Key.P, ModifierKeys.Control | ModifierKeys.Shift, true, 'P')]
    public void ALetterOrZeroRunsItsToolWhileThePanelHasTheKeyboard(Key key, ModifierKeys modifiers, bool chordJustPressed, char expected)
        => Assert.Equal(expected, DocumentToolsLayout.ChordKey(key, modifiers, chordJustPressed));

    [Theory]
    [InlineData(Key.P, ModifierKeys.Alt, true)]
    [InlineData(Key.F5, ModifierKeys.None, false)]
    [InlineData(Key.D1, ModifierKeys.None, false)]
    [InlineData(Key.Enter, ModifierKeys.None, false)]
    // Once the chord's keys are let go, Ctrl+S and the other window commands are the window's again.
    [InlineData(Key.S, ModifierKeys.Control, false)]
    [InlineData(Key.S, ModifierKeys.Control, true)]
    [InlineData(Key.P, ModifierKeys.Control | ModifierKeys.Shift, false)]
    public void OtherKeysAreLeftToTheWindow(Key key, ModifierKeys modifiers, bool chordJustPressed)
        => Assert.Null(DocumentToolsLayout.ChordKey(key, modifiers, chordJustPressed));

    [Fact]
    public void AltSevenOpensThePanelWithNothingOpenBecauseItsButtonStaysEnabled()
    {
        Assert.True(ExpandedShortcutGate.Runs(hasPdf: false, hasReadingDocument: false, railButtonEnabled: true, Key.D7, ModifierKeys.Alt));
        Assert.True(ExpandedShortcutGate.Runs(hasPdf: false, hasReadingDocument: true, railButtonEnabled: true, Key.D7, ModifierKeys.Alt));
        Assert.False(ExpandedShortcutGate.Runs(hasPdf: false, hasReadingDocument: false, railButtonEnabled: false, Key.D7, ModifierKeys.Alt));
        Assert.False(ExpandedShortcutGate.Runs(hasPdf: false, hasReadingDocument: false, railButtonEnabled: true, Key.D7, ModifierKeys.Alt | ModifierKeys.Shift));
    }

    [Fact]
    public void AltSevenIsOnTheShortcutTableAsTheDocumentToolsPanel()
    {
        var map = ShortcutTable.BuildMap();
        Assert.Equal("Str_Rail_DocumentTools", map[KbLayer.Alt]["D7"].Label);
        Assert.True(ToolSearchReadingCommands.Admits(true, "Str_Rail_DocumentTools"));
    }
}
