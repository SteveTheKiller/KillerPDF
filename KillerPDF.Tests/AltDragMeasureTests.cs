using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// Alt+click in the Measure tool places a point with snapping off, so Alt+drag's window move stands aside while
// Measure is the tool. Every other tool keeps the move, and full screen never moves.
public sealed class AltDragMeasureTests
{
    [Theory]
    [InlineData(EditTool.Measure, false, false)]
    [InlineData(EditTool.Select, false, true)]
    [InlineData(EditTool.Draw, false, true)]
    [InlineData(EditTool.Shape, false, true)]
    [InlineData(EditTool.Select, true, false)]
    [InlineData(EditTool.Measure, true, false)]
    public void AltDragMovesTheWindowUnlessMeasureOrFullScreen(EditTool tool, bool fullScreen, bool moves)
        => Assert.Equal(moves, AltDragTools.MovesWindow(tool, fullScreen));

    [Fact]
    public void ChoosingAToolAndFullScreenBothSetAltDragFromTheSameRule()
    {
        const string apply = "ApplyAltDrag();";
        string setTool = ShellSource.Between(ShellSource.Read("Shell", "ToolSelection.cs"),
            "private void SetTool(EditTool tool, bool restoringPane = false)", "var map = new");
        Assert.True(setTool.IndexOf(apply, StringComparison.Ordinal) > setTool.IndexOf("_currentTool = tool;", StringComparison.Ordinal));
        Assert.Contains("private void ApplyAltDrag() => Controls.AltDrag.SetIsEnabled(this, KillerPDF.Services.AltDragTools.MovesWindow(_currentTool, _fullScreen));",
            ShellSource.Read("Shell", "ToolSelection.cs"));

        string fullScreen = ShellSource.Read("Shell", "FullScreen.cs");
        Assert.DoesNotContain("AltDrag.SetIsEnabled(this, true)", fullScreen);
        Assert.DoesNotContain("AltDrag.SetIsEnabled(this, false)", fullScreen);
        Assert.Equal(2, fullScreen.Split(apply).Length - 1);
    }
}
