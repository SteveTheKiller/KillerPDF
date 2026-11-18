namespace KillerPDF.Services
{
    /// <summary>Whether a superseded working copy is still named by an open tab or window, so the edit that
    /// replaced it must leave it for session cleanup rather than delete it now.</summary>
    internal static class WorkingCopyReferences
    {
        /// <summary>One tab's file names. IsEdited marks the tab the edit just moved on: its CurrentFile can
        /// still hold the old path until it is next captured, so only its OriginalFile and DeferredPath count.</summary>
        internal readonly record struct SessionFiles(string? CurrentFile, string? OriginalFile, string? DeferredPath, bool IsEdited);

        internal static bool InUse(string path, IEnumerable<SessionFiles> sessions, IEnumerable<string?> liveFiles)
        {
            bool Same(string? name) => string.Equals(name, path, StringComparison.OrdinalIgnoreCase);
            return sessions.Any(session => Same(session.OriginalFile) || Same(session.DeferredPath)
                    || (!session.IsEdited && Same(session.CurrentFile)))
                || liveFiles.Any(Same);
        }
    }
}
