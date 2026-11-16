using System.IO;

namespace KillerPDF.Services;

/// <summary>
/// The files the Merge dialog will merge, in merge order (#495). Files and folders come from the buttons or from a drop
/// from File Explorer. A folder adds the PDFs directly inside it in name order; anything that is not a PDF, a path that
/// no longer exists and a file already in the list are left out.
/// </summary>
internal sealed class MergeFileList
{
    private readonly List<string> _paths = [];

    internal IReadOnlyList<string> Paths => _paths;

    /// <summary>Adds files and folders and returns how many PDFs went in.</summary>
    internal int Add(IEnumerable<string> paths)
    {
        int added = 0;
        foreach (string path in paths)
        {
            IEnumerable<string> files;
            try
            {
                if (Directory.Exists(path))
                    files = Directory.EnumerateFiles(path, "*", SearchOption.TopDirectoryOnly)
                        .OrderBy(file => Path.GetFileName(file), StringComparer.CurrentCultureIgnoreCase);
                else if (File.Exists(path)) files = [path];
                else continue;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string file in files)
            {
                if (!string.Equals(Path.GetExtension(file), ".pdf", StringComparison.OrdinalIgnoreCase)) continue;
                string full = Path.GetFullPath(file);
                if (_paths.Any(existing => string.Equals(existing, full, StringComparison.OrdinalIgnoreCase))) continue;
                _paths.Add(full);
                added++;
            }
        }
        return added;
    }

    internal void RemoveAt(int index)
    {
        if (index >= 0 && index < _paths.Count) _paths.RemoveAt(index);
    }

    /// <summary>Moves one file; a target past either end lands at that end.</summary>
    internal void Move(int from, int to)
    {
        if (from < 0 || from >= _paths.Count) return;
        string path = _paths[from];
        _paths.RemoveAt(from);
        _paths.Insert(Math.Clamp(to, 0, _paths.Count), path);
    }
}
