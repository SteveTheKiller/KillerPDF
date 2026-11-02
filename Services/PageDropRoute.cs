namespace KillerPDF.Services;

/// <summary>Where a page drop between windows is decided. Page drags carry their payload in process,
/// so a drop can come from a pane of another window.</summary>
internal static class PageDropRoute
{
    /// <summary>The window that runs the owner password gate of a limited page drag: the one that holds
    /// the source tab, so its own chrome, panes and unlock reopen describe its own document. A source
    /// whose window cannot be found leaves the gate with the window that took the drop.</summary>
    internal static T GateWindow<T>(T dropWindow, T? sourceWindow) where T : class => sourceWindow ?? dropWindow;

    /// <summary>Whether a window may point its chrome at a pane: only at one of its own two.</summary>
    internal static bool MayFocus(object pane, object first, object second) =>
        ReferenceEquals(pane, first) || ReferenceEquals(pane, second);
}
