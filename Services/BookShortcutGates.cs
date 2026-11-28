using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>Whether the Alt+1 to Alt+7, Alt+Shift+W/N and direct document tool keys act, given what is open. Alt+7
/// is the Document tools panel, whose rail button stays enabled with nothing open, so its key follows the button
/// alone. A direct document tool key (Ctrl+Shift+M, Ctrl+Shift+X) always reaches the tool list, which offers only
/// the tools that work for what is open, as the panel does.</summary>
internal static class ExpandedShortcutGate
{
    internal static bool Runs(bool hasPdf, bool hasReadingDocument, bool railButtonEnabled, Key key, ModifierKeys modifiers)
        => hasPdf
            || (key is Key.W or Key.N) && modifiers == (ModifierKeys.Alt | ModifierKeys.Shift)
            || modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && ShortcutTable.DocumentToolForDirectKey(key.ToString()) is not null
            || railButtonEnabled && modifiers == ModifierKeys.Alt && key == Key.D7
            || hasReadingDocument && railButtonEnabled && modifiers == ModifierKeys.Alt && key is >= Key.D1 and <= Key.D6;
}

/// <summary>Whether a save or print command that found no PDF tells the user to open one.</summary>
internal static class OpenFirstPrompt
{
    internal static bool Shows(bool hasPdf, bool hasBook) => hasPdf || !hasBook;
}

/// <summary>The Search Tools commands that also work on a reading document, such as an EPUB or DjVu book.</summary>
internal static class ToolSearchReadingCommands
{
    // Exactly the commands whose key handlers already act on a reading document. Editing tools stay out.
    internal static readonly HashSet<string> Labels =
    [
        "Str_View_Continuous", "Str_View_Single", "Str_View_TwoPage", "Str_View_Grid", "Str_View_BookMode",
        "Str_Lbl_Pan",
        "Str_DocInvertSetting", "Str_InvertImagesToo", "Str_Kb_FirstPage", "Str_Kb_LastPage",
        "Str_Zoom_ActualSize", "Str_Zoom_FitWidth", "Str_Zoom_FitPage", "Str_KS_FileSize", "Str_Ctx_MoveToNewWindow",
        "Str_Rail_Pages", "Str_Rail_Outline", "Str_Rail_Comments", "Str_Rail_Attachments", "Str_Rail_Layers", "Str_Rail_Signatures",
        "Str_Rail_DocumentTools",
    ];

    internal static bool Admits(bool hasReadingDocument, string labelKey) => hasReadingDocument && Labels.Contains(labelKey);
}
