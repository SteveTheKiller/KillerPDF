using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrTextMetricsTests
{
    [Fact]
    public void CompareMeasuresCharacterAndWordEdits()
    {
        PdfOcrTextMetrics metrics = PdfOcrTextMetrics.Compare(
            "one two three", "one too four");

        Assert.Equal((13, 12, 6), (metrics.ExpectedCharacterCount,
            metrics.RecognizedCharacterCount, metrics.CharacterEditCount));
        Assert.Equal((3, 3, 2), (metrics.ExpectedWordCount,
            metrics.RecognizedWordCount, metrics.WordEditCount));
        Assert.Equal(6d / 13, metrics.CharacterErrorRate, 12);
        Assert.Equal(2d / 3, metrics.WordErrorRate, 12);
        Assert.Equal(7d / 13, metrics.CharacterAccuracy, 12);
        Assert.Equal(1d / 3, metrics.WordAccuracy, 12);
    }

    [Fact]
    public void CompareNormalizesWhitespaceAndCountsUnicodeScalars()
    {
        PdfOcrTextMetrics metrics = PdfOcrTextMetrics.Compare(
            "  OCR\r\n😀\tworks  ", "OCR 😀 works");

        Assert.Equal(11, metrics.ExpectedCharacterCount);
        Assert.Equal(0, metrics.CharacterEditCount);
        Assert.Equal(3, metrics.ExpectedWordCount);
        Assert.Equal(0, metrics.WordEditCount);
        Assert.Equal(0, metrics.CharacterErrorRate);
        Assert.Equal(0, metrics.WordErrorRate);
        Assert.Equal(1, metrics.CharacterAccuracy);
        Assert.Equal(1, metrics.WordAccuracy);
    }

    [Theory]
    [InlineData("", "", 0, 0)]
    [InlineData("", "text", 1, 1)]
    [InlineData("text", "", 1, 1)]
    public void CompareHandlesEmptyText(
        string expected, string recognized, double characterRate, double wordRate)
    {
        PdfOcrTextMetrics metrics = PdfOcrTextMetrics.Compare(expected, recognized);

        Assert.Equal(characterRate, metrics.CharacterErrorRate);
        Assert.Equal(wordRate, metrics.WordErrorRate);
    }

    [Fact]
    public void CompareRejectsUnboundedEditDistanceWork()
    {
        string text = new('a', 10_001);

        Assert.Throws<ArgumentException>(() => PdfOcrTextMetrics.Compare(text, text));
    }
}
