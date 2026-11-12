namespace KillerPDF.Controls;

// The document tool launchers (DocumentCommandDialog): one icon tile per command, in a grid sized so its last row
// never holds a lone tile. Icons are Segoe MDL2 code points, keyed by the command's label string key.
internal static class CommandLauncher
{
    private static readonly Dictionary<string, string> Glyphs = new(StringComparer.Ordinal)
    {
        // Production layouts and Bates batches
        ["Str_Production_NUp"] = "\uE80A",
        ["Str_Production_Booklet"] = "\uE736",
        ["Str_Production_Repeat"] = "\uE8C8",
        ["Str_Production_CutStack"] = "\uE8C6",
        ["Str_Production_Manual"] = "\uE8CB",
        ["Str_Production_Poster"] = "\uE740",
        ["Str_Production_Bates"] = "\uE8EF",
        // Form data
        ["Str_Exchange_ExportForms"] = "\uEDE1",
        ["Str_Exchange_ImportForms"] = "\uE8B5",
        ["Str_FormTools_Recognize"] = "\uE721",
        ["Str_FormTools_ConvertXfa"] = "\uE8AB",
        ["Str_FormTools_Flatten"] = "\uE81E",
        // Bookmark data
        ["Str_Exchange_ExportBookmarks"] = "\uEDE1",
        ["Str_Exchange_ImportBookmarks"] = "\uE8B5",
        // Preflight and its repairs
        ["Str_DocumentTools_Preflight"] = "\uE9D9",
        ["Str_DocumentTools_PreflightProfile"] = "\uE8E5",
        ["Str_Repair_Language"] = "\uE774",
        ["Str_Repair_Metadata"] = "\uE946",
        ["Str_Repair_PageBox"] = "\uE7A8",
        ["Str_Repair_OutputIntent"] = "\uE790",
        ["Str_Repair_Bleed"] = "\uE7A8",
        ["Str_Repair_Trim"] = "\uE7A8",
        ["Str_Repair_Art"] = "\uE7A8",
        // Missing metadata fields
        ["Str_DocInfo_Title"] = "\uE8D2",
        ["Str_DocInfo_Author"] = "\uE77B",
        ["Str_DocInfo_Subject"] = "\uE8BD",
        ["Str_DocInfo_Keywords"] = "\uE8EC",
    };

    private const string DocumentGlyph = "\uE8A5";

    internal static bool HasGlyph(string labelKey) => Glyphs.ContainsKey(labelKey);

    internal static string GlyphFor(string labelKey) => Glyphs.TryGetValue(labelKey, out var glyph) ? glyph : DocumentGlyph;

    /// <summary>Columns for a launcher grid: up to four across, never leaving one tile alone on the last row.</summary>
    internal static int Columns(int commands)
    {
        if (commands <= 3) return Math.Max(1, commands);
        if (commands == 4) return 2;
        if (commands % 3 == 0) return 3;
        if (commands % 4 == 0) return 4;
        return commands % 4 != 1 ? 4 : 3;
    }
}
