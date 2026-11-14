using System.Windows.Input;

namespace KillerPDF.Services;

/// <summary>
/// The Document tools sidebar panel's sections, its column count at a width, and the key that runs a tool while
/// the panel has the keyboard. The panel (Shell/DocumentTools.cs) builds the tools that fit what is open and asks
/// this class how to lay them out, so a PDF, a book and an empty window share one arrangement.
/// </summary>
internal static class DocumentToolsLayout
{
    internal const string ReportsKey = "Str_DocTools_GroupReports";
    internal const string EditKey = "Str_DocTools_GroupEdit";
    internal const string AutomationKey = "Str_DocTools_GroupAutomation";

    /// <summary>
    /// The narrowest column a tool tile reads well in: a glyph over a name that may wrap to two lines. Three fit
    /// across the default sidebar, even with its scroll bar showing.
    /// </summary>
    internal const double MinColumnWidth = 60;

    private static readonly string[] SectionOrder = [ReportsKey, EditKey, AutomationKey];

    private static readonly Dictionary<string, string> SectionOf = new(StringComparer.Ordinal)
    {
        // Reports and checks: they read the document and say what is in it.
        ["Str_DocumentTools_Preflight"] = ReportsKey,
        ["Str_CM_Title"] = ReportsKey,
        ["Str_DocumentTools_PrintProduction"] = ReportsKey,
        ["Str_DocumentTools_Separations"] = ReportsKey,
        ["Str_DocumentTools_Accessibility"] = ReportsKey,
        ["Str_DocumentTools_Signatures"] = ReportsKey,
        ["Str_CertCatalog_Title"] = ReportsKey,
        ["Str_DocumentTools_Layers"] = ReportsKey,
        ["Str_DocumentTools_Portfolio"] = ReportsKey,
        ["Str_DocumentTools_Measurements"] = ReportsKey,
        ["Str_DocumentTools_NavigationAudit"] = ReportsKey,
        ["Str_DocumentTools_StructuralComparison"] = ReportsKey,
        // Edits to the document's structure.
        ["Str_NativePdf_Convert"] = EditKey,
        ["Str_DocumentTools_EditLayers"] = EditKey,
        ["Str_DocumentTools_EditPortfolio"] = EditKey,
        ["Str_DocumentTools_EditPortfolioStructure"] = EditKey,
        ["Str_DocumentTools_EditAttachments"] = EditKey,
        ["Str_DocumentTools_Comments"] = EditKey,
        ["Str_DocumentTools_AutoBookmarks"] = EditKey,
        ["Str_DocumentTools_PageLabels"] = EditKey,
        ["Str_DocumentTools_InitialView"] = EditKey,
        ["Str_DocumentTools_InsertToc"] = EditKey,
        // Repeatable work and data in or out.
        ["Str_Guided_Title"] = AutomationKey,
        ["Str_DocumentTools_Macros"] = AutomationKey,
        ["Str_DocumentTools_DataMerge"] = AutomationKey,
        ["Str_DocumentTools_FormData"] = AutomationKey,
        ["Str_DocumentTools_BookmarkData"] = AutomationKey,
        ["Str_DocumentTools_ProductionLayouts"] = AutomationKey,
    };

    /// <summary>
    /// Sorts <paramref name="tools"/> (label keys) into the panel's sections, keeping their order within each.
    /// A section with nothing in it is left out. A tool with no section is refused, so a new tool cannot
    /// silently vanish from the panel.
    /// </summary>
    internal static IReadOnlyList<(string TitleKey, IReadOnlyList<string> Tools)> Group(IEnumerable<string> tools)
    {
        var sections = SectionOrder.ToDictionary(key => key, _ => new List<string>(), StringComparer.Ordinal);
        foreach (string tool in tools)
        {
            if (!SectionOf.TryGetValue(tool, out string? section))
                throw new ArgumentException("The Document tools panel has no section for " + tool + ".", nameof(tools));
            sections[section].Add(tool);
        }
        return [.. SectionOrder.Where(key => sections[key].Count > 0)
            .Select(key => (key, (IReadOnlyList<string>)sections[key]))];
    }

    /// <summary>How many columns of tool rows fit across <paramref name="width"/> pixels, at least one.</summary>
    internal static int Columns(double width)
        => double.IsFinite(width) && width > 0 ? Math.Max(1, (int)Math.Floor(width / MinColumnWidth)) : 1;

    /// <summary>
    /// The tool key a key press stands for while the panel has the keyboard: a letter or zero, bare or with Shift.
    /// Right after Ctrl+Shift+D (<paramref name="chordJustPressed"/>) Ctrl and Shift may still be held from the
    /// chord, so Ctrl+Shift counts too. Null for anything else, so Ctrl+S and the Alt keys stay the window's.
    /// </summary>
    internal static char? ChordKey(Key key, ModifierKeys modifiers, bool chordJustPressed)
    {
        if (modifiers is not (ModifierKeys.None or ModifierKeys.Shift)
            && !(chordJustPressed && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))) return null;
        if (key is Key.D0 or Key.NumPad0) return '0';
        if (key is >= Key.A and <= Key.Z) return (char)('A' + (key - Key.A));
        return null;
    }
}
