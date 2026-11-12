using System.Windows.Media;
using KillerPDF.Shell;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    // Selecting a highlight, Shapes rectangle, drawing or measurement puts its look on the draw bar so the bar can
    // restyle it. The tool's own settings come back when it is deselected, so a mark on the page never becomes the
    // look of the next one (the 1.9.3 Reddit report: a 100% rectangle turned every later highlight solid). A setting
    // changed on the bar while the mark was selected is a deliberate choice and stays, the same rule the text tool
    // follows for borrowed page ink (ReleaseSelectedTextLook).
    private sealed record DrawToolLook(Color Highlight, Color Line, Color Draw, byte Opacity, double Width);
    private DrawToolLook? _drawLookBeforeSelection;
    private DrawToolLook? _selectedDrawLook;   // what the selection put on the bar

    private DrawToolLook CurrentDrawLook() => new(_highlightColor, _lineAnnotColor, _drawColor, _drawOpacity, _drawWidth);

    /// <summary>Call before a selection writes its look onto the draw bar.</summary>
    private void BorrowDrawLook() => _drawLookBeforeSelection ??= CurrentDrawLook();

    /// <summary>Call once the bar holds the selection's look.</summary>
    private void NoteSelectedDrawLook() => _selectedDrawLook = CurrentDrawLook();

    private void ReleaseSelectedDrawLook()
    {
        if (_drawLookBeforeSelection is not { } saved) return;
        var synced = _selectedDrawLook ?? CurrentDrawLook();
        _drawLookBeforeSelection = null;
        _selectedDrawLook = null;
        if (_highlightColor == synced.Highlight) _highlightColor = saved.Highlight;
        if (_lineAnnotColor == synced.Line) _lineAnnotColor = saved.Line;
        if (_drawColor == synced.Draw) _drawColor = saved.Draw;
        if (_drawOpacity == synced.Opacity) _drawOpacity = saved.Opacity;
        if (_drawWidth == synced.Width) _drawWidth = saved.Width;
        if (_currentTool is EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough or EditTool.Underline
            or EditTool.Shape or EditTool.Line or EditTool.Measure && _annotBarTool == _currentTool)
            ShowDrawSettings(_currentTool);
    }
}
