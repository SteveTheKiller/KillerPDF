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

    [Fact]
    public void WidgetRecognitionMapsGeometryBeforeRecognizingFields()
    {
        byte[] bgra = WhiteBgra(200, 100);
        var widget = new PdfFormWidgetInfo
        {
            PageIndex = 0,
            AnnotationIndex = 0,
            ObjectNumber = 1,
            Generation = 0,
            FieldName = "totalAmount",
            FieldKind = PdfFormFieldKind.Text,
            Flags = 0,
            Value = "",
            DefaultAppearance = "",
            MaximumLength = 8,
            OnValue = "",
            HasAction = false,
            HasAppearanceState = false,
            Options = [],
            PageBoxLeft = 0,
            PageBoxBottom = 0,
            PageBoxWidth = 100,
            PageBoxHeight = 100,
            PageRotation = 0,
            Left = 10,
            Bottom = 20,
            Right = 60,
            Top = 40
        };
        int calls = 0;
        PdfOcrResult Recognize(ReadOnlyMemory<byte> pixels, int width, int height,
            string? whitelist, CancellationToken cancellationToken)
        {
            calls++;
            if (calls == 1) return new PdfOcrResult("", 0, []);
            Assert.Equal((100, 20), (width, height));
            Assert.Equal(PdfOcrFormLayout.NumericWhitelist, whitelist);
            return new PdfOcrResult("42", 1,
                [new PdfOcrPixelWord("42", 1, 0, 0, width, height)]);
        }

        PdfOcrResult result = PdfOcrFormRecognizer.Recognize(
            Recognize, bgra, 200, 100, [widget]);

        PdfOcrPixelWord word = Assert.Single(result.Words);
        Assert.Equal("42", word.Text);
        Assert.Equal((20, 60, 120, 80),
            (word.Left, word.Top, word.Right, word.Bottom));
    }

    [Theory]
    [InlineData(-1, 0, 10, 4, 1, false)]
    [InlineData(0, 0, 13, 4, 1, false)]
    [InlineData(0, 0, 10, 5, 1, false)]
    [InlineData(0, 0, 10, 4, -1, false)]
    [InlineData(0, 0, 10, 4, 0, true)]
    [InlineData(0, 0, 10, 4, 11, true)]
    public void FormRecognitionRejectsInvalidRegionsBeforeCallingTheBackend(
        int left, int top, int right, int bottom, int maximumLength, bool isComb)
    {
        int calls = 0;
        PdfOcrResult Recognize(ReadOnlyMemory<byte> pixels, int width, int height,
            string? whitelist, CancellationToken cancellationToken)
        {
            calls++;
            return new PdfOcrResult("", 0, []);
        }
        var region = new PdfOcrFormRegion(
            left, top, right, bottom, null, maximumLength, [], isComb);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcrFormRecognizer.Recognize(
                Recognize, WhiteBgra(12, 4), 12, 4, [region]));
        Assert.Equal(0, calls);
    }

    private static byte[] WhiteBgra(int width, int height)
    {
        byte[] pixels = Enumerable.Repeat(byte.MaxValue, width * height * 4).ToArray();
        return pixels;
    }
}
