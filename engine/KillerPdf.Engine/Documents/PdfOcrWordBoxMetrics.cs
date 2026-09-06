namespace KillerPdf.Engine.Documents;

/// <summary>One-to-one overlap measurements for expected and recognized OCR word boxes.</summary>
public sealed record PdfOcrWordBoxMetrics(
    int ExpectedWordCount,
    int RecognizedWordCount,
    int MatchedWordCount,
    int MatchedAtFiftyPercentCount,
    double IntersectionOverUnionSum)
{
    private const int MaximumPairCount = 1_000_000;

    /// <summary>Gets the selected expected-to-recognized word-box matches.</summary>
    public IReadOnlyList<PdfOcrWordBoxMatch> Matches { get; init; } = [];

    /// <summary>Gets average intersection over union, including missing expected words as zero.</summary>
    public double AverageIntersectionOverUnion => ExpectedWordCount == 0
        ? RecognizedWordCount == 0 ? 1 : 0
        : IntersectionOverUnionSum / ExpectedWordCount;

    /// <summary>Gets the fraction of expected words matched at 50 percent overlap or better.</summary>
    public double RecallAtFiftyPercent => ExpectedWordCount == 0
        ? RecognizedWordCount == 0 ? 1 : 0
        : MatchedAtFiftyPercentCount / (double)ExpectedWordCount;

    /// <summary>Matches word boxes greedily by greatest intersection over union.</summary>
    public static PdfOcrWordBoxMetrics Compare(
        IReadOnlyList<PdfOcrPixelWord> expected,
        IReadOnlyList<PdfOcrPixelWord> recognized)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(recognized);
        if ((long)expected.Count * recognized.Count > MaximumPairCount)
            throw new ArgumentException(
                "The OCR word-box comparison exceeds the work limit.");
        Validate(expected, nameof(expected));
        Validate(recognized, nameof(recognized));
        var candidates = new List<(double Overlap, int Expected, int Recognized)>();
        for (int expectedIndex = 0; expectedIndex < expected.Count; expectedIndex++)
            for (int recognizedIndex = 0;
                recognizedIndex < recognized.Count; recognizedIndex++)
            {
                double overlap = IntersectionOverUnion(
                    expected[expectedIndex], recognized[recognizedIndex]);
                if (overlap > 0)
                    candidates.Add((overlap, expectedIndex, recognizedIndex));
            }
        bool[] usedExpected = new bool[expected.Count];
        bool[] usedRecognized = new bool[recognized.Count];
        int matched = 0, matchedAtFiftyPercent = 0;
        double overlapSum = 0;
        var matches = new List<PdfOcrWordBoxMatch>();
        foreach ((double overlap, int expectedIndex, int recognizedIndex) in candidates
            .OrderByDescending(candidate => candidate.Overlap)
            .ThenBy(candidate => candidate.Expected)
            .ThenBy(candidate => candidate.Recognized))
        {
            if (usedExpected[expectedIndex] || usedRecognized[recognizedIndex]) continue;
            usedExpected[expectedIndex] = true;
            usedRecognized[recognizedIndex] = true;
            matched++;
            if (overlap >= 0.5) matchedAtFiftyPercent++;
            overlapSum += overlap;
            matches.Add(new PdfOcrWordBoxMatch(
                expectedIndex, recognizedIndex, overlap));
        }
        return new PdfOcrWordBoxMetrics(expected.Count, recognized.Count,
            matched, matchedAtFiftyPercent, overlapSum)
        {
            Matches = matches
        };
    }

    private static double IntersectionOverUnion(
        PdfOcrPixelWord left, PdfOcrPixelWord right)
    {
        int intersectionWidth = Math.Min(left.Right, right.Right)
            - Math.Max(left.Left, right.Left);
        int intersectionHeight = Math.Min(left.Bottom, right.Bottom)
            - Math.Max(left.Top, right.Top);
        if (intersectionWidth <= 0 || intersectionHeight <= 0) return 0;
        long intersection = (long)intersectionWidth * intersectionHeight;
        long leftArea = (long)(left.Right - left.Left) * (left.Bottom - left.Top);
        long rightArea = (long)(right.Right - right.Left) * (right.Bottom - right.Top);
        return intersection / (double)(leftArea + rightArea - intersection);
    }

    private static void Validate(
        IReadOnlyList<PdfOcrPixelWord> words, string parameterName)
    {
        if (words.Any(word => word is null
            || word.Left < 0 || word.Top < 0
            || word.Right <= word.Left || word.Bottom <= word.Top))
            throw new ArgumentException(
                "An OCR word box is invalid.", parameterName);
    }
}

/// <summary>Maps one expected OCR word box to one recognized word box.</summary>
public sealed record PdfOcrWordBoxMatch(
    int ExpectedIndex,
    int RecognizedIndex,
    double IntersectionOverUnion);
