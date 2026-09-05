using System.Reflection;

namespace KillerPdf.Engine.Fonts;

internal static class PdfStandardFontSubstitutes
{
    private static readonly Lazy<TrueTypeFont> SansRegular = Font("LiberationSans-Regular.ttf");
    private static readonly Lazy<TrueTypeFont> SansBold = Font("LiberationSans-Bold.ttf");
    private static readonly Lazy<TrueTypeFont> SansItalic = Font("LiberationSans-Italic.ttf");
    private static readonly Lazy<TrueTypeFont> SansBoldItalic = Font("LiberationSans-BoldItalic.ttf");
    private static readonly Lazy<TrueTypeFont> SerifRegular = Font("LiberationSerif-Regular.ttf");
    private static readonly Lazy<TrueTypeFont> SerifBold = Font("LiberationSerif-Bold.ttf");
    private static readonly Lazy<TrueTypeFont> SerifItalic = Font("LiberationSerif-Italic.ttf");
    private static readonly Lazy<TrueTypeFont> SerifBoldItalic = Font("LiberationSerif-BoldItalic.ttf");
    private static readonly Lazy<TrueTypeFont> MonoRegular = Font("LiberationMono-Regular.ttf");
    private static readonly Lazy<TrueTypeFont> MonoBold = Font("LiberationMono-Bold.ttf");
    private static readonly Lazy<TrueTypeFont> MonoItalic = Font("LiberationMono-Italic.ttf");
    private static readonly Lazy<TrueTypeFont> MonoBoldItalic = Font("LiberationMono-BoldItalic.ttf");

    private static readonly IReadOnlyDictionary<string, Lazy<TrueTypeFont>> Fonts =
        new Dictionary<string, Lazy<TrueTypeFont>>(StringComparer.Ordinal)
        {
            ["Helvetica"] = SansRegular,
            ["Helvetica-Bold"] = SansBold,
            ["Helvetica-Oblique"] = SansItalic,
            ["Helvetica-BoldOblique"] = SansBoldItalic,
            ["Times-Roman"] = SerifRegular,
            ["Times-Bold"] = SerifBold,
            ["Times-Italic"] = SerifItalic,
            ["Times-BoldItalic"] = SerifBoldItalic,
            ["Courier"] = MonoRegular,
            ["Courier-Bold"] = MonoBold,
            ["Courier-Oblique"] = MonoItalic,
            ["Courier-BoldOblique"] = MonoBoldItalic
        };

    internal static TrueTypeFont Find(string fontName)
    {
        if (Fonts.TryGetValue(fontName, out Lazy<TrueTypeFont>? exact))
            return exact.Value;

        bool bold = Contains(fontName, "bold") || Contains(fontName, "semibold")
            || Contains(fontName, "demi") || Contains(fontName, "black")
            || Contains(fontName, "heavy");
        bool italic = Contains(fontName, "italic") || Contains(fontName, "oblique")
            || fontName.EndsWith("-It", StringComparison.OrdinalIgnoreCase)
            || Contains(fontName, "chancery");
        bool mono = Contains(fontName, "courier") || Contains(fontName, "mono")
            || Contains(fontName, "typewriter");
        bool serif = !mono && (Contains(fontName, "times") || Contains(fontName, "serif")
            || Contains(fontName, "minion") || Contains(fontName, "calluna")
            || Contains(fontName, "century") || Contains(fontName, "schoolbook")
            || Contains(fontName, "warnock"));

        return (mono, serif, bold, italic) switch
        {
            (true, _, true, true) => MonoBoldItalic.Value,
            (true, _, true, false) => MonoBold.Value,
            (true, _, false, true) => MonoItalic.Value,
            (true, _, false, false) => MonoRegular.Value,
            (_, true, true, true) => SerifBoldItalic.Value,
            (_, true, true, false) => SerifBold.Value,
            (_, true, false, true) => SerifItalic.Value,
            (_, true, false, false) => SerifRegular.Value,
            (_, _, true, true) => SansBoldItalic.Value,
            (_, _, true, false) => SansBold.Value,
            (_, _, false, true) => SansItalic.Value,
            _ => SansRegular.Value
        };
    }

    private static bool Contains(string value, string part) =>
        value.Contains(part, StringComparison.OrdinalIgnoreCase);

    private static Lazy<TrueTypeFont> Font(string fileName) => new(() =>
    {
        Assembly assembly = typeof(PdfStandardFontSubstitutes).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts." + fileName)
            ?? throw new InvalidOperationException(
                $"The standard-font substitute {fileName} is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return TrueTypeFont.LoadForExtraction(memory.ToArray());
    }, LazyThreadSafetyMode.ExecutionAndPublication);
}
