using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// 1.9.4 card menus (the KillerUI card menu kit): the page thumbnail and markup right-click menus are cards, the page
// menu is short, and selecting a mark lends it the draw bar without changing the look of the next mark.
public sealed class CardMenuTests
{
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([Root(), .. parts]));

    private static string Between(string text, string start, string end)
    {
        int from = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing " + start);
        int to = text.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        return text[from..(to < 0 ? text.Length : to)];
    }

    private static int Count(string text, string part)
    {
        int count = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    [Fact]
    public void CardGlyphsAreWrittenAsEscapesNeverLiteralCharacters()
    {
        // Literal private-use glyphs do not survive tooling (family rule); the card code uses \u escapes.
        foreach (var file in new[] { ("Shell", "PageCard.cs"), ("Shell", "AnnotationCard.cs"), ("Controls", "DeletePagesDialog.cs") })
        {
            string text = Read(file.Item1, file.Item2);
            Assert.DoesNotContain(text, c => c >= '' && c <= '');
        }
        string pageMenu = Between(Read("Shell", "ContextMenu.cs"), "private void AddPageMenuItems(", "private Button NavRowButton(");
        Assert.DoesNotContain(pageMenu, c => c >= '' && c <= '');
    }

    [Fact]
    public void TheKitCarriesTheCardStylesAndFlatButtons()
    {
        string controls = Read("Controls", "Controls.xaml");
        foreach (string key in new[] { "CardMenu", "CardSlot", "CardLabel", "CardQuickButton", "CardSwatch", "CardSegment", "CardToggle", "ThinSlider" })
            Assert.Contains($"x:Key=\"{key}\"", controls);
        Assert.DoesNotContain("liftRest", controls);
        Assert.DoesNotContain("liftRest", Read("Controls", "UiKit.cs"));
        Assert.True(File.Exists(Path.Combine(Root(), "Controls", "SwatchRow.cs")));
    }

    [Fact]
    public void AThumbnailOpensThePageCardWithItsSixQuickActions()
    {
        string handler = Between(Read("Shell", "ContextMenu.cs"), "private void PageList_MouseRightButtonUp", "private void SidebarArea_MouseRightButtonUp");
        Assert.Contains("BuildPageCard(clickedPage)", handler);
        Assert.DoesNotContain("Str_Ctx_InsertBlank", handler);

        string card = Read("Shell", "PageCard.cs");
        string strip = Between(card, "var strip = QuickStrip(menu,", "// The caption is short");
        Assert.Equal(6, Count(strip, "Loc(\"Str_"));
        // The short caption keeps the full action as its tooltip and accessible name.
        Assert.Contains("insert.ToolTip = Loc(\"Str_Ctx_InsertBlank\");", card);
        Assert.Contains("\"CardMenu\"", card);
        // The scanner cleanup, the extract-from-page tools and the moves fold into three submenus.
        Assert.Contains("cleanUp.Items.Add(", card);
        Assert.Contains("fromPage.Items.Add(", card);
        Assert.Contains("move.Items.Add(", card);
    }

    [Fact]
    public void MarkupOpensTheAnnotationCard()
    {
        string menu = Read("Shell", "ContextMenu.cs");
        Assert.Contains("AddAnnotationCard(hit);", Between(menu, "internal void PopulateContextMenu", "private PageAnnotation? AnnotationAt"));
        Assert.DoesNotContain("AddAnnotationMenuItems", menu);

        string card = Read("Shell", "AnnotationCard.cs");
        Assert.Contains("new SwatchRow", card);
        Assert.Contains("\"ThinSlider\"", card);
        Assert.Contains("\"CardSegment\"", card);
        // The card restyles its marks through the page undo, never through the bar's fields.
        string apply = Between(card, "private void ApplyCardStyle", "\n        }");
        Assert.Contains("PushStyleUndo(targets, gesture);", apply);
        Assert.DoesNotContain("_highlightColor", card);
        Assert.DoesNotContain("_drawColor", card);
    }

    [Fact]
    public void ThePageMenuIsShortWithItsPageToolsUnderPage()
    {
        string page = Between(Read("Shell", "ContextMenu.cs"), "private void AddPageMenuItems(", "private FrameworkElement LabelledStrip(");
        Assert.InRange(Count(page, "_ctxMenu.Items.Add("), 1, 14);
        Assert.Equal(10, Count(page, "tools.Items.Add(Make"));
        Assert.Contains("AddNavRow(_ctxMenu, pageIdx);", page);
        string nav = Between(Read("Shell", "ContextMenu.cs"), "private void AddNavRow(", "private Button NavRowButton(");
        foreach (string key in new[] { "Str_Kb_Back", "Str_Kb_Forward", "Str_Ctx_UndoLast", "Str_Ctx_Redo", "Str_Ctx_Print" })
            Assert.Contains($"\"{key}\"", nav);
    }

    [Fact]
    public void SelectingAMarkLendsItTheBarAndDeselectingGivesTheToolsLookBack()
    {
        string annotations = Read("Controls", "Viewer", "PdfViewer.Annotations.cs");
        string sync = Between(annotations, "else if (annot is HighlightAnnotation { IsShape: true } shapeSel)", "// A measurement opens the Measure bar");
        Assert.Equal(3, Count(sync, "BorrowDrawLook();"));
        Assert.Equal(3, Count(sync, "NoteSelectedDrawLook();"));
        string measure = Between(annotations, "else if (annot is MeasurementAnnotation msel)", "ShowDrawSettings(EditTool.Measure);");
        Assert.Contains("BorrowDrawLook();", measure);
        Assert.Contains("ReleaseSelectedDrawLook();", Between(annotations, "internal void ClearSelection()", "private void ClearMultiSelection()"));
    }
}
