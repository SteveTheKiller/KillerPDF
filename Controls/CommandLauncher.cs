namespace KillerPDF.Controls;

// The document tool launchers (DocumentCommandDialog): one icon tile per command, in a grid sized so its last row
// never holds a lone tile. Icons are Segoe MDL2 code points, keyed by the command's label string key; the tiles in
// one launcher never share an icon.
internal static class CommandLauncher
{
    private static readonly Dictionary<string, int> Glyphs = new(StringComparer.Ordinal)
    {
        // Production layouts and Bates batches
        ["Str_Production_NUp"] = 0xE80A,
        ["Str_Production_Booklet"] = 0xE736,
        ["Str_Production_Repeat"] = 0xE8C8,
        ["Str_Production_CutStack"] = 0xE8C6,
        ["Str_Production_Manual"] = 0xE8CB,
        ["Str_Production_Poster"] = 0xE740,
        ["Str_Production_Bates"] = 0xE8EF,
        // Form data: export and import are the family's upload and download arrows, which read apart at a glance.
        ["Str_Exchange_ExportForms"] = 0xE898,
        ["Str_Exchange_ImportForms"] = 0xE896,
        ["Str_FormTools_Recognize"] = 0xE721,
        ["Str_FormTools_ConvertXfa"] = 0xE8AB,
        ["Str_FormTools_Flatten"] = 0xE81E,
        // Bookmark data
        ["Str_Exchange_ExportBookmarks"] = 0xE898,
        ["Str_Exchange_ImportBookmarks"] = 0xE896,
        // Preflight and its repairs. Preflight shows a report, so it has the report icon.
        ["Str_DocumentTools_Preflight"] = ReportGlyph,
        ["Str_DocumentTools_PreflightProfile"] = 0xE8E5,
        ["Str_Repair_Language"] = 0xE774,
        ["Str_Repair_Metadata"] = 0xE946,
        ["Str_Repair_PageBox"] = 0xE7A8,
        ["Str_Repair_OutputIntent"] = 0xE790,
        // Page boxes to clear: bleed reaches past the page, trim is the cut, art is the artwork.
        ["Str_Repair_Bleed"] = 0xE740,
        ["Str_Repair_Trim"] = 0xE7A8,
        ["Str_Repair_Art"] = 0xE8B9,
        // Missing metadata fields
        ["Str_DocInfo_Title"] = 0xE8D2,
        ["Str_DocInfo_Author"] = 0xE77B,
        ["Str_DocInfo_Subject"] = 0xE8BD,
        ["Str_DocInfo_Keywords"] = 0xE8EC,
    };

    // A command with no icon of its own gets the report icon, never the plain square of the generic document glyph.
    private const int ReportGlyph = 0xE9F9;

    internal static bool HasGlyph(string labelKey) => Glyphs.ContainsKey(labelKey);

    internal static int CodePointFor(string labelKey) => Glyphs.TryGetValue(labelKey, out int glyph) ? glyph : ReportGlyph;

    internal static string GlyphFor(string labelKey) => char.ConvertFromUtf32(CodePointFor(labelKey));

    /// <summary>Columns for a launcher grid: up to four across, never leaving one tile alone on the last row.</summary>
    internal static int Columns(int commands)
    {
        if (commands <= 3) return Math.Max(1, commands);
        if (commands == 4) return 2;
        if (commands % 3 == 0) return 3;
        if (commands % 4 == 0) return 4;
        return commands % 4 != 1 ? 4 : 3;
    }

    /// <summary>Empty cells left on the grid's last row, where the Close button sits instead of a footer row.</summary>
    internal static int OpenCells(int commands, int columns) =>
        columns <= 0 || commands % columns == 0 ? 0 : columns - commands % columns;
}
