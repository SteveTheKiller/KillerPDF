namespace KillerPDF.Services;

/// <summary>The words of a scanned line an edit really changes: original words First through Last
/// give way to Replacement, and everything else keeps its scanned look.</summary>
internal sealed record ScanEditSpan(int First, int Last, string Replacement)
{
    /// <summary>Trims the words an edit left alone from both ends, or returns null for no change.</summary>
    internal static ScanEditSpan? Diff(IReadOnlyList<string> original, string typed)
    {
        ArgumentNullException.ThrowIfNull(original);
        string[] edited = (typed ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        int start = 0;
        while (start < original.Count && start < edited.Length
            && string.Equals(original[start], edited[start], StringComparison.Ordinal)) start++;
        int originalEnd = original.Count, editedEnd = edited.Length;
        while (originalEnd > start && editedEnd > start
            && string.Equals(original[originalEnd - 1], edited[editedEnd - 1], StringComparison.Ordinal))
        {
            originalEnd--;
            editedEnd--;
        }
        if (start == originalEnd && start == editedEnd) return null;
        // Pure insertion has no word to cover, so it takes over the word before it.
        if (start == originalEnd && start > 0) start--;
        else if (start == originalEnd) originalEnd++;
        return new ScanEditSpan(start, originalEnd - 1, string.Join(' ', edited[Math.Min(start, editedEnd)..editedEnd]));
    }
}
