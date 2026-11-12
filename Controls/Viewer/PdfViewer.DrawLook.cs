using KillerPDF.Shell;

namespace KillerPDF.Controls;

public partial class PdfViewer
{
    // Selecting a highlight, Shapes rectangle, drawing or measurement puts its look on the draw bar so the bar can
    // restyle it. The tool's own settings come back when it is deselected, so a mark on the page never becomes the
    // look of the next one (the 1.9.3 Reddit report: a 100% rectangle turned every later highlight solid). A setting
    // changed on the bar while the mark was selected is a deliberate choice and stays, the same rule the text tool
    // follows for borrowed page ink (ReleaseSelectedTextLook). The rules live in DrawToolLook.cs.
    private readonly DrawLookLease _drawLookLease = new();

    private DrawToolLook CurrentDrawLook() => new(_highlightColor, _lineAnnotColor, _drawColor, _drawOpacity, _drawWidth);

    private static bool IsDrawBarTool(EditTool? tool) => tool is EditTool.Draw or EditTool.Highlight or EditTool.Strikethrough
        or EditTool.Underline or EditTool.Shape or EditTool.Line or EditTool.Measure;

    /// <summary>
    /// Opens the draw bar synced to a selected highlight, Shapes rectangle, drawing or measurement, so its color,
    /// opacity and width can be edited in place. A highlight's style picks the matching tool; a filled Shapes rectangle
    /// opens the Shapes bar, so its opacity never becomes the highlighter's. Returns false for any other mark.
    /// </summary>
    private bool SyncDrawBarToSelection(PageAnnotation annot)
    {
        EditTool tool;
        switch (annot)
        {
            case HighlightAnnotation { IsShape: true } shape:
                _drawLookLease.Borrow(CurrentDrawLook());
                _drawColor = shape.GetColor();
                _drawOpacity = shape.GetColor().A;
                tool = EditTool.Shape;
                break;
            case HighlightAnnotation highlight:
                _drawLookLease.Borrow(CurrentDrawLook());
                if (highlight.Style == HighlightStyle.Fill) _highlightColor = highlight.GetColor();
                else _lineAnnotColor = highlight.GetColor();
                tool = highlight.Style switch
                {
                    HighlightStyle.Strikethrough => EditTool.Strikethrough,
                    HighlightStyle.Underline => EditTool.Underline,
                    _ => EditTool.Highlight
                };
                break;
            case InkAnnotation ink:
                _drawLookLease.Borrow(CurrentDrawLook());
                _drawColor = ink.GetColor();
                _drawOpacity = ink.GetColor().A;
                _drawWidth = ink.StrokeWidth;
                tool = EditTool.Draw;
                break;
            case MeasurementAnnotation measurement:
                _drawLookLease.Borrow(CurrentDrawLook());
                _drawColor = measurement.GetColor();
                _drawOpacity = measurement.GetColor().A;
                _drawWidth = measurement.StrokeWidth;
                tool = EditTool.Measure;
                break;
            default:
                return false;
        }
        _drawLookLease.Note(CurrentDrawLook());
        if (!KeepsOwnBar(_currentTool)) ShowDrawSettings(tool);
        return true;
    }

    /// <summary>
    /// Redact and Crop keep their own bars: their Apply, Clear and confirm controls never give way to a selected mark's
    /// bar. The selection still lends its look, because switching tools keeps it selected and the next bar change must
    /// start from the mark (1.9.3 review r3 BF-002, r4 BF-001).
    /// </summary>
    private static bool KeepsOwnBar(EditTool tool) => tool is EditTool.Redact or EditTool.Crop;

    /// <summary>
    /// The markup card restyled the selection: the bar the selection opened shows the mark's new look, so the next
    /// bar change starts from it instead of writing the old look back (review BF-001, BF-002).
    /// </summary>
    internal void RefreshSelectedDrawBar()
    {
        if (_selectedAnnotation is { } selected && _drawLookLease.IsActive) SyncDrawBarToSelection(selected);
    }

    /// <summary>
    /// What settings save for the draw tools: the look the bar returns to, not a selected mark's. It releases
    /// nothing, because a close or sign-out that saves settings can still be cancelled (1.9.3 review r2 BF-002, r3 BF-001).
    /// </summary>
    internal DrawToolLook DrawLookToSave() => _drawLookLease.Peek(CurrentDrawLook());

    private void ReleaseSelectedDrawLook(bool refreshBar = true)
    {
        if (_drawLookLease.Release(CurrentDrawLook()) is not { } look) return;
        (_highlightColor, _lineAnnotColor, _drawColor, _drawOpacity, _drawWidth) = (look.Highlight, look.Line, look.Draw, look.Opacity, look.Width);
        // Whichever draw bar the selection left up, the active draw tool's own bar comes back showing its own look.
        if (refreshBar && IsDrawBarTool(_currentTool) && IsDrawBarTool(_annotBarTool)) ShowDrawSettings(_currentTool);
    }
}
