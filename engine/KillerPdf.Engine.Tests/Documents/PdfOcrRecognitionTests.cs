using System.Security.Cryptography;
using System.Numerics;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Rendering;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrRecognitionTests
{
    [Fact]
    public void TrainerBuildsDeterministicSerializableClassifier()
    {
        PdfOcrTrainingSample[] samples =
        [
            new("A", new float[] { 1, 0 }),
            new("A", new float[] { 0.8f, 0 }),
            new("B", new float[] { 0, 1 }),
            new("B", new float[] { 0, 0.8f })
        ];

        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(2, 1, samples);
        PdfOcrRecognitionModel restored = PdfOcrRecognitionModel.Load(model.Save());

        Assert.Equal(["A", "B"], model.Labels);
        Assert.Equal(model.Labels, restored.Labels);
        Assert.Equal((2, 1), (restored.Width, restored.Height));
        Assert.Equal(model.Save(),
            PdfOcrModelTrainer.Train(2, 1, samples.Reverse()).Save());
    }

    [Fact]
    public void TrainerRejectsInvalidFeaturesAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { float.NaN })]));
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { 2 })]));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.Throws<OperationCanceledException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { 1 })], canceled.Token));
    }

    [Fact]
    public void TrainerEvaluatesAccuracyConfidenceAndConfusion()
    {
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(2, 1,
        [
            new("A", new float[] { 1, 0 }),
            new("B", new float[] { 0, 1 })
        ]);

        PdfOcrModelEvaluation evaluation = PdfOcrModelTrainer.Evaluate(model,
        [
            new("A", new float[] { 1, 0 }),
            new("B", new float[] { 0, 1 }),
            new("B", new float[] { 1, 0 })
        ]);

        Assert.Equal(3, evaluation.SampleCount);
        Assert.Equal(2, evaluation.CorrectCount);
        Assert.Equal(2d / 3, evaluation.Accuracy, 12);
        Assert.InRange(evaluation.AverageConfidence, 0.5, 1);
        Assert.Equal([
            new PdfOcrConfusion("A", "A", 1),
            new PdfOcrConfusion("B", "A", 1),
            new PdfOcrConfusion("B", "B", 1)
        ], evaluation.Confusion);
    }

    [Fact]
    public void ModelRoundTripsWithHashVerificationAndRecognizesGlyphs()
    {
        PdfOcrRecognitionModel created = RecognitionModel();
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

    [Fact]
    public void ModelClassificationHandlesVectorBlocksAndScalarTail()
    {
        int features = Math.Min(128, Vector<float>.Count + 3);
        float[] positive = Enumerable.Repeat(1f, features).ToArray();
        float[] negative = Enumerable.Repeat(-1f, features).ToArray();
        PdfOcrRecognitionModel model = PdfOcrRecognitionModel.Create(
            features, 1, ["positive", "negative"],
            positive.Concat(negative).ToArray(), new float[] { 0, 0 });
        string blank = new('.', features * 2);
        PdfOcrPreparedImage image = Prepared(features * 2, 4,
            [blank, new string('#', features) + new string('.', features), blank, blank]);

        PdfOcrRecognizedWord word = Assert.Single(PdfOcrRecognizer.Recognize(
            image, PdfOcrLayoutAnalyzer.Analyze(image), model));

        Assert.Equal("positive", word.Text);
        Assert.Equal(1 / (1 + Math.Exp(-2 * features)), word.Confidence, 12);
    }

    [Fact]
    public void ModelCatalogSelectsRequestedExactAndPrimaryLanguages()
    {
        PdfOcrRecognitionModel english = TinyModel("E");
        PdfOcrRecognitionModel french = TinyModel("F");
        var catalog = new PdfOcrRecognitionModelCatalog([
            new("en", english),
            new("fr-FR", french)
        ]);

        PdfOcrRecognitionModelSelection exact = catalog.Select(["FR_fr", "en-US"]);
        PdfOcrRecognitionModelSelection fallback = catalog.Select(["de-DE", "en-US"]);

        Assert.Equal(("fr-fr", french), (exact.Language, exact.Model));
        Assert.Equal(("en", english), (fallback.Language, fallback.Model));
        Assert.Throws<NotSupportedException>(() => catalog.Select(["de-DE"]));
    }

    [Fact]
    public void ModelCatalogLazilyLoadsOnlyTheSelectedVerifiedLanguage()
    {
        byte[] english = TinyModel("E").Save();
        byte[] french = TinyModel("F").Save();
        int englishReads = 0, frenchReads = 0;
        var catalog = PdfOcrRecognitionModelCatalog.Create([
            new("en", () => { englishReads++; return english; },
                Convert.ToHexString(SHA256.HashData(english))),
            new("fr", () => { frenchReads++; return french; },
                Convert.ToHexString(SHA256.HashData(french)))
        ]);

        PdfOcrRecognitionModelSelection first = catalog.Select(["en-US"]);
        PdfOcrRecognitionModelSelection second = catalog.Select(["en"]);

        Assert.Same(first.Model, second.Model);
        Assert.Equal((1, 0), (englishReads, frenchReads));
        Assert.Equal(["en", "fr"], catalog.Languages);
    }

    [Fact]
    public void GlyphNormalizationPreservesAspectRatioAndCentersInk()
    {
        PdfOcrPreparedImage image = Prepared(4, 1, ["####"]);

        float[] features = PdfOcrRecognizer.NormalizeGlyph(
            image, new PdfOcrImageRegion(0, 0, 4, 1), 4, 4);

        Assert.Equal([
            0, 0, 0, 0,
            1, 1, 1, 1,
            0, 0, 0, 0,
            0, 0, 0, 0
        ], features);
    }

    [Fact]
    public void PageRecognizerRunsDirectlyFromEngineRenderAndMapsPdfBounds()
    {
        PdfOcrRecognitionModel model = RecognitionModel();
        PdfImage image = RasterImage(
        [
            "........",
            "#...#...",
            "#...#...",
            "#...##..",
            "#...##..",
            "........"
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(8, 6, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 8, 6)).Build());
        var recognizer = new PdfOcrPageRecognizer(document, model);

        PdfOcrPageRecognition result = recognizer.Recognize(0,
            new PdfRenderOptions(8, 6, includeAnnotations: false, includeFormFields: false),
            new PdfOcrOptions(["eng"], deskew: false, correctOrientation: false,
                detectPageSegments: false));

        PdfOcrWord word = Assert.Single(result.Review.Words);
        Assert.Equal("IL", word.Text);
        Assert.Equal("eng", word.Language);
        Assert.Equal(new PdfContentBounds(0, 1, 6, 5), word.BoundingBox);
        Assert.Empty(result.Diagnostics);
        Assert.Equal((8, 6), (result.PixelWidth, result.PixelHeight));
    }

    [Fact]
    public void PageRecognizerUsesTheFirstAvailableRequestedLanguageModel()
    {
        PdfImage image = RasterImage([
            "....", ".##.", ".##.", "...."
        ]);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(4, 4, new PdfContentStreamBuilder().DrawImage(image, 0, 0, 4, 4)).Build());
        var catalog = new PdfOcrRecognitionModelCatalog([
            new("en", TinyModel("E"))
        ]);
        var recognizer = new PdfOcrPageRecognizer(document, catalog);

        PdfOcrPageRecognition result = recognizer.Recognize(0,
            new PdfRenderOptions(4, 4, includeAnnotations: false, includeFormFields: false),
            new PdfOcrOptions(["de-DE", "en-US"], deskew: false,
                correctOrientation: false, removeBackground: false, removeNoise: false,
                detectPageSegments: false));

        PdfOcrWord word = Assert.Single(result.Review.Words);
        Assert.Equal("E", word.Text);
        Assert.Equal("en", word.Language);
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
                removeBackground: false, removeNoise: false, detectPageSegments: false));
    }

    private static PdfOcrRecognitionModel RecognitionModel()
    {
        var iWeights = new float[16];
        var lWeights = new float[16];
        foreach (int index in new[] { 10, 14 })
        {
            iWeights[index] = -2;
            lWeights[index] = 2;
        }
        return PdfOcrRecognitionModel.Create(4, 4, ["I", "L"],
            iWeights.Concat(lWeights).ToArray(), new float[] { 0, 0 });
    }

    private static PdfOcrRecognitionModel TinyModel(string label) =>
        PdfOcrRecognitionModel.Create(1, 1, [label], new float[] { 1 }, new float[] { 0 });

    private static PdfImage RasterImage(string[] rows)
    {
        int width = rows[0].Length;
        byte[] rgb = new byte[width * rows.Length * 3];
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < width; x++)
            {
                byte value = rows[y][x] == '#' ? (byte)0 : (byte)255;
                int offset = (y * width + x) * 3;
                rgb[offset] = rgb[offset + 1] = rgb[offset + 2] = value;
            }
        return PdfImage.FromRgb(width, rows.Length, rgb);
    }
}
