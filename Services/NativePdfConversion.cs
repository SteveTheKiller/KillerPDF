using System.IO;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

internal static class NativePdfConversion
{
    internal static void WriteNewDjvu(NativeWorkingDocument source, string sourcePath, string destination,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted PDF must use a different path from its source.", nameof(destination));
        if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted document requires a PDF destination.", nameof(destination));
        AtomicFileOutput.WriteNew(destination, source.ConvertDjvuToPdf(cancellationToken), cancellationToken);
    }

    internal static void WriteNewEpub(NativeWorkingDocument source, string sourcePath, string destination,
        EpubPdfConversionOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted PDF must use a different path from its source.", nameof(destination));
        if (!Path.GetExtension(destination).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The converted document requires a PDF destination.", nameof(destination));
        AtomicFileOutput.WriteNew(destination, source.ConvertEpubToPdf(options, cancellationToken), cancellationToken);
    }
}
