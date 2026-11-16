using System.IO;

namespace KillerPDF.Controls;

/// <summary>Why a typed folder name cannot be created, or None.</summary>
public enum FolderNameProblem { None, Empty, InvalidCharacters, Reserved, TrailingDotOrSpace, TooLong, Exists }

/// <summary>
/// The New folder rules for the open and save dialogs: the typed name is trimmed and checked against what Windows
/// refuses before anything is created, and the name box starts with the first free "New folder" name.
/// </summary>
internal static class FolderNameRule
{
    private const int MaximumLength = 255;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    internal static string Clean(string name) => name.Trim();

    /// <summary>Checks a typed name. <paramref name="exists"/> answers whether the cleaned name is already taken.</summary>
    internal static FolderNameProblem Check(string name, Func<string, bool> exists)
    {
        string cleaned = Clean(name);
        if (cleaned.Length == 0) return FolderNameProblem.Empty;
        if (cleaned.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || cleaned.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
            return FolderNameProblem.InvalidCharacters;
        if (cleaned.EndsWith('.') || cleaned.EndsWith(' ')) return FolderNameProblem.TrailingDotOrSpace;
        string stem = cleaned.Split('.')[0];
        if (ReservedNames.Contains(stem)) return FolderNameProblem.Reserved;
        if (cleaned.Length > MaximumLength) return FolderNameProblem.TooLong;
        return exists(cleaned) ? FolderNameProblem.Exists : FolderNameProblem.None;
    }

    /// <summary>The base name if free, otherwise "base (2)", "base (3)" and so on.</summary>
    internal static string FirstFree(string baseName, Func<string, bool> exists)
    {
        if (!exists(baseName)) return baseName;
        for (int number = 2; ; number++)
        {
            string candidate = $"{baseName} ({number})";
            if (!exists(candidate)) return candidate;
        }
    }
}
