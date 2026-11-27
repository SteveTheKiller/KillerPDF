namespace KillerPDF.Services;

/// <summary>
/// Whether Alt+drag moves KillerPDF's main window. Full screen stays put, and the Measure tool places a point with
/// Alt held to turn snapping off, so the move stands aside while Measure is the tool.
/// </summary>
internal static class AltDragTools
{
    internal static bool MovesWindow(EditTool tool, bool fullScreen) => !fullScreen && tool != EditTool.Measure;
}
