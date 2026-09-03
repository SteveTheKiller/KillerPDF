using System.Globalization;

namespace KillerPDF.Services;

internal static class PageSizeFormatter
{
    private readonly record struct KnownSize(string Name, double Width, double Height, bool Metric);

    private static readonly KnownSize[] KnownSizes =
    [
        new("A0", 2384, 3370, true),
        new("A1", 1684, 2384, true),
        new("A2", 1191, 1684, true),
        new("A3", 842, 1191, true),
        new("A4", 595, 842, true),
        new("A5", 420, 595, true),
        new("A6", 298, 420, true),
        new("Letter", 612, 792, false),
        new("Legal", 612, 1008, false),
        new("Tabloid", 792, 1224, false),
        new("ANSI C", 1224, 1584, false),
        new("ANSI D", 1584, 2448, false),
        new("ANSI E", 2448, 3168, false),
    ];

    internal static (string Label, string Details, bool Metric) Format(double widthPoints, double heightPoints, bool? metric = null)
    {
        double inchesWide = widthPoints / 72.0;
        double inchesHigh = heightPoints / 72.0;
        double mmWide = inchesWide * 25.4;
        double mmHigh = inchesHigh * 25.4;
        KnownSize? known = FindKnownSize(widthPoints, heightPoints);

        string name = known?.Name ?? string.Empty;
        if (name == "Tabloid" && widthPoints > heightPoints) name = "Ledger";

        bool useMetric = metric ?? known?.Metric ?? false;
        string primary = useMetric
            ? $"{FormatNumber(mmWide, 1)} x {FormatNumber(mmHigh, 1)} mm"
            : $"{FormatNumber(inchesWide, 2)} x {FormatNumber(inchesHigh, 2)} in";
        string label = string.IsNullOrEmpty(name) ? primary : $"{name}  {primary}";
        string details = $"{FormatNumber(inchesWide, 2)} x {FormatNumber(inchesHigh, 2)} in | " +
                         $"{FormatNumber(mmWide, 1)} x {FormatNumber(mmHigh, 1)} mm | " +
                         $"{FormatNumber(widthPoints, 1)} x {FormatNumber(heightPoints, 1)} pt";
        return (label, details, useMetric);
    }

    private static KnownSize? FindKnownSize(double width, double height)
    {
        double shortSide = Math.Min(width, height);
        double longSide = Math.Max(width, height);
        foreach (KnownSize size in KnownSizes)
            if (Math.Abs(shortSide - size.Width) <= 3 && Math.Abs(longSide - size.Height) <= 3)
                return size;
        return null;
    }

    private static string FormatNumber(double value, int decimals) =>
        value.ToString(decimals == 1 ? "0.#" : "0.##", CultureInfo.CurrentCulture);
}
