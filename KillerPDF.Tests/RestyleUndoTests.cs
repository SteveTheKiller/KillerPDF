using Xunit;

namespace KillerPDF.Tests;

// Changing a selected annotation's color, size, opacity or font from the options bar is one undo step per
// gesture: a swatch pick or toggle is one step, and a slider drag or key press sequence is one step, taken
// before the first change, the way the Blur slider does it.
public sealed class RestyleUndoTests
{
    [Theory]
    [InlineData("private void ApplyTextStyleToSelection(", "private void ApplyDrawStyleToSelection(")]
    [InlineData("private void ApplyDrawStyleToSelection(", "private void ShowTextSettings()")]
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
        // The snapshot covers every page of the selection in one undo entry.
        string helper = ShellSource.Between(ShellSource.Read("Shell", "TextSettingsBar.cs"),
            "private void PushStyleUndo(", "private void ApplyTextStyleToSelection(");
        Assert.Contains("PushPagesSnapshotUndoExt(annotations.Select(a => a.PageIndex))", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("foreach", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("PushPageSnapshotUndoExt(", helper, StringComparison.Ordinal);
    }

    // No slider is found by guessing from the mouse capture or the keys down: each slider on the text and draw bars
    // owns the SliderGesture made for it and hands it to the apply call, as the Blur slider does.
    [Fact]
    public void ASliderDragIsOneStepNotOnePerTick()
    {
        string bar = ShellSource.Read("Shell", "TextSettingsBar.cs");
        string bars = ShellSource.Read("Shell", "AnnotationBars.cs");

        Assert.DoesNotContain("ActiveSliderGesture", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Mouse.Captured", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Keyboard.IsKeyDown", bar, StringComparison.Ordinal);
        string helper = ShellSource.Between(bar, "private void PushStyleUndo(", "private void ApplyTextStyleToSelection(");
        Assert.Contains("gesture is not null && !gesture.Starts()", helper, StringComparison.Ordinal);

        foreach (string slider in new[] { "sizeSlider", "opacitySlider", "fillOpSlider", "spacingSlider" })
            Assert.Contains($"new Controls.SliderGesture({slider})", bar, StringComparison.Ordinal);
        foreach (string gesture in new[] { "sizeGesture", "opacityGesture", "fillOpGesture", "spacingGesture" })
            Assert.Contains($"ApplyTextStyleToSelection({gesture})", bar, StringComparison.Ordinal);

        foreach (string slider in new[] { "sizeSlider", "opacitySlider" })
            Assert.Contains($"new Controls.SliderGesture({slider})", bars, StringComparison.Ordinal);
        foreach (string gesture in new[] { "sizeGesture", "opacityGesture" })
            Assert.Contains($"ApplyDrawStyleToSelection({gesture})", bars, StringComparison.Ordinal);
    }
}