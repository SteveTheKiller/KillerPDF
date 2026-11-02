using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace KillerPDF.Services
{
    /// <summary>One document saved aside when the window could no longer draw.</summary>
    internal sealed record RecoveredDocument(string? OriginalPath, string RecoveredPath, string? SuggestedName,
        FileRevision? LoadedRevision = null, string? RecoveryError = null, string? StatePath = null,
        bool Protected = false);

    /// <summary>How an unsaved tab is written aside. A protected document never reaches the folder decrypted.</summary>
    internal enum RecoveryRoute { Plain, UnderItsEncryption, NotWritten }

    /// <summary>What became of a protected recovered copy when its open returned.</summary>
    internal enum ProtectedReopen { Reopened, Pending, NotOpened }

    /// <summary>How a tab open returned: whether the tab it opened into is still loading on a
    /// background thread, and whether any background open is running in its pane at all.</summary>
    internal readonly record struct TabOpen(bool TabLoading, bool PaneLoading);

    /// <summary>Protected recovered copies whose open has not finished, by the path they open from. A
    /// protected copy opens like any protected file, after its password or on a background thread, so it
    /// waits here until the open that finishes takes it home. An open that ends with no tab gives it up.</summary>
    internal sealed class ProtectedRecoveryList
    {
        private readonly Dictionary<string, RecoveredDocument> _waiting = new(StringComparer.OrdinalIgnoreCase);

        internal void Expect(RecoveredDocument document) => _waiting[document.RecoveredPath] = document;

        /// <summary>The copy an open of this path finished, or null when none was waiting for it.</summary>
        internal RecoveredDocument? Take(string openedPath) =>
            _waiting.Remove(openedPath, out RecoveredDocument? document) ? document : null;

        /// <summary>Gives up a copy whose background open failed or was canceled.</summary>
        internal void Abandon(string openedPath) => _waiting.Remove(openedPath);

        /// <summary>Where a copy stands once its open has returned. Taken means it went home. Still
        /// waiting with the open running on a background thread means it may yet. Still waiting with
        /// nothing running means the prompt was canceled or the open failed, so it is given up.</summary>
        internal ProtectedReopen Settle(string recoveredPath, TabOpen open)
        {
            if (!_waiting.ContainsKey(recoveredPath)) return ProtectedReopen.Reopened;
            if (open.PaneLoading) return ProtectedReopen.Pending;
            _waiting.Remove(recoveredPath);
            return ProtectedReopen.NotOpened;
        }
    }

    /// <summary>
    /// WPF's render thread can fail for good (UCEERR_RENDERTHREADFAILURE, often after sleep, a
    /// display change or a graphics driver reset). The window then stays black and cannot recover,
    /// so KillerPDF saves unsaved documents aside and restarts instead of offering Continue (#456).
    /// </summary>
    internal static class RenderFailureRecovery
    {
        internal const int RenderThreadFailureHResult = unchecked((int)0x88980406);
        internal const string RecoverySwitch = "--render-recovery";

        /// <summary>Decides how a tab is written aside when the window fails.</summary>
        internal static RecoveryRoute Route(bool readOnly, bool protectedSource, OpenedProtection protection) =>
            readOnly ? RecoveryRoute.NotWritten
            : !protectedSource ? RecoveryRoute.Plain
            : protection.CanSeal ? RecoveryRoute.UnderItsEncryption : RecoveryRoute.NotWritten;

        /// <summary>Writes a finished snapshot into the recovery folder by the tab's route.</summary>
        internal static void WriteCopy(string snapshot, string copy, RecoveryRoute route, OpenedProtection protection)
        {
            if (route == RecoveryRoute.UnderItsEncryption) protection.Seal(snapshot, copy);
            else File.Copy(snapshot, copy, overwrite: false);
        }

        /// <summary>Whether a recovered tab may save back over its original. A protected original takes
        /// only a tab that still holds its protection, so nothing plain can land on it.</summary>
        internal static bool MayReturnHome(RecoveredDocument document, OpenedProtection reopened) =>
            !document.Protected || reopened.CanSeal;

        internal static bool IsRenderThreadFailure(Exception? exception)
        {
            for (Exception? e = exception; e is not null; e = e.InnerException)
            {
                if (e is COMException { HResult: RenderThreadFailureHResult }) return true;
                if (e is AggregateException aggregate)
                    foreach (Exception inner in aggregate.InnerExceptions)
                        if (IsRenderThreadFailure(inner)) return true;
            }
            return false;
        }

        /// <summary>A fresh, dated folder under the user's data root for this recovery.</summary>
        internal static string CreateFolder(string root)
        {
            string folder = Path.Combine(root, "Recovery", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(folder);
            return folder;
        }

        /// <summary>
        /// Where a recovery folder may go, most preferred first: the user's data root, the local data
        /// root when the user's is portable, then a KillerPDF folder in the system temp. None is the
        /// session temp root, whose files a restarted session treats as imports, not recoveries.
        /// </summary>
        internal static IReadOnlyList<string> FolderRoots(string userRoot, string localRoot, string systemTemp) =>
            [.. new[] { userRoot, localRoot, Path.Combine(systemTemp, "KillerPDF") }
                .Select(root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)))
                .Distinct(StringComparer.OrdinalIgnoreCase)];

        /// <summary>A fresh recovery folder under the first root that accepts one, or null when none does.</summary>
        internal static string? CreateFolder(IEnumerable<string> roots)
        {
            foreach (string root in roots)
                try { return CreateFolder(root); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                    or NotSupportedException or ArgumentException) { }
            return null;
        }

        /// <summary>A file name for a recovered copy that cannot collide or escape the folder.</summary>
        internal static string CopyPath(string folder, string? name, int index)
        {
            string stem = Path.GetFileNameWithoutExtension(string.IsNullOrWhiteSpace(name) ? "document" : name);
            foreach (char invalid in Path.GetInvalidFileNameChars()) stem = stem.Replace(invalid, '_');
            if (stem.Length == 0) stem = "document";
            return Path.Combine(folder, $"{index + 1:00}-{stem}.pdf");
        }

        /// <summary>
        /// The name a document with nowhere to be saved is listed under: its file, the name its first
        /// save would offer, or <paramref name="untitled"/>.
        /// </summary>
        internal static string LostName(string? originalFile, string? suggestedName, string untitled) =>
            originalFile is { Length: > 0 } ? Path.GetFileName(originalFile) : suggestedName ?? untitled;

        /// <summary>The restart's line for a lost document: its name and why it was lost.</summary>
        internal static string LostNote(string? originalFile, string? suggestedName, string untitled, string reason) =>
            LostName(originalFile, suggestedName, untitled) + ": " + reason;

        /// <summary>The name the restart lists a recovered or lost document under.</summary>
        internal static string DocumentName(RecoveredDocument document) =>
            Path.GetFileName(document.OriginalPath ?? document.SuggestedName ?? document.RecoveredPath);

        /// <summary>
        /// The restart's line for a document that did not fully recover. Several errors share the
        /// line, so none of them reads like another document.
        /// </summary>
        internal static string ProblemNote(RecoveredDocument document) =>
            DocumentName(document) + ": " + string.Join("; ",
                (document.RecoveryError ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

        /// <summary>The restart's list: one line per note, in order, even when two read the same.</summary>
        internal static string JoinNotes(IEnumerable<string> notes) => string.Join("\n", notes);

        internal static string WriteManifest(string folder, IReadOnlyList<RecoveredDocument> documents)
        {
            string path = Path.Combine(folder, "recovery.json");
            AtomicFileOutput.Write(path, JsonSerializer.SerializeToUtf8Bytes(documents));
            return path;
        }

        /// <summary>The recovered documents, keeping only copies that are still inside the recovery folder.</summary>
        internal static IReadOnlyList<RecoveredDocument> ReadManifest(string path)
        {
            try
            {
                string folder = Path.GetFullPath(Path.GetDirectoryName(path)!) + Path.DirectorySeparatorChar;
                var documents = JsonSerializer.Deserialize<List<RecoveredDocument>>(File.ReadAllBytes(path)) ?? [];
                return documents.FindAll(d => !string.IsNullOrEmpty(d.RecoveredPath)
                    && Path.GetFullPath(d.RecoveredPath).StartsWith(folder, StringComparison.OrdinalIgnoreCase)
                    && (File.Exists(d.RecoveredPath) || d.RecoveryError is not null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException
                or ArgumentException or NotSupportedException)
            {
                return [];
            }
        }
    }
}
