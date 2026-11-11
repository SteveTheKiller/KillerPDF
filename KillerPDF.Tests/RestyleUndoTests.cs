using Xunit;

namespace KillerPDF.Tests;

// Changing a selected annotation's color, size, opacity or font from the options bar is one undo step per
// gesture: a swatch pick or toggle is one step, and a slider drag or key press sequence is one step, taken
// before the first change, the way the Blur slider does it.
public sealed class RestyleUndoTests
{
    [Theory]
    [InlineData("private void ApplyTextStyleToSelection()", "private void ApplyDrawStyleToSelection()")]
    [InlineData("private void ApplyDrawStyleToSelection()", "private void ShowTextSettings()")]
    public void RestylingTakesAPageSnapshotBeforeItChangesAnything(string start, string end)
    {
        string bar = ShellSource.Read("Shell", "TextSettingsBar.cs");
        string method = ShellSource.Between(bar, start, end);

        int snapshot = method.IndexOf("PushStyleUndo(", StringComparison.Ordinal);
        Assert.True(snapshot >= 0, "no undo snapshot in " + start);
        Assert.True(snapshot < method.IndexOf("MarkDirty()", StringComparison.Ordinal), "the snapshot follows MarkDirty");
        // Nothing is restyled before it: the only earlier mention of Apply is the local function itself.
        Assert.DoesNotMatch(new System.Text.RegularExpressions.Regex(@"(?<!void )\bApply\("), method[..snapshot]);
        Assert.Contains("Apply(", method[snapshot..], StringComparison.Ordinal);
        // The snapshot is the page's annotations, taken once per page of the selection.
        string helper = ShellSource.Between(ShellSource.Read("Shell", "TextSettingsBar.cs"),
            "private void PushStyleUndo(", "private void ApplyTextStyleToSelection()");
        Assert.Contains("PushPageSnapshotUndoExt(page)", helper, StringComparison.Ordinal);    }

    [Fact]
    public void ASliderDragIsOneStepNotOnePerTick()
    {
        string bar = ShellSource.Read("Shell", "TextSettingsBar.cs");
        string gesture = ShellSource.Between(bar, "SliderGesture? ActiveSliderGesture()", "private void ApplyTextStyleToSelection()");

        Assert.Contains("Mouse.Captured", gesture, StringComparison.Ordinal);
        Assert.Contains("new Controls.SliderGesture(", gesture, StringComparison.Ordinal);
        Assert.Contains(".Starts()", bar, StringComparison.Ordinal);
    }
}