using System.IO;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal static partial class PdfEngineIntegration
{
    internal static TrueTypeFont? ResolveEpubFontFamily(EpubFontFamily family)
    {
        string name = family.IsGeneric ? family.Name.ToLowerInvariant() switch
        {
            "serif" => "Times New Roman",
            "sans-serif" => "Arial",
            "monospace" => "Courier New",
            "cursive" => "Comic Sans MS",
            "fantasy" => "Impact",
            _ => family.Name
        } : family.Name;
        byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(name);
        if (bytes is null) return null;
        try { return TrueTypeFont.Load(bytes); }
        catch (Exception error) when (error is FormatException or NotSupportedException
            or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }
}
