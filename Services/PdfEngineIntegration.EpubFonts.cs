using System.IO;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal static partial class PdfEngineIntegration
{
    internal static Func<EpubFontFaceRequest, EpubFontFaceResult?> EpubFontFaceResolver(string defaultFamily) => request =>
    {
        string name = request.Family is { } family ? EpubFamilyName(family) : defaultFamily;
        try
        {
            var selected = InstalledFontCatalog.EpubFaceBytes(name, request.Weight, request.Style);
            if (selected is null) return null;
            var font = TrueTypeFont.Load(selected.Value.Bytes);
            EpubFontStyle? actualStyle = font.DeclaredStyle switch
            {
                TrueTypeFontStyle.Normal => EpubFontStyle.Normal,
                TrueTypeFontStyle.Italic => EpubFontStyle.Italic,
                _ => null
            };
            if (actualStyle is null || actualStyle != request.Style) return null;
            return new(font, selected.Value.Weight,
                selected.Value.Weight == request.Weight ? EpubFontMatch.Exact : EpubFontMatch.Nearest)
                { ActualStyle = actualStyle.Value };
        }
        catch (Exception error) when (error is FormatException or NotSupportedException
            or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    };

    internal static TrueTypeFont? ResolveEpubFontFamily(EpubFontFamily family)
    {
        string name = EpubFamilyName(family);
        byte[]? bytes = InstalledFontCatalog.RegularFaceBytes(name);
        if (bytes is null) return null;
        try { return TrueTypeFont.Load(bytes); }
        catch (Exception error) when (error is FormatException or NotSupportedException
            or ArgumentException or InvalidDataException or InvalidOperationException)
        {
            return null;
        }
    }

    private static string EpubFamilyName(EpubFontFamily family) => family.IsGeneric ? family.Name.ToLowerInvariant() switch
        {
            "serif" => "Times New Roman",
            "sans-serif" => "Arial",
            "monospace" => "Courier New",
            "cursive" => "Comic Sans MS",
            "fantasy" => "Impact",
            _ => family.Name
        } : family.Name;
}
