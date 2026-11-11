using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace KillerPDF.Tests;

// The Stamp and Transform windows are not linked into this project, so these pin how they are set
// up. Their behavior is exercised by the numbered window steps on a UI VM (see the lane report).
public sealed class StampWindowSourceTests
{
    internal static string Source(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Controls", file));
    }

    private static string Body(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, signature + " not found");
        int open = source.IndexOf('{', start);
        int depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced " + signature);
    }

    // controls BF-013: the preview arrows are named by their private-use glyph.
    [Fact]
    public void TheStampPreviewArrowsAreNamedPreviousAndNextPage()
    {
        string source = Source("StampWindow.cs");
        string nav = Body(source, "private Grid BuildPageNav()");
        Assert.Contains("S(\"Str_Kb_PrevPage\"), () => GoToPage(_pageIndex - 1)", nav, StringComparison.Ordinal);
        Assert.Contains("S(\"Str_Kb_NextPage\"), () => GoToPage(_pageIndex + 1)", nav, StringComparison.Ordinal);
        string arrow = Body(source, "private static Button MakeNavArrow(");
        Assert.Contains("AutomationProperties.SetName(b, name)", arrow, StringComparison.Ordinal);
    }

    // controls BF-010: Enter closed the window as Apply while Apply was disabled.
    [Fact]
    public void EnterAppliesOnlyWhileApplyIsEnabled()
    {
        string source = Source("StampWindow.cs");
        Assert.Matches(new Regex(@"e\.Key == Key\.Enter && _applyBtn\?\.IsEnabled == true"), source);
    }

    // controls BF-015 and BF-024: sizes are read through StampInput, never through NumberStyles.Any.
    [Fact]
    public void TypedSizesAreReadThroughStampInput()
    {
        string source = Source("StampWindow.cs");
        Assert.DoesNotContain("NumberStyles.Any", source, StringComparison.Ordinal);
        Assert.DoesNotContain("double.TryParse(b.Text", source, StringComparison.Ordinal);
        Assert.Contains("StampInput.ReadSize(", Body(source, "private void CommitAndClose()"), StringComparison.Ordinal);
        Assert.Contains("StampInput.NumFontMax", source, StringComparison.Ordinal);
        Assert.Contains("StampInput.WmFontMax", source, StringComparison.Ordinal);
    }

    // controls BF-023: Reset all left positions, mirroring, ranges and the image choice alone.
    [Theory]
    [InlineData("_numPos")]
    [InlineData("_wmPos")]
    [InlineData("_numMirror")]
    [InlineData("_wmRange")]
    [InlineData("_wmTextRadio")]
    [InlineData("_wmImageRadio")]
    [InlineData("_wmImagePath")]
    [InlineData("NumCustomX")]
    [InlineData("WmCustomX")]
    public void ResetAllRestoresEveryControlCommitAndCloseReads(string member)
    {
        Assert.Contains(member, Body(Source("StampWindow.cs"), "private void ResetAll()"), StringComparison.Ordinal);
    }

    // controls BF-025: a saved font that is not installed was replaced by Segoe UI on Apply.
    [Fact]
    public void ApplyKeepsASavedFontThatIsNotInstalled()
    {
        string source = Source("StampWindow.cs");
        Assert.Contains("StampInput.FontToKeep(", Body(source, "private void CommitAndClose()"), StringComparison.Ordinal);
        Assert.DoesNotContain("_spec.WmFont = _wmFont.SelectedItem", source, StringComparison.Ordinal);
        Assert.Contains("_wmFont.Items.Insert(0, _spec.WmFont)", source, StringComparison.Ordinal);
    }

    // controls BF-014: the Transform batch preview arrows are named by an angle quotation mark.
    [Fact]
    public void TheTransformPreviewArrowsAreNamedPreviousAndNextPage()
    {
        string source = Source("TransformWindow.cs");
        string arrow = Body(source, "private static Button NavArrow(");
        Assert.Contains("AutomationProperties.SetName(button, name)", arrow, StringComparison.Ordinal);
        Assert.Contains("NavArrow(\"\u2039\", S(\"Str_Kb_PrevPage\"))", source, StringComparison.Ordinal);
        Assert.Contains("NavArrow(\"\u203A\", S(\"Str_Kb_NextPage\"))", source, StringComparison.Ordinal);
    }

    // controls BF-022: a capture lost mid-drag left the level line following the pointer.
    [Fact]
    public void ALostMouseCaptureEndsTheLevelLine()
    {
        string source = Source("TransformWindow.cs");
        Assert.Matches(new Regex(@"_lineCanvas\.LostMouseCapture \+= \(_, _\) => \{ _drawingLine = false; _alignLine\.Visibility = Visibility\.Collapsed; \};"), source);
    }

    // controls BF-008: Enter in the preset name box applied the dialog through its default button.
    [Fact]
    public void EnterInThePresetNameBoxSavesThePresetInsteadOfApplying()
    {
        string source = Source("ColorManagementDialog.cs");
        Assert.Matches(new Regex(@"presetName\.PreviewKeyDown \+= .*Key\.Enter.*e\.Handled = true", RegexOptions.Singleline), source);
        Assert.Contains("savePreset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent))", source, StringComparison.Ordinal);
    }
}

