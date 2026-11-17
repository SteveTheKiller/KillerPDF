using System.IO;

namespace KillerPDF.Services;

/// <summary>
/// Names what a KillerPDF payload folder owns, so uninstall and conflict repair remove those
/// files and nothing else. Ownership comes from the payload manifest the installer wrote, plus
/// the two files registration adds beside it. A folder without a manifest owns nothing, and no
/// caller removes it.
/// </summary>
internal static class InstallPayloadOwnership
{
    private const string ManifestName = "payload.manifest";
    private const string PortableMarkerName = ".killerpdf-portable";

    /// <summary>Absolute paths of the owned files, and the owned folders deepest first.</summary>
    internal sealed record OwnedPayload(string Root, IReadOnlyList<string> Files, IReadOnlyList<string> Directories);

    /// <summary>
    /// Reads the manifest in <paramref name="directory"/>. Null when there is no manifest or it
    /// names a path outside the folder.
    /// </summary>
    internal static OwnedPayload? Read(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return null;
        string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string manifestPath = Path.Combine(root, ManifestName);
        if (!File.Exists(manifestPath)) return null;
        string rootPrefix = root + Path.DirectorySeparatorChar;
        var files = new List<string>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            string[] parts = line.Split('\t', 3);
            if (parts.Length != 3) return null;
            string relative = parts[2].Replace('/', Path.DirectorySeparatorChar).TrimStart(Path.DirectorySeparatorChar);
            if (relative.Length == 0 || Path.IsPathRooted(parts[2]) || parts[2].Contains(':')) return null;
            string path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) return null;
            files.Add(path);
            for (string? parent = Path.GetDirectoryName(path);
                parent is not null && parent.Length > root.Length;
                parent = Path.GetDirectoryName(parent))
                directories.Add(parent);
        }
        foreach (string extra in new[] { ManifestName, PortableMarkerName, CommandLinePath.CommandFileName })
        {
            string path = Path.Combine(root, extra);
            if (File.Exists(path)) files.Add(path);
        }
        return new OwnedPayload(root, files,
            [.. directories.OrderByDescending(path => path.Length).ThenBy(path => path, StringComparer.OrdinalIgnoreCase)]);
    }

    /// <summary>
    /// Deletes the owned files and removes the owned folders that are then empty, never anything
    /// else. Returns true when the root folder itself was empty and removed.
    /// </summary>
    internal static bool Remove(OwnedPayload owned)
    {
        foreach (string file in owned.Files)
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (string directory in owned.Directories)
            try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false); }
            catch (IOException) { } catch (UnauthorizedAccessException) { }
        try
        {
            Directory.Delete(owned.Root, recursive: false);
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// The cmd.exe lines that remove the payload after the process exits. Each file is deleted by
    /// name and each folder is removed only when empty, so a folder the user shares stays.
    /// </summary>
    internal static string RemovalBatch(OwnedPayload owned)
    {
        // Quoting protects spaces and command separators, but not environment expansion.
        // Batch files require doubled percent signs; delayed expansion must stay disabled
        // through every deletion, including the caller's final self-delete command.
        var batch = new System.Text.StringBuilder("setlocal DisableDelayedExpansion\r\n");
        // A process that is still closing can hold the files for a moment, so the pass repeats
        // while an owned file remains, for about thirty seconds at most. A folder that keeps only
        // the user's own files ends it. One statement per line keeps the counter expanding as it
        // changes without delayed expansion.
        batch.Append("set /a killerpdf_attempts=0\r\n:killerpdf_remove\r\n");
        foreach (string file in owned.Files) batch.Append("del /q \"").Append(Literal(file)).Append("\"\r\n");
        foreach (string directory in owned.Directories) batch.Append("rmdir \"").Append(Literal(directory)).Append("\"\r\n");
        batch.Append("rmdir \"").Append(Literal(owned.Root)).Append("\"\r\n");
        batch.Append("if not exist \"").Append(Literal(owned.Root)).Append("\" goto killerpdf_removed\r\n");
        foreach (string file in owned.Files) batch.Append("if exist \"").Append(Literal(file)).Append("\" goto killerpdf_retry\r\n");
        batch.Append("goto killerpdf_removed\r\n")
            .Append(":killerpdf_retry\r\n")
            .Append("set /a killerpdf_attempts+=1\r\n")
            .Append("if %killerpdf_attempts% geq 30 goto killerpdf_removed\r\n")
            .Append("ping -n 2 127.0.0.1 >nul\r\n")
            .Append("goto killerpdf_remove\r\n")
            .Append(":killerpdf_removed\r\n");
        return batch.ToString();

        static string Literal(string path) => path.Replace("%", "%%", StringComparison.Ordinal);
    }

    // cmd.exe decodes a batch file in its console's code page, not UTF-8, so a folder such as
    // C:\Users\José would not match. The script's hidden console switches to UTF-8 first; cmd reads
    // the file a line at a time, so every later line is read as UTF-8.
    /// <summary>
    /// Writes the script that removes the payload once this process has exited, then deletes itself.
    /// </summary>
    internal static void WriteRemovalScript(string path, OwnedPayload owned, bool removeFolder = false) =>
        File.WriteAllText(path,
            "@echo off\r\n" +
            "chcp 65001 >nul\r\n" +
            "ping -n 3 127.0.0.1 >nul\r\n" +
            RemovalBatch(owned) +
            // A protected script folder of its own goes with the script; %TEMP% stays.
            (removeFolder ? "del \"%~f0\" & rd \"%~dp0.\"\r\n" : "del \"%~f0\"\r\n"));
}
