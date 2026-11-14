using System.Globalization;
using System.IO;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;

namespace KillerPDF.Services;

/// <summary>How Extract Pages divides the document.</summary>
internal enum PageSplitMode
{
    /// <summary>Every page in its own PDF, as the command line's split does.</summary>
    EveryPage,
    /// <summary>A PDF for every few pages, as split --every does.</summary>
    EveryN
}

/// <summary>One file Extract Pages writes: its name and the zero-based pages it holds, in order.</summary>
internal sealed record PageSplitPart(string FileName, IReadOnlyList<int> PageIndices);

/// <summary>
/// The window-free half of Extract Pages (#484). It reads the dialog's page choices, plans the files a split writes
/// and writes them. Files are named exactly as the command line's split names them: one per page is
/// "name-page-001.pdf" (at least three digits), and every few pages is the engine's "name-1.pdf" (as wide as the
/// part count), so a script and the app agree. Each part is built from the saved copy with the pending page turns
/// applied once, as the one-file extract does.
/// </summary>
internal static class PageSplit
{
    /// <summary>The pages from one page number to another, either way round, or null when either is outside the document.</summary>
    internal static IReadOnlyList<int>? FromTo(int from, int to, int pageCount)
    {
        if (from > to) (from, to) = (to, from);
        return from < 1 || to > pageCount ? null : [.. Enumerable.Range(from - 1, to - from + 1)];
    }

    /// <summary>
    /// Where the dialog starts: the selected pages (one-based) or else the whole document. A selection with gaps
    /// cannot be shown as From and To, so the dialog shows it as a page list instead.
    /// </summary>
    internal static (int From, int To, bool Contiguous) Start(IReadOnlyCollection<int> selectedPages, int pageCount)
    {
        int[] pages = [.. selectedPages.Where(page => page >= 1 && page <= pageCount).Distinct().Order()];
        if (pages.Length == 0) return (1, Math.Max(1, pageCount), true);
        return (pages[0], pages[^1], pages[^1] - pages[0] + 1 == pages.Length);
    }

    /// <summary>The typed base name tidied as the engine tidies part names, or null when it cannot name a file.</summary>
    internal static string? BaseName(string typed)
    {
        if (string.IsNullOrWhiteSpace(typed) || typed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        string tidy = string.Join(' ', typed.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '.');
        return tidy.Length == 0 ? null : tidy;
    }

    /// <summary>How many files a split writes, or zero when the page count per file is not usable.</summary>
    internal static int FileCount(PageSplitMode mode, int pageCount, int pagesPerFile) => mode switch
    {
        PageSplitMode.EveryPage => pageCount,
        _ => pagesPerFile <= 0 || pageCount <= 0 ? 0 : (pageCount + pagesPerFile - 1) / pagesPerFile
    };

    /// <summary>The files a split writes, in page order, named as the command line's split names them.</summary>
    internal static IReadOnlyList<PageSplitPart> Plan(PageSplitMode mode, int pageCount, int pagesPerFile, string baseName)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageCount);
        ArgumentException.ThrowIfNullOrWhiteSpace(baseName);
        var parts = new List<PageSplitPart>();
        if (mode == PageSplitMode.EveryPage)
        {
            // KillerPDF.exe split <in> <folder>: name-page-001.pdf, never fewer than three digits.
            int digits = Math.Max(3, pageCount.ToString(CultureInfo.InvariantCulture).Length);
            for (int page = 0; page < pageCount; page++)
                parts.Add(new PageSplitPart(
                    $"{baseName}-page-{(page + 1).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')}.pdf", [page]));
            return parts;
        }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pagesPerFile);
        // KillerPDF.exe split <in> <folder> --every N: the engine's {name}-{index}, padded to the part count's width.
        int count = FileCount(mode, pageCount, pagesPerFile);
        int width = count.ToString(CultureInfo.InvariantCulture).Length;
        for (int part = 0; part < count; part++)
        {
            int first = part * pagesPerFile;
            parts.Add(new PageSplitPart(
                $"{baseName}-{(part + 1).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0')}.pdf",
                [.. Enumerable.Range(first, Math.Min(pagesPerFile, pageCount - first))]));
        }
        return parts;
    }

    /// <summary>The planned files that already exist in the folder.</summary>
    internal static IReadOnlyList<string> Existing(string folder, IReadOnlyList<PageSplitPart> parts) =>
        [.. parts.Select(part => Path.Combine(folder, part.FileName)).Where(path => File.Exists(path) || Directory.Exists(path))];

    /// <summary>Whether a planned file is the open document's own file.</summary>
    internal static bool WouldReplace(string folder, IReadOnlyList<PageSplitPart> parts, string? documentPath) =>
        documentPath is not null && parts.Any(part => string.Equals(Path.GetFullPath(Path.Combine(folder, part.FileName)),
            Path.GetFullPath(documentPath), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Writes every part from the saved copy at <paramref name="sourcePath"/> and returns how many files were written.
    /// Every part is finished in the folder before any planned name is touched, so a failed build leaves the folder as
    /// it was; only a file another program holds while the finished parts are moved into place can stop it partway.
    /// An existing file is replaced only when <paramref name="overwrite"/> says so; otherwise nothing is written.
    /// </summary>
    internal static int Write(string sourcePath, string folder, IReadOnlyList<PageSplitPart> parts,
        IReadOnlyDictionary<int, int> rotations, bool overwrite)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(rotations);
        // A folder never gives way to a part, as the command line's split refuses it too.
        if (parts.Select(part => Path.Combine(folder, part.FileName)).FirstOrDefault(Directory.Exists) is string blocked)
            throw new IOException("A folder has the name of a file to write: " + blocked);
        if (!overwrite && Existing(folder, parts) is [string first, ..])
            throw new IOException("The file already exists: " + first);
        Directory.CreateDirectory(folder);
        // The saved copy is read once and every part imports from it.
        PdfDocument source = PdfDocument.Open(File.ReadAllBytes(sourcePath));
        var staged = new List<(string Temporary, string Destination)>();
        try
        {
            foreach (PageSplitPart part in parts)
            {
                string temporary = Path.Combine(folder, $".killerpdf-split-{Guid.NewGuid():N}.tmp");
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    staged.Add((temporary, Path.Combine(folder, part.FileName)));
                    stream.Write(Build(source, part.PageIndices, rotations));
                    stream.Flush(flushToDisk: true);
                }
            }
            foreach (var (temporary, destination) in staged)
            {
                if (overwrite && File.Exists(destination)) PdfEngineIntegration.ReplaceFileAtomically(temporary, destination);
                else File.Move(temporary, destination, overwrite: false);
            }
        }
        finally
        {
            foreach (var (temporary, _) in staged)
                if (File.Exists(temporary))
                    try { File.Delete(temporary); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return staged.Count;
    }

    // The one-file extract's build (PdfEngineIntegration.ExtractPages): a fresh document importing the pages, with
    // no stale thumbnails, each page turned to the viewer's rotation for it.
    private static byte[] Build(PdfDocument source, IReadOnlyList<int> pageIndices, IReadOnlyDictionary<int, int> rotations)
    {
        PdfDocument empty = PdfDocument.Open(new PdfDocumentBuilder().Build());
        var editor = new PdfIncrementalPageEditor(empty).InsertImportedPages(0, source, pageIndices);
        for (int output = 0; output < pageIndices.Count; output++)
        {
            editor.ClearPageThumbnail(output);
            editor.SetRotation(output, rotations.TryGetValue(pageIndices[output], out int rotation) ? rotation : 0);
        }
        return editor.Build();
    }
}
