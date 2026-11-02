namespace KillerPDF.Services;

/// <summary>Where a page drop between windows is decided. Page drags carry their payload in process,
/// so a drop can come from a pane of another window.</summary>
internal static class PageDropRoute
{
    /// <summary>The window that runs the owner password gate of a limited page drag.</summary>
    internal static T GateWindow<T>(T dropWindow, T? sourceWindow) where T : class => dropWindow;

    /// <summary>Whether a window may point its chrome at a pane.</summary>
    internal static bool MayFocus(object pane, object first, object second) => true;
}
