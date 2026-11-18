using System;
using System.Collections.Generic;
using System.IO;

namespace KillerPDF.Services;

/// <summary>#502: the files of recently closed tabs, newest last, shared by every window so a tab
/// closed in one window can be reopened from another. Kept in memory only: a closed tab is not
/// written anywhere on disk.</summary>
internal static class ClosedTabs
{
    private const int Limit = 25;
    private static readonly List<string> Paths = [];

    internal static void Remember(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        Paths.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        Paths.Add(path);
        if (Paths.Count > Limit) Paths.RemoveAt(0);
    }

    internal static bool CanReopen => Paths.Exists(File.Exists);

    /// <summary>The newest closed file that still exists, removed from the list. Files deleted or
    /// moved since their tab closed are skipped.</summary>
    internal static string? Take()
    {
        while (Paths.Count > 0)
        {
            string path = Paths[^1];
            Paths.RemoveAt(Paths.Count - 1);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    internal static void Clear() => Paths.Clear();
}
