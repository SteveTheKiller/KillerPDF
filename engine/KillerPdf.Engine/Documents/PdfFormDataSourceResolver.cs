namespace KillerPdf.Engine.Documents;

/// <summary>Resolves local FDF and XFDF source references without opening the target PDF.</summary>
public static class PdfFormDataSourceResolver
{
    /// <summary>Resolves an absolute or interchange-file-relative source PDF path.</summary>
    public static string? Resolve(PdfFormDataSet data, string interchangeFilePath)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(interchangeFilePath);
        if (string.IsNullOrWhiteSpace(data.SourcePdfPath)) return null;

        string reference = data.SourcePdfPath;
        if (Path.IsPathRooted(reference)) return Path.GetFullPath(reference);
        if (Uri.TryCreate(reference, UriKind.Absolute, out Uri? uri))
        {
            if (!uri.IsFile)
                throw new NotSupportedException("FDF and XFDF source references must use local files.");
            return Path.GetFullPath(uri.LocalPath);
        }
        string interchangePath = Path.GetFullPath(interchangeFilePath);
        string directory = Path.GetDirectoryName(interchangePath)
            ?? throw new ArgumentException("The interchange file has no parent directory.",
                nameof(interchangeFilePath));
        return Path.GetFullPath(reference, directory);
    }
}
