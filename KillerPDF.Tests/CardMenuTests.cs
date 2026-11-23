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
        // The quick strip draws no rules and adds no padding: with CardMenu's 7 and the 1px border, a tile's hover sits
        // 8 px from every edge of the card (family rule, Steve 2026-11-19).
        string strip = Between(Read("Controls", "CardBuilder.cs"), "var strip = new Border", "return strip;");
        Assert.DoesNotContain("BorderThickness", strip);
        Assert.Contains("Padding = new Thickness(0)", strip);
        Assert.DoesNotContain("Margin = new Thickness(8", strip);
    }

    [Fact]
    public void TheMarkCardsCarryNoTextRows()
    {
        // Steve 2026-11-19: cards are tiles only. Save Picture As, Replace Picture, Paste, Select Text, Unpair and
        // Remove from Group were text rows under the tiles; they are tiles now, and nothing adds a row to the card.
        foreach (string file in new[] { "PictureCard.cs", "AnnotationCard.cs" })
        {
            string card = Read("Shell", file);
            Assert.DoesNotContain("_ctxMenu.Items.Add(MakeMenuItem(", card);
            Assert.DoesNotContain("_ctxMenu.Items.Add(new Separator())", card);
        }
        // The page card's own rows only ever go into its flyout tiles.
        Assert.DoesNotContain("menu.Items.Add(MakeMenuItem(", Read("Shell", "PageCard.cs"));
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
        string strip = Between(card, "CardTile[] pageRow =", "CardTile[] turnRow");
        // Three tiles; Delete is the trash button in the header, as on the mark cards, and Insert Blank Page After took
        // its tile (Steve 2026-11-19). Rotation sits under the tiles (Steve: narrower card).
        Assert.Equal(3, Count(strip, "Loc(\"Str_"));
        Assert.Contains("Str_Ctx_InsertBlank", strip);
        Assert.DoesNotContain("Str_Ctx_DeletePages", strip);
        Assert.Contains("NavRowButton(menu, \"\\uE74D\", \"Str_Ctx_DeletePages\"", Between(card, "private FrameworkElement PageCardHeader(", "private string PageCardMeta("));
        Assert.Contains("menu.MinWidth = menu.MaxWidth = 320;", card);
        // Rotation is a second tile row matching the first (Steve 2026-11-14: the outline buttons stuck out): Rotate CCW,
        // Upside Down (a half turn) and Rotate CW.
        string rotate = Between(card, "CardTile[] turnRow =", "CardTile[] toolRow");
        Assert.Equal(3, Count(rotate, "Loc(\"Str_"));
        Assert.Contains("Str_Ctx_RotateCCWShort", rotate);
        Assert.Contains("Str_Ctx_UpsideDown", rotate);
        Assert.Contains("RotatePages_Click(180", rotate);
        Assert.Contains("Str_Ctx_RotateCWShort", rotate);
        Assert.DoesNotContain("RotateRow(", card);
        // The sidebar sides are one row of four smaller tiles on the card, the current side filled; no Sidebar row or
        // flyout (Steve 2026-11-19). In a two-page view, Two-page thumbnails and Book layout lead a grid of two rows of
        // three with Left, then Right, Top and Bottom (Steve 2026-11-23).
        string side = Between(Read("Shell", "ContextMenu.cs"), "private void AppendSidebarSideSection(", "private ContextMenu MakeThemedMenu(");
        Assert.Contains("QuickStrip(menu, twoPage ? 3 : order.Length, tiles)", side);
        Assert.Contains("tiles = [toggle, book, .. tiles]", side);
        Assert.Contains("{ Checked = side == _sidebarSide }", side);
        Assert.Contains("{ Checked = paired }", side);
        Assert.Contains("SidebarSideShortcut(side)", side);   // each side tile shows its own chord
        Assert.DoesNotContain("new MenuItem", side);
        Assert.Contains("\"CardMenu\"", card);
        // The scanner cleanup, the extract-from-page tools and the moves fold into three submenus.
        Assert.Contains("cleanUp.Items.Add(", card);
        Assert.Contains("fromPage.Items.Add(", card);
        Assert.Contains("move.Items.Add(", card);
        // Review BF-007: the card names the clicked page, because its single-page actions act on that page, and
        // Insert blank page goes after it.
        Assert.DoesNotContain("Str_Card_AllSelected", card);
        Assert.Contains("() => InsertBlankPage(page)", strip);
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
        Assert.Contains("FocusFirstActionFromKeyboard(_ctxMenu, topStrip);", menu2);
        // Text, Paragraph, Image and Signature stay in the page menu with no Place here label (Steve).
        Assert.DoesNotContain("Str_Card_PlaceHere", menu2);
        Assert.Contains("PlaceTextBox(pt, pageIdx);", menu2);
        Assert.Contains("PlaceImageFromDialog(pt, pageIdx);", menu2);
        Assert.Contains("PlaceSignature(pt, pageIdx);", menu2);
        Assert.Contains("EditParagraph(pageIdx, pt)", menu2);
        // The selected-text strip wraps into rows of three so the page menu stays narrow.
        Assert.Contains("take, marks, place);", menu2);
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
        Assert.Equal(8, Count(page, "tools.Items.Add(Make"));
        Assert.Contains("AddNavRow(_ctxMenu, pageIdx);", page);
        // Review BF-008: Delete Page acts on the right-clicked page like its neighbours, not the sidebar selection.
        Assert.Contains("DeletePages(pageIdx)", page);
        Assert.DoesNotContain("Delete_Click", page);
        string nav = Between(Read("Shell", "ContextMenu.cs"), "private void AddNavRow(", "private Button NavRowButton(");
        foreach (string key in new[] { "Str_Kb_Back", "Str_Kb_Forward", "Str_Ctx_UndoLast", "Str_Ctx_Redo", "Str_Ctx_Print" })
            Assert.Contains($"\"{key}\"", nav);
    }

    [Fact]
    public void TheMenusShowTheirCommandsAsTilesAndSubmenusAsChevronTiles()
    {
        // Steve 2026-11-17: go all out on the icons. Submenus become tiles with a chevron that open their rows
        // beside the tile; each command keeps one entry.
        string card = Read("Shell", "PageCard.cs");
        string tools = Between(card, "CardTile[] toolRow =", "var cleanUp");
        foreach (string key in new[] { "Str_Lbl_Merge", "Str_Tf_Suffix", "Str_ColorCorrection", "Str_Ctx_OcrPage", "Str_Ctx_ExportPageImage", "Str_Ctx_StampPages" })
        {
            Assert.Contains($"Loc(\"{key}\")", tools);
            Assert.Equal(1, Count(card, $"Loc(\"{key}\")"));
        }
        string groups = Between(card, "CardTile[] groupRow =", "menu.Items.Add(CardSlot(strip");
        foreach (string row in new[] { "cleanUp)", "fromPage)", "move)" }) Assert.Contains(row, groups);
        Assert.DoesNotContain("menu.Items.Add(cleanUp)", card);
        string kit = Read("Controls", "CardBuilder.cs");
        Assert.Contains("WithFlyoutChevron(button)", kit);
        Assert.Contains("PlacementMode.Right", Between(kit, "private static ContextMenu TileFlyout(", "return flyout;"));

        string page = Between(Read("Shell", "ContextMenu.cs"), "private void AddPageMenuItems(", "private FrameworkElement LabelledStrip(");
        Assert.Contains("FlyoutTile(\"\\uE8A1\", Loc(\"Str_FieldPreset_Menu\"), null, BuildFieldPresetMenu(pt, pageIdx))", page);
        Assert.Contains("Loc(\"Str_TT_ComparePDFs\"), \"AltShift:V\", BuildComparisonContextItem())", page);
        Assert.Contains("Loc(\"Str_Card_PageTools\"), null, tools)", page);
        // Full Screen is in the footer and Book layout on the nav row, so the grid stays three full rows.
        Assert.DoesNotContain("ViewTiles()", page);
        Assert.DoesNotContain("AppendViewItems", page);
        // Full Screen and Book layout show their state on the tile, as the checkable rows did.
        string view = Between(Read("Shell", "ContextMenu.cs"), "private List<CardTile> ViewTiles()", "return tiles;");
        Assert.Contains("{ Checked = _fullScreen }", view);
        Assert.Contains("{ Checked = Controls.PdfViewer.BookMode }", view);

        string ocr = Between(Read("Shell", "Ocr.cs"), "private void OcrMenu_Click(", "private static void SetBusyMessage(");
        Assert.Contains("\"CardMenu\"", ocr);
        foreach (string key in new[] { "Str_Ocr_Region", "Str_Ocr_SearchablePdf", "Str_Ocr_ScanToText", "Str_Ocr_ExtractText" })
            Assert.Contains($"Short(\"{key}\")", ocr);
        Assert.Contains("FlyoutTile(GlyphText(0xF2B7), Loc(\"Str_Ocr_Language\"), null, BuildLanguageMenu()));", ocr);
        Assert.DoesNotContain("menu.Items.Add(MakeMenuItem(", ocr);
    }

    [Fact]
    public void TheOcrMenuIsTwoFullRowsOfThreeWithLanguageLast()
    {
        // Steve 2026-11-17: the standard tile grid, three across and two full rows, Language's chevron tile last on
        // the right of the second row, no separator gap. Form-aware OCR moved into Language's list beside High quality.
        string ocr = Between(Read("Shell", "Ocr.cs"), "private void OcrMenu_Click(", "private static void SetBusyMessage(");
        string grid = Between(ocr, "var tiles = QuickStrip(menu, 3,", "menu.Items.Add(CardSlot(tiles");
        Assert.Equal(5, Count(grid, "new CardTile("));
        Assert.Equal(1, Count(grid, "FlyoutTile("));
        Assert.EndsWith("BuildLanguageMenu()));", grid.TrimEnd());
        Assert.Equal(1, Count(ocr, "QuickStrip("));
        Assert.DoesNotContain("new Separator()", ocr);
        Assert.DoesNotContain("Str_Ocr_FormAware", ocr);
        string language = Between(Read("Shell", "Ocr.cs"), "private MenuItem BuildLanguageMenu()", "private async void DownloadOcrLanguage(");
        Assert.Contains("Header = Loc(\"Str_Ocr_FormAware\")", language);
        Assert.Contains("App.SetSetting(\"OcrFormAware\"", language);
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

    // #496: the page card's top box (the selected-text tiles and Text, Image, Signature) is customizable like the
    // tools grid under it, saved under its own key, and the kit keeps two grids on one card apart.
    [Fact]
    public void ThePageCardsTopBoxIsCustomizableOnItsOwn()
    {
        string menu = Read("Shell", "ContextMenu.cs");
        Assert.Contains("EditableTileGroups(_ctxMenu, \"PageTop\", [], [], take, marks, place)", menu);
        Assert.Contains("EditableTileGroups(_ctxMenu, \"Page\",", menu);
        Assert.Contains("Key = card,", Read("Shell", "PageCard.cs"));
        string kit = Read("Controls", "CardBuilder.cs");
        Assert.Contains("grids.Remove(edit.Key, out var last)", kit);
        Assert.Contains("other.EndEditing();", kit);
        Assert.Contains("SavedLayout(order,", kit);
    }
}
