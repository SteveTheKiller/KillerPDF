using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrResultTests
{
    [Fact]
    public void FromWords_OrdersReadingRowsAndComputesConfidence()
    {
        PdfOcrResult result = PdfOcrResult.FromWords(
        [
            new PdfOcrPixelWord("third", 0.9f, 2, 30, 8, 36),
            new PdfOcrPixelWord("second", 0.6f, 20, 10, 28, 16),
            new PdfOcrPixelWord("first", 0.3f, 2, 12, 8, 18)
        ]);

        Assert.Equal(["first", "second", "third"],
            result.Words.Select(word => word.Text));
        Assert.Equal(string.Join(Environment.NewLine, "first second", "third"),
            result.Text);
        Assert.Equal(0.6f, result.MeanConfidence, 5);
    }

    [Fact]
    public void FromWords_UsesExactRowsWhenToleranceIsZero()
    {
        PdfOcrResult result = PdfOcrResult.FromWords(
        [
            new PdfOcrPixelWord("second", 1, 10, 4, 15, 8),
            new PdfOcrPixelWord("first", 1, 1, 4, 6, 8),
            new PdfOcrPixelWord("third", 1, 1, 5, 6, 9)
        ], lineTolerance: 0);

        Assert.Equal(string.Join(Environment.NewLine, "first second", "third"),
            result.Text);
    }

    [Fact]
    public void Constructor_CopiesRecognizerWordCollection()
    {
        var words = new List<PdfOcrPixelWord>
        {
            new("word", 0.5f, 1, 2, 3, 4)
        };

        var result = new PdfOcrResult("word", 0.5f, words);
        words.Clear();

        Assert.Single(result.Words);
    }
}
