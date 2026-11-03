using System.IO;

namespace KillerPDF.Services;

/// <summary>Supplies the suggested searchable-PDF name and source directory.</summary>
public sealed record OcrPdfOutputSuggestion(string FileName, string InitialDirectory)
{
    /// <summary>Creates a save suggestion from the original source and working document paths.</summary>
    public static OcrPdfOutputSuggestion Create(string? originalFile, string? workingFile)
    {
        string? seed = originalFile ?? workingFile;
        string baseName = "document";
        string initialDirectory = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(seed) &&
                seed.IndexOfAny(Path.GetInvalidPathChars()) < 0 &&
                seed.IndexOfAny(['"', '<', '>', '*', '?']) < 0)
            {
                string candidate = Path.GetFileNameWithoutExtension(seed);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    candidate.IndexOfAny(Path.GetInvalidFileNameChars()) < 0)
                {
                    if (originalFile is not null && Path.IsPathFullyQualified(originalFile))
                        initialDirectory = Path.GetDirectoryName(Path.GetFullPath(originalFile)) ?? string.Empty;
                    baseName = candidate;
                }
            }
        }
        catch (ArgumentException) { }
        catch (NotSupportedException) { }
        catch (PathTooLongException) { }

        return new(baseName + "-searchable.pdf", initialDirectory);
    }
}
