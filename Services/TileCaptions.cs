using System.Collections.Generic;

namespace KillerPDF.Services;

/// <summary>
/// The short captions card tiles show in place of a full command name that does not fit a tile. A tile is at most
/// 84 px wide and its caption two lines tall, so "Open Containing Folder" was cut to "Open Containi...". The tile
/// shows the short caption; its tooltip and accessible name keep the full name. Keys map the full name's resource
/// key to the short one's (TileCaptionTests measures every tile caption against the tile).
/// </summary>
internal static class TileCaptions
{
    internal static readonly IReadOnlyDictionary<string, string> ShortKeys = new Dictionary<string, string>(System.StringComparer.Ordinal)
    {
        ["Str_Ctx_OpenContainingFolder"] = "Str_Ctx_OpenContainingFolderShort",
        ["Str_Ocr_SearchablePdf"] = "Str_Ocr_SearchablePdfShort",
        ["Str_Ctx_OcrPage"] = "Str_Ctx_OcrPageShort",
        ["Str_Repair_Language"] = "Str_Repair_LanguageShort",
        ["Str_Repair_PageBox"] = "Str_Repair_PageBoxShort",
    };
}
