using System.Windows.Media;

namespace KillerPDF.Controls;

// The draw bar's look (PdfViewer.DrawLook.cs): the highlighter, line and pen colors, the opacity and the width.
internal sealed record DrawToolLook(Color Highlight, Color Line, Color Draw, byte Opacity, double Width)
{
    /// <summary>
    /// Setting by setting, a value changed on the bar since the selection put its look there is a deliberate choice and
    /// wins; a value nobody touched goes back to the tool's own. A color's hue and its alpha are separate settings: the
    /// bar writes a picked color with the mark's alpha and an opacity drag rewrites only the alpha, so comparing whole
    /// colors would carry the mark's opacity (or its hue) into the tool (1.9.4 review r2 BF-001).
    /// </summary>
    internal static DrawToolLook Settle(DrawToolLook toolLook, DrawToolLook synced, DrawToolLook current) => new(
        Settle(toolLook.Highlight, synced.Highlight, current.Highlight),
        Settle(toolLook.Line, synced.Line, current.Line),
        Settle(toolLook.Draw, synced.Draw, current.Draw),
        current.Opacity == synced.Opacity ? toolLook.Opacity : current.Opacity,
        current.Width == synced.Width ? toolLook.Width : current.Width);

    private static Color Settle(Color toolColor, Color synced, Color current)
    {
        bool hueChanged = current.R != synced.R || current.G != synced.G || current.B != synced.B;
        var hue = hueChanged ? current : toolColor;
        return Color.FromArgb(current.A != synced.A ? current.A : toolColor.A, hue.R, hue.G, hue.B);
    }
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

    /// <summary>The look the bar returns to on release, without releasing: what settings save while a mark is selected.</summary>
    internal DrawToolLook Peek(DrawToolLook current) =>
        _toolLook is { } toolLook ? DrawToolLook.Settle(toolLook, _synced ?? current, current) : current;

    internal DrawToolLook? Release(DrawToolLook current)
    {
        if (_toolLook is not { } toolLook) return null;
        var synced = _synced ?? current;
        _toolLook = null;
        _synced = null;
        return DrawToolLook.Settle(toolLook, synced, current);
    }
}
