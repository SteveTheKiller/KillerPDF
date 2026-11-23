using System;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #467: every press and wheel in a pane sent the keyboard to the page. The options bar and its
// dropdowns sit inside the pane, so a click on a font in the list, or the wheel over it, took the
// focus from the open ComboBox and closed it before anything was picked.
public class PointerFocusTests
{
    [Fact]
    public void OnlyAPressOnTheDocumentMovesTheKeyboardToThePage()
    {
        Assert.True(PointerFocus.MovesKeyboardToPage(onOptionsBar: false, inPopup: false));
        Assert.False(PointerFocus.MovesKeyboardToPage(onOptionsBar: true, inPopup: false));
        Assert.False(PointerFocus.MovesKeyboardToPage(onOptionsBar: false, inPopup: true));
    }

    [Fact]
    public void ThePaneFocusHookChecksWhereThePointerIs()
    {
        string split = ShellSource.Read("Shell", "SplitPane.cs");
        Assert.Contains("Viewer.PreviewMouseDown  += (_, e) => FocusPaneFromPointer(Viewer, e.OriginalSource);", split, StringComparison.Ordinal);
        Assert.Contains("ViewerB.PreviewMouseDown += (_, e) => FocusPaneFromPointer(ViewerB, e.OriginalSource);", split, StringComparison.Ordinal);
        Assert.Contains("Viewer.PreviewMouseWheel  += (_, e) => FocusPaneFromPointer(Viewer, e.OriginalSource);", split, StringComparison.Ordinal);
        Assert.Contains("ViewerB.PreviewMouseWheel += (_, e) => FocusPaneFromPointer(ViewerB, e.OriginalSource);", split, StringComparison.Ordinal);
        string hook = ShellSource.Between(split, "private void FocusPaneFromPointer", "internal void FocusPane(");
        Assert.Contains("pane.FocusReadingSurfaceFromPointer(source);", hook, StringComparison.Ordinal);

        string api = ShellSource.Read("Controls", "Viewer", "PdfViewer.Api.cs");
        string fromPointer = ShellSource.Between(api, "internal void FocusReadingSurfaceFromPointer", "internal void FocusReadingSurfaceExt");
        Assert.Contains("PointerFocus.MovesKeyboardToPage(", fromPointer, StringComparison.Ordinal);
        Assert.Contains("InWindowTree(", fromPointer, StringComparison.Ordinal);
    }
}
