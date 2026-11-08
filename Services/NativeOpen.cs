using System.IO;
using KillerPdf.Engine.Formats.Epub;

namespace KillerPDF.Services;

/// <summary>Reads a native EPUB or DjVu file and lays it out, away from the caller's thread.</summary>
internal static class NativeOpen
{
    private const long MaximumDjvuBytes = 64L << 20;

    internal static bool IsEpubPath(string path) =>
        Path.GetExtension(path).Equals(".epub", StringComparison.OrdinalIgnoreCase);

    /// <summary>Opens the file on the calling thread. EPUB needs layout options; DjVu ignores them.</summary>
    internal static NativeWorkingDocument Open(string path, EpubLayoutOptions? epubOptions,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool epub = IsEpubPath(path);
        if (epub) ArgumentNullException.ThrowIfNull(epubOptions);
        byte[] bytes;
        using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            long maximum = epub ? EpubDocument.MaxPackageBytes : MaximumDjvuBytes;
            if (source.Length > maximum)
                throw new InvalidDataException("Native document input exceeds the " + (maximum >> 20) + " MiB limit.");
            bytes = new byte[checked((int)source.Length)];
            source.ReadExactly(bytes);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return epub ? NativeWorkingDocument.OpenEpub(bytes, epubOptions!, cancellationToken)
            : NativeWorkingDocument.OpenDjvu(bytes, cancellationToken);
    }

    /// <summary>
    /// Reads and lays out the file on a pool thread, so a long book never holds the caller's
    /// dispatcher. EPUB layout callbacks such as the font resolver run on that pool thread.
    /// </summary>
    internal static Task<NativeWorkingDocument> OpenAsync(string path, EpubLayoutOptions? epubOptions,
        IProgress<(int Done, int Total)>? chapterProgress = null, CancellationToken cancellationToken = default)
    {
        // The layout calls back on the pool thread; IProgress carries each report to the caller's context.
        if (epubOptions is not null && chapterProgress is not null)
            epubOptions = epubOptions with { ChapterProgress = (done, total) => chapterProgress.Report((done, total)) };
        return Task.Run(() => Open(path, epubOptions, cancellationToken), cancellationToken);
    }
}
