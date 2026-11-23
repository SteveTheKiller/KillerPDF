using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>How the text-marking tools (Highlight, Underline, Strikethrough) treat text. (#485)</summary>
internal static class TextMarkupToolPolicy
{
    /// <summary>The tools that mark text by dragging across it.</summary>
    internal static bool FollowsText(EditTool tool) =>
        tool is EditTool.Highlight or EditTool.Underline or EditTool.Strikethrough;

    /// <summary>Text selected before a marking tool is chosen is marked at once, as people select first
    /// and decide what to do second.</summary>
    internal static bool MarksSelectionOnChoose(EditTool tool, bool hasSelection) =>
        hasSelection && FollowsText(tool);

    /// <summary>Where there is no text to follow (scans, pictures, margins), a Highlight drag marks the box it
    /// covers, like a highlighter on paper. Underline and Strikethrough need a line of text. (#514)</summary>
    internal static bool DrawsBoxOffText(EditTool tool) => tool == EditTool.Highlight;

    /// <summary>An I-beam over text says a drag will mark it; a cross elsewhere says it draws a box.
    /// The highlight eraser removes marks, not text, so it keeps its cross.</summary>
    internal static Cursor Cursor(EditTool tool, bool erasing, bool overText) =>
        overText && !erasing && FollowsText(tool) ? Cursors.IBeam : Cursors.Cross;
}
