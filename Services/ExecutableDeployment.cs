using System;
using System.IO;
using System.Security.Cryptography;

namespace KillerPDF.Services
{
    internal static class ExecutableDeployment
    {
        internal sealed class FileOperations
        {
            internal Action<Stream, Stream> Copy = (input, output) => input.CopyTo(output);
            internal Action<string, string, string?> Replace = File.Replace;
            internal Action<string, string> Move = File.Move;
            internal Action<string> Delete = File.Delete;
        }

        internal static void Install(string source, string destination)
            => Install(source, destination, new FileOperations());

        internal static void Install(string source, string destination, FileOperations files)
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
            bool retainStaged = false;
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    byte[] expected;
                    using (var hash = SHA256.Create()) expected = hash.ComputeHash(input);
                    input.Position = 0;
                    using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        files.Copy(input, output);
                        output.Flush(true);
                    }
                    using (var copy = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var hash = SHA256.Create())
                    {
                        byte[] actual = hash.ComputeHash(copy);
                        if (copy.Length != input.Length || !System.Linq.Enumerable.SequenceEqual(expected, actual))
                            throw new InvalidDataException("The staged executable does not match the complete source.");
                    }
                }
                Publish(staged, destination, files, ref retainStaged);
            }
            finally
            {
                if (!retainStaged) TryDelete(staged, files);
            }
        }

        /// <summary>
        /// Deletes the backups earlier publications left beside <paramref name="destination"/>. Replacing
        /// a running exe renames the mapped image to its backup, which cannot be deleted until that
        /// process exits, so it is cleared by the next publication or uninstall instead. Only regular
        /// files named exactly "&lt;destination&gt;.&lt;32 hex&gt;.bak" are touched, and one still in use, or one a failed
        /// publication kept for recovery (marked by a ".keep" file beside it), stays.
        /// </summary>
        internal static void RemoveRetiredBackups(string destination) => RemoveRetiredBackups(destination, new FileOperations());

        private static void RemoveRetiredBackups(string destination, FileOperations files)
        {
            string? directory = Path.GetDirectoryName(destination);
            if (directory == null || !Directory.Exists(directory)) return;
            string prefix = Path.GetFileName(destination) + ".";
            string[] candidates;
            try { candidates = Directory.GetFiles(directory, prefix + "*.bak"); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (string path in candidates)
            {
                string name = Path.GetFileName(path);
                if (name.Length != prefix.Length + 32 + 4
                    || !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _))
                    continue;
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                    // A failed publication told the user to keep this backup for recovery.
                    if (File.Exists(path + KeepMarker)) continue;
                }
                catch { continue; }
                TryDelete(path, files);
            }
            RemovePublicationLeftovers(destination, files);
        }

        /// <summary>
        /// The self-update publication names its working files "&lt;exe&gt;.old-", ".new-" and ".restore-"
        /// plus 32 hex, and the trusted path stages ".update-&lt;32 hex&gt;.exe" with a report beside it.
        /// A cleanup that failed (the old image still mapped by another session) leaves them, and
        /// they would keep the folder at uninstall. They go once the installed exe is in place and
        /// they are over a day old, so a recovery copy from a failed update is never taken while
        /// it could still matter. The trusted staging (TrustedUpdate.Stage) runs this with the
        /// installed exe once its own copy is verified.
        /// </summary>
        internal static void RemovePublicationLeftovers(string installedExe) => RemovePublicationLeftovers(installedExe, new FileOperations());

        private static void RemovePublicationLeftovers(string destination, FileOperations files)
        {
            string? directory = Path.GetDirectoryName(destination);
            if (directory == null || !File.Exists(destination)) return;
            string exe = Path.GetFileName(destination);
            string[] all;
            try { all = Directory.GetFiles(directory); }
            catch (IOException) { return; }
            catch (UnauthorizedAccessException) { return; }
            foreach (string path in all)
            {
                if (!IsPublicationLeftover(Path.GetFileName(path), exe)) continue;
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                    if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < TimeSpan.FromDays(1)) continue;
                    if (File.Exists(path + KeepMarker)) continue;
                }
                catch { continue; }
                TryDelete(path, files);
            }
        }

        private static bool IsPublicationLeftover(string name, string exe)
        {
            foreach (string marker in new[] { ".old-", ".new-", ".restore-" })
                if (name.Length == exe.Length + marker.Length + 32 && name.StartsWith(exe + marker, StringComparison.OrdinalIgnoreCase)
                    && Guid.TryParseExact(name.Substring(exe.Length + marker.Length), "N", out _))
                    return true;
            string staged = name.EndsWith(".recovery.txt", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - ".recovery.txt".Length) : name;
            return staged.Length == ".update-".Length + 32 + ".exe".Length && staged.StartsWith(".update-", StringComparison.OrdinalIgnoreCase)
                && staged.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && Guid.TryParseExact(staged.Substring(8, 32), "N", out _);
        }

        private static bool IsRetiredBackup(string name, string exe)
        {
            string prefix = exe + ".";
            return name.Length == prefix.Length + 32 + 4 && name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)
                && Guid.TryParseExact(name.Substring(prefix.Length, 32), "N", out _);
        }

        /// <summary>
        /// The update working files in an install folder, by name, for an uninstall to take along with
        /// the installed files. Age does not matter here: nothing is coming back to recover from them,
        /// and they would keep the folder. A recovery copy marked to keep stays, since a failed
        /// publication told the user it was there.
        /// </summary>
        internal static string[] UninstallLeftovers(string directory, string exe)
        {
            string[] all;
            try { all = Directory.GetFiles(directory); }
            catch (IOException) { return []; }
            catch (UnauthorizedAccessException) { return []; }
            var leftovers = new System.Collections.Generic.List<string>();
            foreach (string path in all)
            {
                string name = Path.GetFileName(path);
                if (!IsPublicationLeftover(name, exe) && !IsRetiredBackup(name, exe)) continue;
                try
                {
                    if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) continue;
                    if (File.Exists(path + KeepMarker)) continue;
                }
                catch { continue; }
                leftovers.Add(name);
            }
            return leftovers.ToArray();
        }

        private static void Publish(string staged, string destination, FileOperations files, ref bool retainStaged)
        {
            RemoveRetiredBackups(destination, files);
            string backup = destination + "." + Guid.NewGuid().ToString("N") + ".bak";
            try
            {
                if (File.Exists(destination))
                {
                    try
                    {
                        files.Replace(staged, destination, backup);
                        TryDelete(backup, files);
                        return;
                    }
                    catch (IOException) { retainStaged = File.Exists(backup) || !File.Exists(destination); }
                    catch (PlatformNotSupportedException) { }
                }
                if (File.Exists(destination))
                {
                    if (File.Exists(backup)) { retainStaged = true; throw new IOException("The replacement left both destination and backup files."); }
                    files.Move(destination, backup);
                    retainStaged = true;
                }
                files.Move(staged, destination);
                retainStaged = false;
                TryDelete(backup, files);
            }
            catch (Exception publicationError)
            {
                if (!File.Exists(destination))
                {
                    retainStaged = true;
                    string recovery = File.Exists(backup) ? backup : staged;
                    try
                    {
                        string restored = destination + "." + Guid.NewGuid().ToString("N") + ".restore";
                        File.Copy(recovery, restored);
                        files.Move(restored, destination);
                        if (recovery == staged) { retainStaged = false; return; }
                    }
                    catch (Exception recoveryError)
                    {
                        throw RecoveryError(staged, backup, new AggregateException(publicationError, recoveryError));
                    }
                }
                if (retainStaged) throw RecoveryError(staged, backup, publicationError);
                throw;
            }
        }

        private const string KeepMarker = ".keep";

        /// <summary>Marks the backup as a recovery file before naming it, so a later publication leaves it alone.</summary>
        private static IOException RecoveryError(string staged, string backup, Exception error)
        {
            if (File.Exists(backup)) { try { File.WriteAllText(backup + KeepMarker, string.Empty); } catch { } }
            return new IOException("The executable could not be published. Recovery files were kept. Staged: " + staged + ". Backup: " + backup, error);
        }

        private static void TryDelete(string path, FileOperations files)
        {
            try { files.Delete(path); } catch { }
        }
    }
}
