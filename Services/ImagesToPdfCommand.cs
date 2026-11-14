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
        if (ordered.Count == 0) throw new ArgumentException("No image was given.", nameof(files));
        foreach (string file in ordered)
        {
            if (!File.Exists(file)) throw new FileNotFoundException($"{Path.GetFileName(file)} was not found.", file);
            if (!ImagesToPdfPlan.IsImage(file))
                throw new InvalidDataException($"{Path.GetFileName(file)} is not a JPEG, PNG, BMP, GIF or TIFF image.");
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
        throw new IOException("Every name for the PDF is already taken.");
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
