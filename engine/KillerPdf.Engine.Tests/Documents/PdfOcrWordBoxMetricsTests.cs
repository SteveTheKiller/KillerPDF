using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrWordBoxMetricsTests
{
    [Fact]
    public void CompareMatchesEachWordToItsGreatestOverlap()
    {
        PdfOcrWordBoxMetrics metrics = PdfOcrWordBoxMetrics.Compare(
        [
            Word(0, 0, 10, 10),
            Word(20, 0, 30, 10),
            Word(40, 0, 50, 10)
        ],
        [
            Word(21, 0, 31, 10),
            Word(0, 0, 10, 10),
            Word(22, 0, 28, 10)
        ]);

        Assert.Equal((3, 3, 2, 2), (metrics.ExpectedWordCount,
            metrics.RecognizedWordCount, metrics.MatchedWordCount,
            metrics.MatchedAtFiftyPercentCount));
        Assert.Equal((1 + 9d / 11) / 3,
            metrics.AverageIntersectionOverUnion, 12);
        Assert.Equal(2d / 3, metrics.RecallAtFiftyPercent, 12);
        Assert.Equal([(0, 1), (1, 0)], metrics.Matches
            .Select(match => (match.ExpectedIndex, match.RecognizedIndex)));
    }

    [Fact]
    public void CompareHandlesEmptyAndInvalidBoxes()
    {
        Assert.Equal(1, PdfOcrWordBoxMetrics.Compare([], []).AverageIntersectionOverUnion);
        Assert.Equal(0, PdfOcrWordBoxMetrics.Compare([], [Word(0, 0, 1, 1)])
            .RecallAtFiftyPercent);
        Assert.Throws<ArgumentException>(() => PdfOcrWordBoxMetrics.Compare(
            [Word(0, 0, 0, 1)], []));
    }

    [Fact]
    public void CompareRejectsUnboundedPairWork()
    {
        PdfOcrPixelWord[] words = [.. Enumerable.Repeat(Word(0, 0, 1, 1), 1001)];

        Assert.Throws<ArgumentException>(() =>
            PdfOcrWordBoxMetrics.Compare(words, words));
    }

    private static PdfOcrPixelWord Word(int left, int top, int right, int bottom) =>
        new("word", 1, left, top, right, bottom);
}
