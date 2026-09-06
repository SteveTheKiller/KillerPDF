namespace KillerPdf.Engine.Documents;

/// <summary>Measures the reading order of matched OCR words.</summary>
public sealed record PdfOcrReadingOrderMetrics(
    long ComparablePairCount,
    long InversionCount)
{
    /// <summary>Gets the fraction of matched word pairs in the expected order.</summary>
    public double Accuracy => ComparablePairCount == 0
        ? 1
        : (ComparablePairCount - InversionCount) / (double)ComparablePairCount;

    /// <summary>Compares recognized list order with expected list order.</summary>
    public static PdfOcrReadingOrderMetrics Compare(
        IReadOnlyList<PdfOcrWordBoxMatch> matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        int[] recognizedOrder = [.. matches
            .OrderBy(match => match.ExpectedIndex)
            .Select(match => match.RecognizedIndex)];
        long inversions = 0;
        for (int left = 0; left < recognizedOrder.Length; left++)
            for (int right = left + 1; right < recognizedOrder.Length; right++)
                if (recognizedOrder[left] > recognizedOrder[right]) inversions++;
        long pairs = (long)recognizedOrder.Length * (recognizedOrder.Length - 1) / 2;
        return new PdfOcrReadingOrderMetrics(pairs, inversions);
    }
}
