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
    public void CompareMatchedWordsSeparatesRecognitionFromReadingOrder()
    {
        PdfOcrPixelWord[] expected =
        [
            new("first", 1, 0, 0, 10, 10),
            new("second", 1, 0, 20, 10, 30)
        ];
        PdfOcrPixelWord[] recognized =
        [
            new("second", 1, 0, 20, 10, 30),
            new("first", 1, 0, 0, 10, 10)
        ];
        PdfOcrWordBoxMatch[] matches =
        [
            new(0, 1, 1),
            new(1, 0, 1)
        ];

        PdfOcrTextMetrics metrics = PdfOcrTextMetrics.CompareMatchedWords(
            expected, recognized, matches);

        Assert.Equal(1, metrics.CharacterAccuracy);
        Assert.Equal(1, metrics.WordAccuracy);
        Assert.Equal(0, metrics.CharacterEditCount);
        Assert.Equal(0, metrics.WordEditCount);
    }

    [Fact]
    public void CompareMatchedWordsCountsMismatchesMissingWordsAndExtras()
    {
        PdfOcrPixelWord[] expected =
        [
            new("one", 1, 0, 0, 10, 10),
            new("two", 1, 20, 0, 30, 10)
        ];
        PdfOcrPixelWord[] recognized =
        [
            new("owe", 1, 0, 0, 10, 10),
            new("extra", 1, 40, 0, 50, 10)
        ];

        PdfOcrTextMetrics metrics = PdfOcrTextMetrics.CompareMatchedWords(
            expected, recognized, [new(0, 0, 1)]);

        Assert.Equal((6, 8, 9), (metrics.ExpectedCharacterCount,
            metrics.RecognizedCharacterCount, metrics.CharacterEditCount));
        Assert.Equal((2, 2, 3), (metrics.ExpectedWordCount,
            metrics.RecognizedWordCount, metrics.WordEditCount));
    }

    [Fact]
    public void CompareMatchedWordsRejectsDuplicateIndexes()
    {
        PdfOcrPixelWord[] words = [new("one", 1, 0, 0, 10, 10)];

        Assert.Throws<ArgumentException>(() => PdfOcrTextMetrics.CompareMatchedWords(
            words, words, [new(0, 0, 1), new(0, 0, 1)]));
    }

    [Fact]
    public void CompareRejectsUnboundedEditDistanceWork()
    {
        string text = new('a', 10_001);

        Assert.Throws<ArgumentException>(() => PdfOcrTextMetrics.Compare(text, text));
    }
}
