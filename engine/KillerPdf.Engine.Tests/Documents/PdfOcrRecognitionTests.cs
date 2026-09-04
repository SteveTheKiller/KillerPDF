using System.Security.Cryptography;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrRecognitionTests
{
    [Fact]
    public void ModelRoundTripsWithHashVerificationAndRecognizesGlyphs()
    {
        PdfOcrRecognitionModel created = PdfOcrRecognitionModel.Create(2, 2, ["I", "L"],
            new float[] { 0, 2, 0, -1, 0, -2, 0, 1 }, new float[] { 0, 0 });
        byte[] bytes = created.Save();
        string hash = Convert.ToHexString(SHA256.HashData(bytes));
        PdfOcrRecognitionModel model = PdfOcrRecognitionModel.Load(bytes, hash);
        PdfOcrPreparedImage image = Prepared(8, 6,
        [
            "........",
            "#...#...",
            "#...#...",
            "#...##..",
            "#...##..",
            "........"
        ]);
        PdfOcrPageLayout layout = PdfOcrLayoutAnalyzer.Analyze(image);

        IReadOnlyList<PdfOcrRecognizedWord> words = PdfOcrRecognizer.Recognize(image, layout, model);

        Assert.Equal(["IL"], words.Select(word => word.Text));
        Assert.All(words, word => Assert.InRange(word.Confidence, 0.5, 1));
        Assert.Throws<CryptographicException>(() => PdfOcrRecognitionModel.Load(bytes, new string('0', 64)));
    }

    [Fact]
    public void ModelRejectsTruncatedAndNonFinitePayloads()
    {
        Assert.Throws<FormatException>(() => PdfOcrRecognitionModel.Load("bad"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => PdfOcrRecognitionModel.Create(
            1, 1, ["x"], new float[] { float.NaN }, new float[] { 0 }));
    }

    private static PdfOcrPreparedImage Prepared(int width, int height, string[] rows)
    {
        byte[] bgra = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = rows[y][x] == '#' ? (byte)0 : (byte)255;
                int offset = (y * width + x) * 4;
                bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = value;
                bgra[offset + 3] = 255;
            }
        return PdfOcrImagePreprocessor.PrepareBgra(bgra, width, height,
            new PdfOcrOptions(["eng"], deskew: false, correctOrientation: false,
                detectPageSegments: false));
    }
}
