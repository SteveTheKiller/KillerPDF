using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Services;

/// <summary>Which characters of a page's text runs show inside a screen viewport, for the screen
/// reader's visible ranges. Free of the viewer so the mapping can be checked without a window.</summary>
internal static class VisibleTextGeometry
{
    /// <summary>The caret runs [Start, End) whose glyph boxes overlap <paramref name="viewport"/>.
    /// <paramref name="toScreen"/> maps a page canvas point to the screen the way
    /// Visual.PointToScreen does, which rounds to whole device pixels.</summary>
    internal static List<(int Start, int End)> VisibleCarets(PageTextRuns runs, double canvasWidth, double canvasHeight,
        int rotation, Func<Point, Point> toScreen, Rect viewport)
    {
        // One canvas-to-screen mapping per page instead of two visual-tree walks per character;
        // the mapping is affine, so three points define it. They are a whole page apart: screen
        // points are whole pixels, and a canvas unit is usually under half a pixel, so a one-unit
        // step rounds to nothing and every character would map to an empty box.
        Point origin = toScreen(new Point());
        Vector unitX = (toScreen(new Point(canvasWidth, 0)) - origin) / canvasWidth;
        Vector unitY = (toScreen(new Point(0, canvasHeight)) - origin) / canvasHeight;
        var matrix = new Matrix(unitX.X, unitX.Y, unitY.X, unitY.Y, origin.X, origin.Y);
        var result = new List<(int Start, int End)>();
        for (int caret = 0; caret < runs.Chars.Count; caret++)
        {
            RunChar character = runs.Chars[caret];
            if (character.Line < 0 || character.Line >= runs.Lines.Count) continue;
            RunLine line = runs.Lines[character.Line];
            var bounds = TextRunService.CharacterBounds(character, line);
            Rect rect = RedactionGeometry.PageToCanvas(bounds.Left, bounds.Bottom, bounds.Right, bounds.Top,
                runs.PdfWidth, runs.PdfHeight, canvasWidth, canvasHeight, rotation);
            Rect visible = Rect.Intersect(viewport, Rect.Transform(rect, matrix));
            if (visible.IsEmpty || visible.Width <= 0 || visible.Height <= 0) continue;
            if (result.Count > 0 && result[^1].End == caret) result[^1] = (result[^1].Start, caret + 1);
            else result.Add((caret, caret + 1));
        }
        return result;
    }
}
