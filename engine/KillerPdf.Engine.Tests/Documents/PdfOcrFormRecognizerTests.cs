using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrFormRecognizerTests
{
    [Fact]
    public void FormRecognitionReplacesPageWordsAndNormalizesAChoice()
    {
        byte[] bgra = WhiteBgra(20, 10);
        int calls = 0;
        PdfOcrResult Recognize(ReadOnlyMemory<byte> pixels, int width, int height,
            string? whitelist, CancellationToken cancellationToken)
        {
            calls++;
            return calls == 1
                ? new PdfOcrResult("outside inside", 0.8f,
                [
                    new PdfOcrPixelWord("outside", 0.8f, 1, 1, 4, 3),
                    new PdfOcrPixelWord("inside", 0.8f, 6, 2, 12, 5)
                ])
                : new PdfOcrResult("Scienoe", 0.9f,
                    [new PdfOcrPixelWord("Scienoe", 0.9f, 0, 0, width, height)]);
        }
        var region = new PdfOcrFormRegion(
            5, 1, 15, 7, null, 20, ["Science", "History"], false);

        PdfOcrResult result = PdfOcrFormRecognizer.Recognize(
            Recognize, bgra, 20, 10, [region]);

        Assert.Equal(2, calls);
        Assert.Equal("outside Science", result.Text);
        Assert.Equal(["outside", "Science"], result.Words.Select(word => word.Text));
        Assert.Equal((5, 1, 15, 7), (result.Words[1].Left, result.Words[1].Top,
            result.Words[1].Right, result.Words[1].Bottom));
    }

    [Fact]
    public void CombRecognitionUsesOneConstrainedCropPerCell()
    {
        byte[] bgra = WhiteBgra(12, 4);
        int calls = 0;
        var widths = new List<int>();
        PdfOcrResult Recognize(ReadOnlyMemory<byte> pixels, int width, int height,
            string? whitelist, CancellationToken cancellationToken)
        {
            if (calls++ == 0) return new PdfOcrResult("", 0, []);
            widths.Add(width);
            Assert.Equal("ABC", whitelist);
            string value = ((char)('A' + widths.Count - 1)).ToString();
            return new PdfOcrResult(value, 0.75f,
                [new PdfOcrPixelWord(value, 0.75f, 0, 0, width, height)]);
        }
        var region = new PdfOcrFormRegion(
            0, 0, 12, 4, "ABC", 3, [], true);

        PdfOcrResult result = PdfOcrFormRecognizer.Recognize(
            Recognize, bgra, 12, 4, [region]);

        Assert.Equal([4, 4, 4], widths);
        Assert.Equal("ABC", result.Text);
        Assert.Equal(0.75f, result.MeanConfidence);
    }

    private static byte[] WhiteBgra(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height * 4).ToArray();
        return pixels;
    }
}
