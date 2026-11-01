using System.IO;
using System.IO.Compression;

namespace KillerPDF.Services;

internal static class PdfZipExport
{
    internal static void Write(string source, string destination, CancellationToken cancellation = default)
    {
        source = Path.GetFullPath(source);
        destination = Path.GetFullPath(destination);
        if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The archive cannot replace its source PDF.", nameof(destination));
        AtomicFileOutput.Write(destination, stream =>
        {
            using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);
            archive.CreateEntryFromFile(source, Path.GetFileName(source), CompressionLevel.Optimal);
        }, cancellation);
    }
}
