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
    public void CardRowsAndStripsStartOnTheKitIconColumn()
    {
        // KillerUI 461c02a: a card's labeled rows and its action strip's rules start on the icon column (8 px), the
        // edge the header and the ordinary rows' icons use; at 24, past the gutter, the labels sat in from everything.
        string annotation = Read("Shell", "AnnotationCard.cs");
        Assert.DoesNotMatch(@"CardSlot\([^;]*new Thickness\(24,", annotation);
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(annotation, @"new Thickness\(8, \d, 4, \d\)").Count);
        // The quick strip draws no rules, so it keeps its full width instead of the kit's 8 px rule inset; with the
        // inset, "Extract Page(s)" wrapped onto two lines on SN02 (2026-11-13).
        string strip = Between(Read("Shell", "PageCard.cs"), "var strip = new Border", "return strip;");
        Assert.DoesNotContain("BorderThickness", strip);
        Assert.Contains("Padding = new Thickness(2, 4, 2, 4)", strip);
        Assert.DoesNotContain("Margin = new Thickness(8", strip);
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
        string strip = Between(card, "var strip = QuickStrip(menu,", "menu.Items.Add(CardSlot(strip");
        // Three tiles; Blank page is an icon in the header and rotation sits under the tiles (Steve: narrower card).
        Assert.Equal(3, Count(strip, "Loc(\"Str_"));
        Assert.Contains("header.Children.Add(insert);", card);
        Assert.Contains("menu.MinWidth = menu.MaxWidth = 320;", card);
        string rotate = Between(card, "private FrameworkElement RotateRow(", "private Button CompactCardButton(");
        Assert.Contains("Str_Ctx_RotateCCWShort", rotate);
        Assert.Contains("Str_Ctx_RotateCWShort", rotate);
        Assert.Contains("menu.Items.Add(CardSlot(RotateRow(menu)", card);
        // The sidebar side and two-page thumbnails sit in one Sidebar row's flyout, not four rows at the foot.
        string side = Between(Read("Shell", "ContextMenu.cs"), "private void AppendSidebarSideSection(", "private ContextMenu MakeThemedMenu(");
        Assert.Contains("Header = Loc(\"Str_Card_Sidebar\")", side);
        Assert.Contains("sidebar.Items.Add(row);", side);
        Assert.DoesNotContain("Header = Loc(\"Str_Sidebar\"), IsEnabled = false", side);
        // The short caption keeps the full action as its tooltip and accessible name.
        Assert.Contains("ToolTip = Loc(\"Str_Ctx_InsertBlank\")", card);
        Assert.Contains("\"CardMenu\"", card);
        // The scanner cleanup, the extract-from-page tools and the moves fold into three submenus.
        Assert.Contains("cleanUp.Items.Add(", card);
        Assert.Contains("fromPage.Items.Add(", card);
        Assert.Contains("move.Items.Add(", card);
        // Review BF-007: the card names the clicked page, because its single-page actions act on that page, and
        // Insert blank page goes after it.
        Assert.DoesNotContain("Str_Card_AllSelected", card);
        Assert.Contains("InsertBlankPage(page);", card);
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
        // Text, Paragraph, Image and Signature stay in the page menu as a strip with no Place here label (Steve).
        Assert.DoesNotContain("Str_Card_PlaceHere", menu2);
        Assert.Contains("_ctxMenu.Items.Add(CardSlot(placeStrip, new Thickness(0)));", menu2);
        Assert.Contains("PlaceTextBox(pt, pageIdx);", menu2);
        Assert.Contains("PlaceImageFromDialog(pt, pageIdx);", menu2);
        Assert.Contains("PlaceSignature(pt, pageIdx);", menu2);
        Assert.Contains("EditParagraph(pageIdx, pt)", menu2);
        // The selected-text strip wraps into rows of three so the page menu stays narrow.
        Assert.Contains("var textStrip = QuickStrip(_ctxMenu, 3, [.. text]);", menu2);
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
