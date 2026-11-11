using Xunit;
using static KillerPDF.Tests.ViewerSourcePin;

namespace KillerPDF.Tests;

// SOURCE PINS (SHELL-002 remainder). Neither the viewer nor the main window is compiled into this project, so the
// behavior evidence is the written window check in the 1.9.3 notes (needs-vm).
public sealed class SplitPaneFormFieldFocusPinTests
{
    // Delete in a pane acts on that pane's own selected form field. A pane that loses focus to the other pane must
    // not keep a selected field, or a later Delete in it would remove a field the user has moved away from.
    [Fact]
    public void APaneThatLosesFocusToTheOtherPaneClearsItsSelectedFormField()
    {
        string split = Source("Shell", "SplitPane.cs");
        string focus = Between(split, "internal void FocusPane(PdfViewer pane)", "ApplyFocusHalo();");

        int clear = focus.IndexOf("ActiveViewer.ClearSelectedFormFieldExt();", StringComparison.Ordinal);
        int capture = focus.IndexOf("ActiveViewer.CaptureActiveIfAny();", StringComparison.Ordinal);
        int swap = focus.IndexOf("ActiveViewer = pane;", StringComparison.Ordinal);
        Assert.True(clear >= 0, "FocusPane must clear the outgoing pane's selected form field.");
        Assert.True(clear < swap && capture >= 0 && swap > capture, "The clear must run on the outgoing pane, before ActiveViewer changes.");
        Assert.Equal(1, Count(split, "ClearSelectedFormFieldExt()"));
    }

    [Fact]
    public void ClearingTheSelectedFormFieldAlsoDropsItsSizeBar()
    {
        string forms = Viewer("PdfViewer.Forms.cs");
        string api = Viewer("PdfViewer.Api.cs");
        string clear = Between(forms, "internal void ClearSelectedFormField()", "// The box that edits the selected field");

        Assert.Contains("_selectedFormFieldName = null;", clear);
        Assert.Contains("HideFormSizeBar();", clear);
        Assert.Contains("internal void ClearSelectedFormFieldExt() => ClearSelectedFormField();", api);
    }
}
