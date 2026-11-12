using System.IO;
using Xunit;

namespace KillerPDF.Tests;

// 1.9.3 card menus (the KillerUI card menu kit): the page thumbnail and markup right-click menus are cards, the page
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
        foreach (var file in new[] { ("Shell", "PageCard.cs"), ("Shell", "AnnotationCard.cs"), ("Controls", "DeletePagesDialog.cs"), ("KillerPDF.Tests", "CardMenuTests.cs") })
        {
            string text = Read(file.Item1, file.Item2);
            Assert.DoesNotContain(text, c => c >= '\uE000' && c <= '\uF8FF');
        }
        string pageMenu = Between(Read("Shell", "ContextMenu.cs"), "private void AddPageMenuItems(", "private Button NavRowButton(");
        Assert.DoesNotContain(pageMenu, c => c >= '\uE000' && c <= '\uF8FF');
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
        // Review BF-007: the card names the clicked page, because its single-page actions act on that page, and
        // Insert blank page goes after it.
        Assert.DoesNotContain("Str_Card_AllSelected", card);
        Assert.Contains("() => InsertBlankPage(page)", card);
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
        // Review BF-012: opened from the keyboard, both cards focus their first quick action.
        Assert.Contains("FocusFirstActionFromKeyboard(_ctxMenu, strip);", card);
        Assert.Contains("FocusFirstActionFromKeyboard(menu, strip);", Read("Shell", "PageCard.cs"));
        // r2 BF-004/005/009: the page menu's strips, the page list's Menu key, and the selected mark itself.
        string menu2 = Read("Shell", "ContextMenu.cs");
        Assert.Contains("FocusFirstActionFromKeyboard(_ctxMenu, textStrip);", menu2);
        Assert.Contains("FocusFirstActionFromKeyboard(_ctxMenu, placeStrip);", menu2);
        Assert.Contains("var hit = target ?? AnnotationAt(pt, pageIdx);", menu2);
        string keys = Read("Shell", "KeyboardShortcuts.cs");
        Assert.Contains("if (!OpenPageCardFromKeyboard()) OpenContextMenuAtSelection();", keys);
        Assert.Contains("PopulateContextMenu(pt, pg, _selectedAnnotation);", keys);
    }

    [Fact]
    public void ASelectedMarksLookIsNeverSavedAsTheToolsDefault()
    {
        // r2 BF-002, r3 BF-001: settings save the settled tool look (DrawToolLookTests covers Peek) and nothing is
        // released before a close that can still be cancelled.
        Assert.Contains("var draw = ActiveViewer.DrawLookToSave();", Read("Shell", "MainWindow.xaml.cs"));
        Assert.DoesNotContain("DrawToolDefaultsForExit", Read("Shell", "WindowChrome.cs"));
        Assert.DoesNotContain("DrawToolDefaultsForExit", Read("Shell", "RenderRecovery.cs"));
    }

    [Fact]
    public void RedactAndCropKeepTheirBarsButASelectionStillLendsItsLook()
    {
        // r3 BF-002, r4 BF-001/002: under Redact or Crop a selected mark still syncs and borrows the draw look (a tool
        // switch keeps the selection, and the next bar change must start from the mark), but neither the draw bar nor
        // the text bar takes the dock from Redact's Apply and Clear or Crop's confirm bar.
        string drawLook = Read("Controls", "Viewer", "PdfViewer.DrawLook.cs");
        string sync = Between(drawLook, "private bool SyncDrawBarToSelection(", "internal void RefreshSelectedDrawBar()");
        Assert.DoesNotContain("EditTool.Crop) return false;", sync);
        Assert.Contains("if (!KeepsOwnBar(_currentTool)) ShowDrawSettings(tool);", sync);
        Assert.Contains("if (!KeepsOwnBar(_currentTool)) ShowTextSettings();",
            Between(Read("Controls", "Viewer", "PdfViewer.Annotations.cs"), "private void SelectAnnotation(", "private static bool IsDraggable("));
    }

    [Fact]
    public void ThePageMenuIsShortWithItsPageToolsUnderPage()
    {
        string page = Between(Read("Shell", "ContextMenu.cs"), "private void AddPageMenuItems(", "private FrameworkElement LabelledStrip(");
        Assert.InRange(Count(page, "_ctxMenu.Items.Add("), 1, 14);
        Assert.Equal(10, Count(page, "tools.Items.Add(Make"));
        Assert.Contains("AddNavRow(_ctxMenu, pageIdx);", page);
        // Review BF-008: Delete Page acts on the right-clicked page like its neighbours, not the sidebar selection.
        Assert.Contains("DeletePages(pageIdx)", page);
        Assert.DoesNotContain("Delete_Click", page);
        string nav = Between(Read("Shell", "ContextMenu.cs"), "private void AddNavRow(", "private Button NavRowButton(");
        foreach (string key in new[] { "Str_Kb_Back", "Str_Kb_Forward", "Str_Ctx_UndoLast", "Str_Ctx_Redo", "Str_Ctx_Print" })
            Assert.Contains($"\"{key}\"", nav);
    }

    [Fact]
    public void SelectingAMarkLendsItTheBarAndDeselectingGivesTheToolsLookBack()
    {
        // The borrow and release rules themselves are exercised in DrawToolLookTests; these pin the wiring.
        string annotations = Read("Controls", "Viewer", "PdfViewer.Annotations.cs");
        Assert.Contains("else SyncDrawBarToSelection(annot);", Between(annotations, "private void SelectAnnotation(", "private static bool IsDraggable("));
        Assert.Contains("ReleaseSelectedDrawLook();", Between(annotations, "internal void ClearSelection()", "private void ClearMultiSelection()"));
        string drawLook = Read("Controls", "Viewer", "PdfViewer.DrawLook.cs");
        string sync = Between(drawLook, "private bool SyncDrawBarToSelection(", "internal void RefreshSelectedDrawBar()");
        Assert.Equal(4, Count(sync, "_drawLookLease.Borrow(CurrentDrawLook());"));
        Assert.Contains("_drawLookLease.Note(CurrentDrawLook());", sync);
        // Review BF-001/002/003: a card change refreshes the bar the selection opened and keeps the outlines.
        string apply = Between(Read("Shell", "AnnotationCard.cs"), "private void ApplyCardStyle", "\n        }");
        Assert.Contains("ActiveViewer.RefreshSelectedDrawBar();", apply);
        Assert.Contains("ActiveViewer.ReattachMultiOutlinesExt();", apply);
    }
}
