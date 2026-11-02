using System.IO;

namespace KillerPDF.Services;

internal sealed class ProcessTempFiles : IDisposable
{
    private const string LeaseName = "owner.lease";
    private const string RecoveryRetentionName = "recovery-retained.json";
    // A retained session holds unsaved work after a recovery that did not finish. It is kept for a
    // month so it can be found by hand, then cleaned up like any other stale session.
    private static readonly TimeSpan RecoveryRetention = TimeSpan.FromDays(30);
    private readonly string root;
    private readonly List<string> paths = [];
    private readonly List<string> directories = [];
    private readonly Lazy<(string Directory, FileStream Lease)> owned;
    private bool disposed;
    private bool retainedForRecovery;
    private readonly Action<string, byte[]> writeMarker;

    public ProcessTempFiles(string root, Action<string, byte[]>? writeMarker = null)
    {
        this.root = Path.GetFullPath(root);
        this.writeMarker = writeMarker ?? ((path, bytes) => AtomicFileOutput.Write(path, bytes));
        owned = new(() =>
        {
            string directory = Path.Combine(this.root, "session-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var lease = new FileStream(Path.Combine(directory, LeaseName),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            return (directory, lease);
        });
    }

    public string DirectoryPath
    {
        get
        {
            lock (paths)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                return owned.Value.Directory;
            }
        }
    }

    public string CreatePath(string tag)
    {
        lock (paths)
        {
            string path = Path.Combine(DirectoryPath, $"killerpdf_{tag}_{Guid.NewGuid():N}.pdf");
            paths.Add(path);
            return path;
        }
    }

    public string CreateImportDirectoryPath()
    {
        lock (paths)
        {
            string directory = Path.Combine(DirectoryPath, "import-" + Guid.NewGuid().ToString("N"));
            directories.Add(directory);
            return directory;
        }
    }

    /// <summary>True for any path under the temp root, which this or a later process deletes.</summary>
    public bool Owns(string path)
    {
        try
        {
            string full = Path.GetFullPath(path);
            return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>This session's folder when it is kept for a recovery, so a restart can be told to keep it.</summary>
    internal string? RetainedDirectory
    {
        get
        {
            lock (paths) return retainedForRecovery && owned.IsValueCreated ? owned.Value.Directory : null;
        }
    }

    internal void RetainForRecovery(string? manifest)
    {
        lock (paths)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            retainedForRecovery = true;
            string marker = Path.Combine(DirectoryPath, RecoveryRetentionName);
            try
            {
                writeMarker(marker, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Manifest = manifest }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (File.Exists(marker)) return;
                // A full disk can refuse even a small marker. Renaming the lease needs no new
                // space, so the lease becomes the marker and later launches keep the session too.
                // It is released first because it is held without delete sharing.
                owned.Value.Lease.Dispose();
                MarkWithLease(owned.Value.Directory);
            }
        }
    }

    private static void MarkWithLease(string directory)
    {
        string marker = Path.Combine(directory, RecoveryRetentionName);
        File.Move(Path.Combine(directory, LeaseName), marker);
        File.SetLastWriteTimeUtc(marker, DateTime.UtcNow);
    }

    public void CleanupOwned()
    {
        lock (paths)
        {
            if (retainedForRecovery || owned.IsValueCreated
                && File.Exists(Path.Combine(owned.Value.Directory, RecoveryRetentionName))) return;
            foreach (string path in paths)
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            paths.Clear();
            foreach (string directory in directories)
                try { if (Directory.Exists(directory)) Directory.Delete(directory, true); } catch { }
            directories.Clear();
        }
    }

    /// <summary>
    /// Removes sessions no running process holds. <paramref name="retainedSession"/> is the folder of
    /// the process a render-failure restart replaces: it is kept and marked even when that process
    /// could not write its own marker, for example on a full disk.
    /// </summary>
    public void CleanupStale(string? retainedSession = null)
    {
        try
        {
            if (!Directory.Exists(root)
                || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return;
            string? kept = KeptSession(retainedSession);
            foreach (string directory in Directory.GetDirectories(root, "session-*"))
            {
                try
                {
                    string name = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(name.AsSpan("session-".Length), "N", out _)
                        || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                        continue;
                    string retention = Path.Combine(directory, RecoveryRetentionName);
                    string leasePath = Path.Combine(directory, LeaseName);
                    if (string.Equals(Path.GetFullPath(directory), kept, StringComparison.OrdinalIgnoreCase))
                    {
                        if (!File.Exists(retention))
                        {
                            try
                            {
                                writeMarker(retention,
                                    System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { Manifest = (string?)null }));
                            }
                            // The replaced process has exited, so its lease is free to become the marker.
                            catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException)
                                && File.Exists(leasePath))
                            {
                                MarkWithLease(directory);
                            }
                        }
                        continue;
                    }
                    bool expired = false;
                    if (File.Exists(retention))
                    {
                        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(retention) < RecoveryRetention) continue;
                        File.Delete(retention);
                        expired = true;
                    }
                    bool hasLease = File.Exists(leasePath);
                    if (hasLease && (File.GetAttributes(leasePath) & FileAttributes.ReparsePoint) != 0) continue;
                    // A session without a lease is skipped, unless its lease became an expired marker.
                    if (!hasLease && !expired) continue;
                    // An active process holds this handle exclusively for its whole lifetime.
                    using (FileStream? lease = hasLease
                        ? new FileStream(leasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null)
                    {
                        foreach (string file in Directory.GetFiles(directory, "killerpdf_*.pdf"))
                            try { File.Delete(file); } catch { }
                        foreach (string import in Directory.GetDirectories(directory, "import-*"))
                        {
                            if (!Guid.TryParseExact(Path.GetFileName(import).AsSpan("import-".Length), "N", out _)
                                || (File.GetAttributes(import) & FileAttributes.ReparsePoint) != 0) continue;
                            try { Directory.Delete(import, true); } catch { }
                        }
                        if (Directory.EnumerateFileSystemEntries(directory).Any(entry =>
                            !string.Equals(Path.GetFileName(entry), LeaseName, StringComparison.OrdinalIgnoreCase)))
                            continue;
                    }
                    if (hasLease) File.Delete(leasePath);
                    // Unknown files prevent removal instead of being recursively deleted.
                    Directory.Delete(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    // The retained session named on a restart's command line, or null when it is blank or not a path.
    private static string? KeptSession(string? retainedSession)
    {
        if (string.IsNullOrWhiteSpace(retainedSession)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(retainedSession)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        lock (paths)
        {
            if (disposed) return;
            CleanupOwned();
            if (owned.IsValueCreated) owned.Value.Lease.Dispose();
            disposed = true;
        }
    }
}
