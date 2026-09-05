using KillerPdf.Engine.Fonts;

namespace KillerPDF.Services;

internal sealed class InstalledPdfFontResolver : IPdfFontResolver
{
    internal static InstalledPdfFontResolver Instance { get; } = new();

    private readonly Dictionary<PdfFontRequest, byte[]?> _cache = [];
    private readonly Dictionary<(string Family, bool Bold, bool Italic), byte[]?>
        _familyCache = [];
    private readonly Lock _gate = new();

    public byte[]? Resolve(PdfFontRequest request)
    {
        lock (_gate)
        {
            if (_cache.TryGetValue(request, out byte[]? cached)) return cached;
            bool bold = IsBold(request.PostScriptName);
            bool italic = IsItalic(request.PostScriptName);
            byte[]? resolved = Candidates(request)
                .Select(family => FaceBytes(family, bold, italic))
                .FirstOrDefault(bytes => bytes is not null);
            _cache[request] = resolved;
            return resolved;
        }
    }

    private byte[]? FaceBytes(string family, bool bold, bool italic)
    {
        var key = (family, bold, italic);
        if (!_familyCache.TryGetValue(key, out byte[]? bytes))
        {
            bytes = InstalledFontCatalog.FaceBytes(family, bold, italic);
            _familyCache[key] = bytes;
        }
        return bytes;
    }

    private static IEnumerable<string> Candidates(PdfFontRequest request)
    {
        string name = NormalizeFamily(request.PostScriptName);
        if (name.Length > 0) yield return name;

        bool serif = Contains(name, "min") || Contains(name, "ming")
            || Contains(name, "song") || Contains(name, "serif");
        string[] fallbacks = request.Ordering switch
        {
            "Japan1" when serif => ["Yu Mincho", "MS Mincho", "Meiryo", "Yu Gothic", "MS Gothic"],
            "Japan1" => ["Yu Gothic", "Meiryo", "MS Gothic"],
            "GB1" when serif => ["SimSun", "Microsoft YaHei"],
            "GB1" => ["Microsoft YaHei", "SimSun"],
            "CNS1" when serif => ["PMingLiU", "Microsoft JhengHei"],
            "CNS1" => ["Microsoft JhengHei", "PMingLiU"],
            "Korea1" when serif => ["Batang", "Malgun Gothic"],
            "Korea1" => ["Malgun Gothic", "Batang"],
            _ => Array.Empty<string>()
        };
        foreach (string family in fallbacks)
            if (!family.Equals(name, StringComparison.OrdinalIgnoreCase)) yield return family;
    }

    private static string NormalizeFamily(string value)
    {
        int comma = value.IndexOf(',');
        if (comma >= 0) value = value[..comma];
        string[] suffixes =
        [
            "-BoldItalic", "-BoldOblique", "-SemiboldItalic", "-Bold",
            "-Semibold", "-Italic", "-Oblique", "PSMT", "MT"
        ];
        foreach (string suffix in suffixes)
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length];
                break;
            }
        return value.Replace('-', ' ').Trim();
    }

    private static bool IsBold(string value) => Contains(value, "bold")
        || Contains(value, "semibold") || Contains(value, "demi")
        || Contains(value, "black") || Contains(value, "heavy");

    private static bool IsItalic(string value) => Contains(value, "italic")
        || Contains(value, "oblique") || value.EndsWith("-It", StringComparison.OrdinalIgnoreCase);

    private static bool Contains(string value, string part) =>
        value.Contains(part, StringComparison.OrdinalIgnoreCase);
}
