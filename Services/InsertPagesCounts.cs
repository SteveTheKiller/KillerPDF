namespace KillerPDF.Services;

/// <summary>
/// The page counts of Insert Pages' sources. A tab that has not loaded is listed with no count, and a background read
/// fills it in when it finishes; a count already known is never replaced.
/// </summary>
internal sealed class InsertPagesCounts
{
    private readonly int?[] _counts;

    internal InsertPagesCounts(IEnumerable<int?> counts) => _counts = [.. counts];

    internal int? this[int index] => index >= 0 && index < _counts.Length ? _counts[index] : null;

    /// <summary>Records a count for a source that has none; false when nothing changed.</summary>
    internal bool TryFill(int index, int count)
    {
        if (index < 0 || index >= _counts.Length || count < 1 || _counts[index] is not null) return false;
        _counts[index] = count;
        return true;
    }
}
