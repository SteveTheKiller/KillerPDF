using System.Reflection;

namespace KillerPdf.Engine.Fonts;

internal static class PdfStandardFontSubstitutes
{
    private static readonly IReadOnlyDictionary<string, Lazy<TrueTypeFont>> Fonts =
        new Dictionary<string, Lazy<TrueTypeFont>>(StringComparer.Ordinal)
        {
            ["Helvetica"] = Font("LiberationSans-Regular.ttf"),
            ["Helvetica-Bold"] = Font("LiberationSans-Bold.ttf"),
            ["Helvetica-Oblique"] = Font("LiberationSans-Italic.ttf"),
            ["Helvetica-BoldOblique"] = Font("LiberationSans-BoldItalic.ttf"),
            ["Times-Roman"] = Font("LiberationSerif-Regular.ttf"),
            ["Times-Bold"] = Font("LiberationSerif-Bold.ttf"),
            ["Times-Italic"] = Font("LiberationSerif-Italic.ttf"),
            ["Times-BoldItalic"] = Font("LiberationSerif-BoldItalic.ttf"),
            ["Courier"] = Font("LiberationMono-Regular.ttf"),
            ["Courier-Bold"] = Font("LiberationMono-Bold.ttf"),
            ["Courier-Oblique"] = Font("LiberationMono-Italic.ttf"),
            ["Courier-BoldOblique"] = Font("LiberationMono-BoldItalic.ttf")
        };

    internal static TrueTypeFont? Find(string fontName) =>
        Fonts.TryGetValue(fontName, out Lazy<TrueTypeFont>? font) ? font.Value : null;

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
