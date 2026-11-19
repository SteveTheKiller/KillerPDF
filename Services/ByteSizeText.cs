using System.Globalization;
using System.Windows;

namespace KillerPDF.Services;

/// <summary>
/// A file or download size as the interface shows it (#506). The number follows the current
/// culture and the unit follows the interface language, so French reads "2,4 Mo" and Russian
/// "2,4 МБ" instead of an English "MB" in a translated window.
/// </summary>
internal static class ByteSizeText
{
    private const double Kilo = 1024.0, Mega = 1024.0 * 1024.0;

    /// <summary>"512 B", "37 KB" or "2.4 MB".</summary>
    internal static string Format(long bytes) =>
        bytes >= Mega ? Megabytes(bytes / Mega)
        : bytes >= Kilo ? Unit("Str_Unit_KB", "{0} KB", (bytes / Kilo).ToString("0", CultureInfo.CurrentCulture))
        : Unit("Str_Unit_Bytes", "{0} B", bytes.ToString(CultureInfo.CurrentCulture));

    /// <summary>A size in whole kilobytes: "1,204 KB".</summary>
    internal static string Kilobytes(long bytes) =>
        Unit("Str_Unit_KB", "{0} KB", (bytes / Kilo).ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>A size already in megabytes, one decimal: "2.4 MB".</summary>
    internal static string Megabytes(double megabytes) =>
        Unit("Str_Unit_MB", "{0} MB", megabytes.ToString("0.0", CultureInfo.CurrentCulture));

    /// <summary>Download progress in megabytes: "2.4 / 15.0 MB".</summary>
    internal static string MegabytesOf(double done, double total) =>
        Unit("Str_Unit_MB", "{0} MB", done.ToString("0.0", CultureInfo.CurrentCulture) + " / "
            + total.ToString("0.0", CultureInfo.CurrentCulture));

    private static string Unit(string key, string english, string number)
    {
        string format = Application.Current?.TryFindResource(key) as string ?? english;
        try { return string.Format(CultureInfo.CurrentCulture, format, number); }
        catch (System.FormatException) { return string.Format(CultureInfo.InvariantCulture, english, number); }
    }
}
