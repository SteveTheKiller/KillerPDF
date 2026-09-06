namespace KillerPdf.Engine.Documents;

/// <summary>Measures the reading order of matched OCR words.</summary>
public sealed record PdfOcrReadingOrderMetrics(
    long ComparablePairCount,
    long InversionCount)
{
    private const int MaximumMatches = 1_000_000;

    /// <summary>Gets the fraction of matched word pairs in the expected order.</summary>
    public double Accuracy => ComparablePairCount == 0
        ? 1
        : (ComparablePairCount - InversionCount) / (double)ComparablePairCount;

    /// <summary>Compares recognized list order with expected list order.</summary>
    public static PdfOcrReadingOrderMetrics Compare(
        IReadOnlyList<PdfOcrWordBoxMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        if (matches.Count > MaximumMatches)
            throw new ArgumentException(
                "The OCR reading-order comparison exceeds the match limit.",
                nameof(matches));
        int[] recognizedOrder = [.. matches
            .OrderBy(match => match.ExpectedIndex)
            .Select(match => match.RecognizedIndex)];
        int[] recognizedValues = [.. recognizedOrder.Distinct().Order()];
        var tree = new int[recognizedValues.Length + 1];
        long inversions = 0;
        for (int index = 0; index < recognizedOrder.Length; index++)
        {
            int rank = Array.BinarySearch(recognizedValues, recognizedOrder[index]) + 1;
            int precedingAtOrBelow = 0;
            for (int position = rank; position > 0; position -= position & -position)
                precedingAtOrBelow += tree[position];
            inversions += index - precedingAtOrBelow;
            for (int position = rank; position < tree.Length; position += position & -position)
                tree[position]++;
        }
        long pairs = (long)recognizedOrder.Length * (recognizedOrder.Length - 1) / 2;
        return new PdfOcrReadingOrderMetrics(pairs, inversions);
    }
}
