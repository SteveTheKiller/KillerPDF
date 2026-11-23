#nullable enable
using System;
using System.IO;

namespace KillerPDF.Controls
{
    /// <summary>
    /// When the file dialog may start accepting. A running listing or accept always blocks it. A folder
    /// listing that failed or timed out blocks it too, unless the name box holds a fully qualified path,
    /// which the accept step resolves without the listing. Folder mode accepts the shown folder, so it
    /// always needs the listing.
    /// </summary>
    internal static class PickerAcceptRule
    {
        public static bool CanAccept(bool listingBusy, bool listingAvailable, bool accepting, bool folderMode, string? typedName)
        {
            if (listingBusy || accepting) return false;
            if (listingAvailable) return true;
            if (folderMode) return false;
            string typed = (typedName ?? "").Trim().Trim('"');
            return typed.Length > 0 && typed.IndexOfAny(Path.GetInvalidPathChars()) < 0 && IsFullyQualified(typed);
        }

        /// <summary>Whether an accept asks for the files in a new window (KillerPDF #513): Shift held with Enter or the
        /// Open button, in Open mode. Save and folder pickers have nothing to open.</summary>
        public static bool OpensInNewWindow(bool shiftHeld, bool openMode) => shiftHeld && openMode;

        /// <summary>Whether Enter on the file list opens a folder rather than accepting: only one row is highlighted and
        /// it is a folder. Files, or several rows, are accepted like the Open button.</summary>
        public static bool ListEnterOpensFolder(int selectedCount, bool firstIsFolder) => selectedCount == 1 && firstIsFolder;

        // Path.IsPathFullyQualified does not exist on .NET Framework, which the apps target.
        private static bool IsFullyQualified(string path)
        {
            if (path.StartsWith(@"\\") || path.StartsWith("//"))
            {
                // \\server\share at least: a bare \\ or \\server names no place to resolve.
                var parts = path.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.None);
                return parts.Length >= 2 && parts[0].Length > 0 && parts[1].Length > 0;
            }
            return path.Length >= 3 && char.IsLetter(path[0]) && path[1] == ':' && (path[2] == '\\' || path[2] == '/');
        }
    }
}
