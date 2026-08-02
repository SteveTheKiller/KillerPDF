namespace KillerPDF
{
    /// <summary>
    /// One link rectangle on a page, in render-dim coordinates.
    ///
    /// Used by the tiled views (continuous, grid, two-page), where a per-link overlay would swallow
    /// the click without its own handler ever firing - so clicks and the hover cursor are resolved
    /// by bounds-testing these rects instead. That makes them the source of truth for links outside
    /// single-page view.
    ///
    /// TOP-LEVEL, not nested. Links.cs lives in the viewer control while ContextMenu.cs lives on the
    /// window and also bounds-tests these rects to build the right-click menu, so neither class can
    /// own the type.
    /// </summary>
    internal readonly record struct LinkInfo(
        double Cx, double Cy, double Cw, double Ch, object Tag, string Tip, int AnnotIndex);
}
