using System.Windows.Media;

namespace KillerPDF.Controls;

// The draw bar's look (PdfViewer.DrawLook.cs): the highlighter, line and pen colors, the opacity and the width.
internal sealed record DrawToolLook(Color Highlight, Color Line, Color Draw, byte Opacity, double Width)
{
    /// <summary>
    /// Field by field, a value changed on the bar since the selection put its look there is a deliberate choice and
    /// wins; a value nobody touched goes back to the tool's own.
    /// </summary>
    internal static DrawToolLook Settle(DrawToolLook toolLook, DrawToolLook synced, DrawToolLook current) => new(
        current.Highlight == synced.Highlight ? toolLook.Highlight : current.Highlight,
        current.Line == synced.Line ? toolLook.Line : current.Line,
        current.Draw == synced.Draw ? toolLook.Draw : current.Draw,
        current.Opacity == synced.Opacity ? toolLook.Opacity : current.Opacity,
        current.Width == synced.Width ? toolLook.Width : current.Width);
}

// A selected mark borrows the draw bar: Borrow before the selection writes its look, Note once the bar holds it, and
// Release when the mark is deselected, which hands back the look the bar should return to.
internal sealed class DrawLookLease
{
    private DrawToolLook? _toolLook;
    private DrawToolLook? _synced;

    internal bool IsActive => _toolLook is not null;

    internal void Borrow(DrawToolLook current)
    {
        // A mark selected again while still selected (a resize, a card change) keeps what the user chose on the bar
        // in between as the look to return to.
        _toolLook = _toolLook is { } toolLook && _synced is { } synced ? DrawToolLook.Settle(toolLook, synced, current)
            : _toolLook ?? current;
    }

    internal void Note(DrawToolLook current) => _synced = current;

    internal DrawToolLook? Release(DrawToolLook current)
    {
        if (_toolLook is not { } toolLook) return null;
        var synced = _synced ?? current;
        _toolLook = null;
        _synced = null;
        return DrawToolLook.Settle(toolLook, synced, current);
    }
}
