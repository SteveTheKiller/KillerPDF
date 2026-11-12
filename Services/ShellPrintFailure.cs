namespace KillerPDF.Services;

/// <summary>
/// The message a print started from Windows shows when it fails. Right-click Print, Start-Process -Verb Print and
/// mail apps run the headless print with no console, so its own reason would otherwise go nowhere.
/// </summary>
internal static partial class ShellPrintFailure
{
    internal const string EnglishFormat = "KillerPDF could not print {0}.";

    /// <summary>Names the file, then gives the print's own reason (in English, as the command line writes it).</summary>
    /// <param name="format">The translated sentence with {0} for the file name; null uses the English one.</param>
    internal static string Message(string? path, string reason, string? format)
    {
        string name = string.IsNullOrWhiteSpace(path) ? "" : System.IO.Path.GetFileName(path.TrimEnd('\\', '/'));
        string lead = string.Format(string.IsNullOrWhiteSpace(format) ? EnglishFormat : format, name);
        string detail = reason.Trim();
        return detail.Length == 0 ? lead : lead + "\n\n" + detail;
    }
}
