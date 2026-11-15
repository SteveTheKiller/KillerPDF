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

    [Fact]
    public void OnlyMarkingToolsFollowTheTextUnderThePointer()
    {
        Assert.True(TextMarkupToolPolicy.FollowsText(EditTool.Highlight));
        Assert.False(TextMarkupToolPolicy.FollowsText(EditTool.Draw));
        Assert.False(TextMarkupToolPolicy.FollowsText(EditTool.Select));
    }
}
