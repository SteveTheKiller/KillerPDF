using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>What a key press does inside an in-place text box.</summary>
internal enum TextEnterAction
{
    /// <summary>Not a line or save key: the box or the window handles it as usual.</summary>
    None,
    /// <summary>Adds a line. The box inserts the break itself, so the press is left unhandled.</summary>
    NewLine,
    /// <summary>Saves the text and closes the box.</summary>
    Commit,
}

/// <summary>
/// #486: which keys add a line and which save an in-place text box. The default is the one the
/// text tool always had: Enter adds a line and Ctrl+Enter saves. The other choice is Enter saves
/// and Shift+Enter adds a line. Shift+Enter adds a line and Ctrl+Enter saves in both, so neither
/// choice takes a key away.
/// </summary>
internal static class TextEnterKeys
{
    /// <summary>The setting that holds the choice: "1" when Enter saves.</summary>
    internal const string SettingKey = "TextEnterCommits";

    internal static TextEnterAction Decide(Key key, ModifierKeys modifiers, bool enterCommits)
    {
        // Only Enter itself. A press the IME is composing arrives as Key.ImeProcessed and must
        // finish the composition, never save the box.
        if (key != Key.Enter) return TextEnterAction.None;
        // Windows reports AltGr as Ctrl+Alt, and AltGr+Enter types on some layouts.
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return TextEnterAction.None;
        if ((modifiers & ModifierKeys.Control) != 0) return TextEnterAction.Commit;
        if ((modifiers & ModifierKeys.Shift) != 0) return TextEnterAction.NewLine;
        return enterCommits ? TextEnterAction.Commit : TextEnterAction.NewLine;
    }

    /// <summary>
    /// Whether leaving the box saves it. Text that was typed is always kept. An emptied edit of the
    /// page's own text, or of a line under a cover, also resolves, so its cover never lingers. An
    /// emptied fresh box or an emptied re-edit of a placed text stays open: closing it would drop
    /// the placed text with no undo step, and Escape is the only key that throws an edit away.
    /// </summary>
    internal static bool CommitsOnLeave(bool hasText, bool pendingCover, bool pendingDirectEdit) =>
        hasText || pendingCover || pendingDirectEdit;

    /// <summary>
    /// Whether a mouse press elsewhere in the window saves the box. A press on the box itself, on
    /// its resize corners, on the text options bar or inside a popup (a dropdown of that bar) is
    /// still part of the edit. Everything else is clicking away: the page, another page, another
    /// annotation, the toolbar, the sidebar, a scroll bar or a tab.
    /// </summary>
    internal static bool PressLeavesEdit(bool onBox, bool onResizeCorner, bool onOptionsBar,
        bool inPopup, bool optionsDropDownOpen, bool resizing) =>
        !(onBox || onResizeCorner || onOptionsBar || inPopup || optionsDropDownOpen || resizing);

    /// <summary>
    /// Whether the keyboard leaving the box saves it. Focus on the text options bar, in a popup drawn
    /// from it, or with its dropdown or More settings menu open is still part of the edit: saving
    /// there rebuilt the bar under the open menu and closed it (#467).
    /// </summary>
    internal static bool FocusMoveLeavesEdit(bool focusInBar, bool focusInPopup,
        bool optionsDropDownOpen, bool optionsMenuOpen) =>
        !(focusInBar || focusInPopup || optionsDropDownOpen || optionsMenuOpen);

    /// <summary>
    /// A box's LostFocus commit runs later, at Background priority. By then the click that moved the
    /// focus may have saved that box and opened another, and the late commit must leave the new box
    /// alone: it used to save it straight away, so a new box closed as soon as it opened.
    /// </summary>
    internal static bool LateCommitStillApplies(object? activeBox, object? boxThatLeft) =>
        activeBox is not null && ReferenceEquals(activeBox, boxThatLeft);
}

/// <summary>The saved choice, shared by every window, with a change event so the bar and the
/// shortcuts overlay of each window follow it.</summary>
internal sealed class TextEnterModeStore(Func<string?> read, Action<string> write)
{
    private bool? _enterCommits;

    internal event Action? Changed;

    internal bool EnterCommits => _enterCommits ??= read() == "1";

    internal void Set(bool enterCommits)
    {
        if (EnterCommits == enterCommits) return;
        _enterCommits = enterCommits;
        write(enterCommits ? "1" : "0");
        Changed?.Invoke();
    }
}
