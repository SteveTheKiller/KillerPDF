using System.IO;

namespace KillerPDF.Services;

/// <summary>
/// Save as KillerPDF on images in Explorer (#479): <c>KillerPDF.exe --images-to-pdf &lt;files...&gt;</c>
/// writes one PDF beside the first image, named after it, without opening a window. Several selected
/// images make one PDF, pages in name order (see <see cref="ImagesToPdfPlan"/>). Success shows nothing;
/// a failure says why in a KillerDialog (ImagesToPdfCommand.Run.cs), since a shell launch has no console.
/// </summary>
internal static partial class ImagesToPdfCommand
{
    internal const string EnglishFailureFormat = "KillerPDF could not make a PDF from {0}.";

    /// <summary>Writes the PDF and returns its path. Throws, writing nothing, when an input is missing,
    /// is not an image or cannot be read. An existing file is never replaced.</summary>
    internal static string Convert(IReadOnlyList<string> files)
    {
        IReadOnlyList<string> ordered = ImagesToPdfPlan.Order(files);
        if (ordered.Count == 0) throw Reason(new ArgumentException(NoImage.English, nameof(files)), NoImage);
        foreach (string file in ordered)
        {
            string name = Path.GetFileName(file);
            if (!File.Exists(file))
                throw Reason(new FileNotFoundException(string.Format(NotFound.English, name), file), NotFound, name);
            if (!ImagesToPdfPlan.IsImage(file))
                throw Reason(new InvalidDataException(string.Format(NotImage.English, name)), NotImage, name);
        }
        byte[] pdf = PdfEngineIntegration.MergeFiles(ordered);
        for (int attempt = 1; attempt <= 10_000; attempt++)
        {
            string output = ImagesToPdfPlan.CandidatePath(ordered[0], attempt);
            if (File.Exists(output) || Directory.Exists(output)) continue;
            try
            {
                AtomicFileOutput.WriteNew(output, pdf);
                return output;
            }
            catch (IOException) when (File.Exists(output)) { }   // another file took the name meanwhile
        }
        throw Reason(new IOException(NamesTaken.English), NamesTaken);
    }

    // The reasons this command writes itself (#506). The exception keeps its English message for logs
    // and tests and carries the key, so the failure dialog can say it in the interface language once
    // the strings are loaded; that happens only after a failure, in ShowFailure.
    private static readonly (string Key, string English) NoImage = ("Str_ImagesToPdf_NoImage", "No image was given.");
    private static readonly (string Key, string English) NotFound = ("Str_ImagesToPdf_NotFound", "{0} was not found.");
    private static readonly (string Key, string English) NotImage =
        ("Str_ImagesToPdf_NotImage", "{0} is not a JPEG, PNG, BMP, GIF or TIFF image.");
    private static readonly (string Key, string English) NamesTaken =
        ("Str_ImagesToPdf_NamesTaken", "Every name for the PDF is already taken.");
    private const string ReasonKey = "KillerPDF.ImagesToPdfReason";

    private static T Reason<T>(T error, (string Key, string English) text, params object[] args) where T : Exception
    {
        error.Data[ReasonKey] = (text.Key, text.English, args);
        return error;
    }

    /// <summary>The reason for a failure in the interface language when it is one of this command's
    /// own, otherwise the exception's message.</summary>
    internal static string ReasonText(Exception error)
    {
        if (error.Data[ReasonKey] is not ValueTuple<string, string, object[]> reason) return error.Message;
        return Localized(reason.Item1, reason.Item2, reason.Item3);
    }

    internal static string NoImageText => Localized(NoImage.Key, NoImage.English, []);

    private static string Localized(string key, string english, object[] args)
    {
        string format = System.Windows.Application.Current?.TryFindResource(key) as string ?? english;
        try { return string.Format(System.Globalization.CultureInfo.CurrentCulture, format, args); }
        catch (FormatException) { return string.Format(System.Globalization.CultureInfo.InvariantCulture, english, args); }
    }

    /// <summary>Names the first image, then gives the reason.</summary>
    /// <param name="format">The translated sentence with {0} for the file name; null uses the English one.</param>
    internal static string FailureMessage(IReadOnlyList<string> files, string reason, string? format)
    {
        string name = files.Count == 0 ? "" : Path.GetFileName(files[0].TrimEnd('\\', '/'));
        string lead = string.Format(string.IsNullOrWhiteSpace(format) ? EnglishFailureFormat : format, name);
        string detail = reason.Trim();
        return detail.Length == 0 ? lead : lead + "\n\n" + detail;
    }
}
