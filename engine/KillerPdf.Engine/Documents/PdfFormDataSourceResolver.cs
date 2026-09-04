namespace KillerPdf.Engine.Documents;

/// <summary>Resolves and opens local FDF and XFDF source references.</summary>
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

    /// <summary>Opens an embedded, referenced, or caller-selected source PDF.</summary>
    public static PdfDocument Open(PdfFormDataSet data, string interchangeFilePath,
        string? password = null, string? replacementSourcePdfPath = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentException.ThrowIfNullOrWhiteSpace(interchangeFilePath);
        ReadOnlyMemory<byte> source;
        if (data.EmbeddedSourcePdf is ReadOnlyMemory<byte> embedded)
            source = embedded;
        else
        {
            string? path = string.IsNullOrWhiteSpace(replacementSourcePdfPath)
                ? Resolve(data, interchangeFilePath)
                : Path.GetFullPath(replacementSourcePdfPath);
            if (path is null)
                throw new FileNotFoundException(
                    "The FDF or XFDF data does not identify a source PDF.");
            source = File.ReadAllBytes(path);
        }
        return password is null ? PdfDocument.Open(source) : PdfDocument.Open(source, password);
    }
}
