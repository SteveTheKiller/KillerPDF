using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// What the page menus offer and when their rows are enabled. The menus need a window to build, so the shell source
// is pinned, the way CardMenuTests does it.
public sealed class PageMenuLogicTests
{
    private static string ContextMenu => ShellSource.Read("Shell", "ContextMenu.cs");

    private static string PageMenuBody => ShellSource.Between(ContextMenu, "private void AddPageMenuItems(", "private FrameworkElement LabelledStrip(");

    [Fact]
    public void ABookGetsOnlyTheRowsThatWorkOnABook()
    {
        // A native EPUB or DjVu book has no PDF behind it, so the place strip, field presets, page tools, stamp and
        // clear rows would all answer "Open a PDF first".
        string guard = ShellSource.Between(PageMenuBody, "ActiveViewer.NativeDocumentRef", "QuickStrip(_ctxMenu");
        Assert.Contains("AddNativePageMenuItems(pageIdx)", guard);

        string book = ShellSource.Between(ContextMenu, "private void AddNativePageMenuItems(", "// The top row of the page menus");
        foreach (string row in new[] { "AddNavRow(_ctxMenu, pageIdx)", "Str_Ctx_Copy", "Str_Ctx_SelectAll", "Str_NativePdf_Convert", "ConvertNativeDocumentToPdf", "AppendViewItems(_ctxMenu)" })
            Assert.Contains(row, book);
        foreach (string pdfOnly in new[] { "BuildFieldPresetMenu", "Str_Card_PageTools", "Str_Ctx_StampPages", "Str_Ctx_ClearPage", "Str_Lbl_Redact", "QuickStrip", "Str_Ctx_DeleteSel", "Str_Ctx_Paste" })
            Assert.DoesNotContain(pdfOnly, book);
    }

    [Fact]
    public void ThePrintButtonOfTheNavRowIsEnabledOnlyWhenPrintCanRun()
    {
        string nav = ShellSource.Between(ContextMenu, "private void AddNavRow(", "private Button NavRowButton(");
        // Print_Click answers "Open a PDF first" without a PDF that has a file.
        Assert.Contains("Print_Click(this, new RoutedEventArgs()), _doc is not null && _currentFile is not null)", nav);
        // A book has no _doc, so the page label reads the viewer's page count.
        Assert.Contains("ActiveViewer.ReadPageCountExt", nav);
    }

    [Fact]
    public void UndoRedoAndClearRowsAreEnabledOnlyWhenTheyCanAct()
    {
        string nav = ShellSource.Between(ContextMenu, "private void AddNavRow(", "private Button NavRowButton(");
        string undo = ShellSource.Between(nav, "\"Str_Ctx_UndoLast\"", "\"Str_Ctx_Redo\"");
        string redo = ShellSource.Between(nav, "\"Str_Ctx_Redo\"", "\"Str_Ctx_Print\"");
        Assert.DoesNotContain(", true));", undo);
        Assert.DoesNotContain(", true));", redo);
        Assert.Contains("edit.Undo", undo);
        Assert.Contains("EditAvailability()", nav);
        Assert.Contains("edit.Redo", redo);
        // The toolbar and the menu read one helper, not the toolbar button's own queued state.
        string toolbar = ShellSource.Read("Shell", "ToolbarLayout.cs");
        string refresh = ShellSource.Between(toolbar, "private void RefreshToolbarAvailability()", "private (bool Undo, bool Redo, bool Clear) EditAvailability()");
        Assert.Contains("EditAvailability()", refresh);
        Assert.DoesNotContain("_undoStack.Count", refresh);
        Assert.DoesNotContain("UndoBtn.IsEnabled", nav);

        // Clear Page Annotations is a tile, enabled only while the page has marks or one is still being drawn.
        string canClear = ShellSource.Between(PageMenuBody, "bool canClearPage =", ";");
        Assert.Contains("pageMarks.Count > 0", canClear);
        Assert.Contains("ActiveViewer.HasPendingAnnotationExt", canClear);
        Assert.Contains("ClearAnnotations_Click(this, new RoutedEventArgs()), false, canClearPage)", ShellSource.Between(PageMenuBody, "Loc(\"Str_Ctx_ClearPage\")", "\n"));
        Assert.DoesNotContain("_ctxMenu.Items.Add(MakeMenuItem(Loc(\"Str_Ctx_ClearPage\")", PageMenuBody);
        string agnostic = ShellSource.Between(ContextMenu, "private void FillPageAgnosticMenu(", "internal void DocPaneBackground_RightClick");
        Assert.Contains("EditAvailability().Clear", ShellSource.Between(agnostic, "Loc(\"Str_Lbl_Clear\")", "AppendViewItems(menu)"));
    }

    [Fact]
    public void AMenuActsOnTheClickedPageUnlessItIsPartOfTheSelection()
    {
        Assert.Equal([4], PageMenuTarget.Pages(4, [0, 1]));
        Assert.Equal([4], PageMenuTarget.Pages(4, []));
        Assert.Equal([0, 4, 7], PageMenuTarget.Pages(4, [0, 4, 7]));
    }

    [Fact]
    public void RotateTransformColorCorrectionAndStampActOnTheClickedPage()
    {
        string tools = ShellSource.Between(PageMenuBody, "var tools = ", "bool canClearPage");
        Assert.Contains("RotatePages_Click(90, pageIdx)", tools);
        Assert.Contains("RotatePages_Click(-90, pageIdx)", tools);
        Assert.Contains("OpenTransformWindow(page: pageIdx)", tools);
        Assert.Contains("OpenTransformWindow(TransformWindowMode.ColorCorrection, pageIdx)", tools);
        Assert.Contains("OpenStampTool(pageIdx)", PageMenuBody);
        // Fix orientation keeps its own rule (selection for two or more pages, otherwise the document).
        Assert.Contains("FixPageOrientation()", tools);
    }
}
