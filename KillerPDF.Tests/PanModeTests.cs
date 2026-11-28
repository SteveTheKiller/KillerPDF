using System;
using KillerPDF.Controls;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PanModeTests
{
    [Theory]
    [InlineData(EditTool.Pan, false, true)]
    [InlineData(EditTool.Pan, true, false)]
    public void AltDragMovesWindowUnlessFullScreenWhenPanIsActive(EditTool tool, bool fullScreen, bool expected)
        => Assert.Equal(expected, AltDragTools.MovesWindow(tool, fullScreen));

    [Fact]
    public void PanToolHasNoDockedOptionsBar()
    {
        Assert.Equal(ToolOptionsBarKind.None, ToolOptionsCatalog.BarFor(EditTool.Pan));
        Assert.Empty(ToolOptionsCatalog.ShedOrder(EditTool.Pan));
    }

    [Fact]
    public void PanShortcutIsRemappableAndAdmittedForReadingBooks()
    {
        Assert.Contains(ShortcutRemap.Commands, command =>
            command.Original.Layer == KbLayer.Base && command.Original.Id == "P" && command.LabelKey == "Str_Lbl_Pan");

        var remap = new ShortcutRemap();
        Assert.Equal("Str_Lbl_Select", remap.Assign(new KsCap(KbLayer.Base, "P", ""), new KsCap(KbLayer.Base, "V", "")));
        Assert.Null(remap.Assign(new KsCap(KbLayer.Base, "P", ""), new KsCap(KbLayer.Base, "Q", "")));
        Assert.Null(remap.Translate(KbLayer.Base, "P"));
        Assert.Equal<(KbLayer, string)?>((KbLayer.Base, "P"), remap.Translate(KbLayer.Base, "Q"));

        Assert.True(ToolSearchReadingCommands.Admits(hasReadingDocument: true, "Str_Lbl_Pan"));
        Assert.False(ToolSearchReadingCommands.Admits(hasReadingDocument: false, "Str_Lbl_Pan"));
    }

    [Fact]
    public void ViewerPanInputAndCursorSyncAreWiredAcrossEventsAndTiles()
    {
        string zoom = ViewerSourcePin.Viewer("PdfViewer.Zoom.cs");
        string down = ViewerSourcePin.Between(zoom, "internal void PagePreviewPanel_PreviewMouseDown(", "else if (e.ChangedButton == MouseButton.Left");
        Assert.Contains("_currentTool == EditTool.Pan", down);
        Assert.Contains("BeginPan(", down);

        string up = ViewerSourcePin.Between(zoom, "internal void PagePreviewPanel_PreviewMouseUp(", "private void PagePreviewPanel_LostMouseCapture(");
        Assert.Contains("EndPanExt();", up);

        int lostAt = zoom.IndexOf("private void PagePreviewPanel_LostMouseCapture(", StringComparison.Ordinal);
        Assert.True(lostAt >= 0);
        string lost = zoom[lostAt..];
        Assert.Contains("_isPanning", lost);
        Assert.Contains("EndPanExt();", lost);

        string xaml = ViewerSourcePin.Viewer("PdfViewer.xaml");
        Assert.Contains("LostMouseCapture=\"PagePreviewPanel_LostMouseCapture\"", xaml, StringComparison.Ordinal);

        string annotations = ViewerSourcePin.Viewer("PdfViewer.Annotations.cs");
        string canvasDown = ViewerSourcePin.Between(annotations, "internal void Canvas_MouseLeftButtonDown(", "if (_doc is null)");
        Assert.Contains("if (_currentTool == EditTool.Pan) return;", canvasDown);

        string move = ViewerSourcePin.Between(annotations, "internal void Canvas_MouseMove(", "// Brush cursor:");
        Assert.Contains("!_isPanning && !_spaceHeld", move);

        string viewport = ViewerSourcePin.Viewer("PdfViewer.Viewport.cs");
        string applyRendered = ViewerSourcePin.Between(viewport, "var overlay = BuildPageOverlay(pi, pageDipW, pageDipH, null);", "var pageGrid = new Grid();");
        Assert.Contains("DragCursors.Open", applyRendered);
        Assert.Contains("_currentTool == EditTool.Pan", applyRendered);
    }

    [Fact]
    public void ShellToolSelectionShortcutsAndDeactivationPreservePanState()
    {
        string selection = ShellSource.Read("Shell", "ToolSelection.cs");
        Assert.Contains("EditTool.Pan => DragCursors.Open,", selection, StringComparison.Ordinal);
        string setTool = ShellSource.Between(selection, "private void SetTool(EditTool tool, bool restoringPane = false)", "var map = new");
        Assert.Contains("tool == EditTool.Pan && HasReadingDocument", setTool);
        Assert.Contains("EditTool.Select or EditTool.Pan", setTool);
        Assert.Contains("(_toolPanBtn, EditTool.Pan)", selection, StringComparison.Ordinal);
        Assert.Contains("ActiveViewer.SyncPanCursorExt();", selection, StringComparison.Ordinal);

        string shortcuts = ShellSource.Read("Shell", "KeyboardShortcuts.cs");
        Assert.Contains("if (key == Key.P) { SetTool(EditTool.Pan); return true; }", shortcuts, StringComparison.Ordinal);
        Assert.Contains("if (key == Key.V) { SetTool(EditTool.Select); return true; }", shortcuts, StringComparison.Ordinal);
        Assert.Contains("key == Key.Escape && HasReadingDocument && _currentTool != EditTool.Select", shortcuts, StringComparison.Ordinal);
        Assert.Contains("Viewer.SyncPanCursorExt();", shortcuts, StringComparison.Ordinal);

        string main = ShellSource.Read("Shell", "MainWindow.xaml.cs");
        Assert.Contains("Viewer.EndPanExt();", main, StringComparison.Ordinal);
        Assert.Contains("ViewerB.EndPanExt();", main, StringComparison.Ordinal);
    }
}
