using System.Windows.Input;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// #485: text marking follows the text under the pointer and the text already selected.
public sealed class TextMarkupToolPolicyTests
{
    [Theory]
    [InlineData(EditTool.Highlight, true, true)]
    [InlineData(EditTool.Underline, true, true)]
    [InlineData(EditTool.Strikethrough, true, true)]
    [InlineData(EditTool.Highlight, false, false)]
    [InlineData(EditTool.Draw, true, false)]
    [InlineData(EditTool.Select, true, false)]
    [InlineData(EditTool.Redact, true, false)]
    public void ChoosingAMarkingToolMarksTheSelectedText(EditTool tool, bool hasSelection, bool marks) =>
        Assert.Equal(marks, TextMarkupToolPolicy.MarksSelectionOnChoose(tool, hasSelection));

    [Fact]
    public void AMarkingToolShowsAnIBeamOverTextAndACrossElsewhere()
    {
        Assert.Same(Cursors.IBeam, TextMarkupToolPolicy.Cursor(EditTool.Highlight, erasing: false, overText: true));
        Assert.Same(Cursors.Cross, TextMarkupToolPolicy.Cursor(EditTool.Highlight, erasing: false, overText: false));
        Assert.Same(Cursors.IBeam, TextMarkupToolPolicy.Cursor(EditTool.Underline, erasing: false, overText: true));
        Assert.Same(Cursors.IBeam, TextMarkupToolPolicy.Cursor(EditTool.Strikethrough, erasing: false, overText: true));
    }

    [Fact]
    public void TheHighlightEraserKeepsItsCrossOverText() =>
        Assert.Same(Cursors.Cross, TextMarkupToolPolicy.Cursor(EditTool.Highlight, erasing: true, overText: true));

    // #514: image-only pages have no text to follow, so a Highlight drag there marks the box it covers.
    // Underline and Strikethrough need a line of text and keep the hint.
    [Theory]
    [InlineData(EditTool.Highlight, true)]
    [InlineData(EditTool.Underline, false)]
    [InlineData(EditTool.Strikethrough, false)]
    [InlineData(EditTool.Draw, false)]
    public void OnlyHighlightDrawsABoxWhereThereIsNoText(EditTool tool, bool box) =>
        Assert.Equal(box, TextMarkupToolPolicy.DrawsBoxOffText(tool));

    [Fact]
    public void AHighlightPressOffTextStartsTheBoxInsteadOfTheHint()
    {
        string annotations = ShellSource.Read("Controls", "Viewer", "PdfViewer.Annotations.cs");
        string press = ShellSource.Between(annotations, "on text these tools FLOW", "case EditTool.Shape:");
        int box = press.IndexOf("TextMarkupToolPolicy.DrawsBoxOffText(_currentTool)", StringComparison.Ordinal);
        int hint = press.IndexOf("Str_St_NoTextUseShapes", StringComparison.Ordinal);
        Assert.True(box >= 0, "The press handler does not ask whether the tool draws a box off text.");
        Assert.True(hint > box, "The no-text hint should only show for tools that do not draw a box.");
    }

    [Fact]
    public void OnlyMarkingToolsFollowTheTextUnderThePointer()
    {
        Assert.True(TextMarkupToolPolicy.FollowsText(EditTool.Highlight));
        Assert.False(TextMarkupToolPolicy.FollowsText(EditTool.Draw));
        Assert.False(TextMarkupToolPolicy.FollowsText(EditTool.Select));
    }
}
