using System.IO;

namespace KillerPDF.Services;

/// <summary>
/// Clears out the copies Explorer opens leave behind. An item with no path on disk, such as a PDF inside a zip
/// folder, is copied to a folder of its own under the Explorer copy root, and the copy is never deleted when its tab
/// closes because the user may have saved edits into it. Startup sweeps the folders nobody has touched for a while.
/// </summary>
internal static class ExplorerCopySweep
{
    /// <summary>How long a copy folder must sit untouched before the sweep deletes it.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>
    /// Deletes the copy folders under <paramref name="root"/> whose newest file was written before
    /// <paramref name="cutoffUtc"/>, and returns how many it removed. A folder with a file that
    /// <paramref name="isInUse"/> reports, or that cannot be read or deleted, is skipped without an error.
    /// </summary>
    internal static int Sweep(string root, DateTime cutoffUtc, Func<string, bool> isInUse)
    {
        int removed = 0;
        string[] folders;
        try
        {
            if (!Directory.Exists(root)) return 0;
            folders = Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return 0; }
        foreach (string folder in folders)
        {
            try
            {
                // Only the folders the copy step makes, named by a guid, and never one that is a link elsewhere.
                if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out _)) continue;
                if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) continue;
                DateTime newest = DateTime.MinValue;
                var files = new List<string>();
                foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
                {
                    files.Add(file);
                    DateTime written = File.GetLastWriteTimeUtc(file);
                    if (written > newest) newest = written;
                }
                // A folder with no files has only its own time to go by.
                if (files.Count == 0) newest = Directory.GetLastWriteTimeUtc(folder);
                if (newest >= cutoffUtc) continue;
                if (files.Any(isInUse)) continue;
                Directory.Delete(folder, recursive: true);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        return removed;
    }

    /// <summary>
    /// The in-use check startup passes the sweep: a file the stored session will reopen counts as in use, because the
    /// sweep runs before any tab has it open, and so does a file something else has open. A session that could not be
    /// read (null) keeps every copy, since any of them might be in it.
    /// </summary>
    internal static Func<string, bool> InUse(IEnumerable<string>? sessionFiles)
    {
        if (sessionFiles is null) return _ => true;
        var kept = new HashSet<string>(sessionFiles.Select(Full), StringComparer.OrdinalIgnoreCase);
        return file => kept.Contains(Full(file)) || IsLocked(file);
    }

    private static string Full(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    /// <summary>True when the file cannot be opened for exclusive use, which is a sign something has it open.</summary>
    internal static bool IsLocked(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return false;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
