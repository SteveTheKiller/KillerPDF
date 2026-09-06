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
    public void TrainingPartitionHoldsOutWholeDocumentsDeterministically()
    {
        bool selected = PdfOcrTrainingPartition.IsHoldout("Folder\\Document.pdf", 10);

        Assert.Equal(selected,
            PdfOcrTrainingPartition.IsHoldout("folder/document.PDF", 10));
        int heldOut = Enumerable.Range(0, 1000).Count(index =>
            PdfOcrTrainingPartition.IsHoldout($"document-{index}.pdf", 10));
        Assert.InRange(heldOut, 75, 125);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfOcrTrainingPartition.IsHoldout("document.pdf", 0));
    }

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
    public void TrainerKeepsDistinctShapePrototypesForOneLabel()
    {
        float[] narrow =
        [
            0, 1, 0,
            0, 1, 0,
            0, 1, 0
        ];
        float[] wide =
        [
            0, 0, 0,
            1, 1, 1,
            0, 0, 0
        ];
        float[] diagonal =
        [
            1, 0, 0,
            0, 1, 0,
            0, 0, 1
        ];
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(3, 3,
        [
            new("A", narrow),
            new("A", wide),
            new("B", diagonal)
        ]);

        PdfOcrModelEvaluation evaluation = PdfOcrModelTrainer.Evaluate(model,
        [
            new("A", narrow),
            new("A", wide),
            new("B", diagonal)
        ]);

        Assert.Equal(["A", "B"], model.Labels);
        Assert.Equal(1, evaluation.Accuracy);
        Assert.Equal(model.Save(), PdfOcrRecognitionModel.Load(model.Save()).Save());
    }

    [Fact]
    public void ClassifierComparesGlyphsWithMatchingPrototypeShapes()
    {
        float[] narrow =
        [
            0, 1, 0,
            0, 1, 0,
            0, 1, 0
        ];
        float[] wide =
        [
            1, 1, 1,
            1, 1, 1,
            1, 1, 1
        ];
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(3, 3,
        [
            new("narrow", narrow),
            .. Enumerable.Repeat(new PdfOcrTrainingSample("wide", wide), 100)
        ]);

        PdfOcrModelEvaluation evaluation = PdfOcrModelTrainer.Evaluate(model,
            [new("narrow", narrow)]);

        Assert.Equal(1, evaluation.Accuracy);
        Assert.Equal(model.Save(), PdfOcrRecognitionModel.Load(model.Save()).Save());
    }

    [Fact]
    public void TrainerKeepsRepresentativeVariantsWithinOneShapeBucket()
    {
        float[] left =
        [
            1, 0, 0,
            1, 0, 0,
            1, 0, 0
        ];
        float[] right =
        [
            0, 0, 1,
            0, 0, 1,
            0, 0, 1
        ];

        PdfOcrRecognitionModel one = PdfOcrModelTrainer.Train(3, 3,
            [new("A", left)]);
        PdfOcrRecognitionModel variants = PdfOcrModelTrainer.Train(3, 3,
            [new("A", left), new("A", right)]);

        Assert.True(variants.Save().Length > one.Save().Length);
        Assert.Equal(variants.Save(), PdfOcrModelTrainer.Train(3, 3,
            [new("A", right), new("A", left)]).Save());
    }

    [Fact]
    public void TrainerUsesLabelFrequencyToResolveIdenticalShapes()
    {
        PdfOcrTrainingSample[] samples =
        [
            new("Arare", new float[] { 1 }),
            .. Enumerable.Repeat(new PdfOcrTrainingSample(
                "Zcommon", new float[] { 1 }), 9)
        ];

        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(1, 1, samples);
        PdfOcrModelEvaluation evaluation = PdfOcrModelTrainer.Evaluate(model,
            [new("Zcommon", new float[] { 1 })]);

        Assert.Equal(1, evaluation.Accuracy);
        Assert.Equal(model.Save(), PdfOcrModelTrainer.Train(
            1, 1, samples.Reverse()).Save());
    }

    [Fact]
    public void TrainerRejectsInvalidFeaturesAndHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { float.NaN })]));
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { 2 })]));
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("\uFFFD", new float[] { 1 })]));
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A\n", new float[] { 1 })]));
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A B", new float[] { 1 })]));
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
        Assert.InRange(evaluation.AverageConfidence, 0.78, 0.83);
        Assert.Equal([
            new PdfOcrConfusion("A", "A", 1),
            new PdfOcrConfusion("B", "A", 1),
            new PdfOcrConfusion("B", "B", 1)
        ], evaluation.Confusion);
    }

    [Fact]
    public void TrainerLearnsLabeledGlyphsFromPreparedPagePixels()
    {
        PdfOcrPreparedImage image = Prepared(8, 6,
        [
            "........",
            "#...#...",
            "#...#...",
            "#...##..",
            "#...##..",
            "........"
        ]);
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(4, 4, image,
        [
            new("I", new PdfOcrImageRegion(0, 1, 1, 5)),
            new("L", new PdfOcrImageRegion(4, 1, 6, 5))
        ]);

        PdfOcrRecognizedWord word = Assert.Single(PdfOcrRecognizer.Recognize(
            image, PdfOcrLayoutAnalyzer.Analyze(image), model));

        Assert.Equal("IL", word.Text);
        Assert.Throws<ArgumentException>(() => PdfOcrModelTrainer.Train(4, 4, image,
            [new PdfOcrLabeledGlyph("I", new PdfOcrImageRegion(-1, 0, 1, 1))]));
    }

    [Theory]
    [InlineData(0, 200, 160)]
    [InlineData(90, 160, 200)]
    public void TrainerCreatesSamplesFromExistingPdfTextLayers(
        int rotation, int pixelWidth, int pixelHeight)
    {
        var builder = new PdfDocumentBuilder()
            .AddPage(100, 80, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 40)
                .SetTextMatrix(1, 0, 0, 1, 10, 20)
                .ShowLatin1Text("AB").EndText())
            .SetPageRotation(0, rotation);
        PdfDocument document = PdfDocument.Open(builder.Build());
        var options = new PdfOcrOptions(["en"], deskew: false,
            correctOrientation: false, removeBackground: false, removeNoise: false,
            detectPageSegments: false);

        IReadOnlyList<PdfOcrTrainingSample> samples =
            PdfOcrModelTrainer.CreatePageSamples(document, 0,
                new PdfRenderOptions(pixelWidth, pixelHeight, includeAnnotations: false,
                    includeFormFields: false), options, 16, 16);
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(16, 16, samples);
        PdfOcrModelEvaluation evaluation = PdfOcrModelTrainer.Evaluate(model, samples);

        Assert.Equal(["A", "B"], samples.Select(sample => sample.Label));
        Assert.All(samples, sample => Assert.Contains(
            sample.Features.ToArray(), value => value > 0));
        Assert.Equal(1, evaluation.Accuracy);
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
    public void CompatibleLanguageModelsCombineWithoutLosingPrototypes()
    {
        PdfOcrRecognitionModel english = PdfOcrRecognitionModel.Create(
            2, 1, ["A"], new float[] { 1, 0 }, new float[] { 0 });
        PdfOcrRecognitionModel spanish = PdfOcrRecognitionModel.Create(
            2, 1, ["N", "Ñ"], new float[] { 0, 1, -1, 0 }, new float[] { 0, 0 });

        PdfOcrRecognitionModel combined = PdfOcrRecognitionModel.Combine(
            [english, spanish]);

        Assert.Equal(["A", "N", "Ñ"], combined.Labels);
        Assert.Equal((english.Width, english.Height), (combined.Width, combined.Height));
        Assert.Equal(combined.Save(), PdfOcrRecognitionModel.Load(combined.Save()).Save());
        Assert.Throws<ArgumentException>(() => PdfOcrRecognitionModel.Combine(
            [english, TinyModel("X")]));
        Assert.Throws<ArgumentException>(() => PdfOcrRecognitionModel.Combine([]));
    }

    [Fact]
    public void RawBgraRecognitionRunsTheCompleteEnginePipeline()
    {
        string[] rows =
        [
            "........",
            "#...#...",
            "#...#...",
            "#...##..",
            "#...##..",
            "........"
        ];
        byte[] bgra = new byte[8 * 6 * 4];
        for (int y = 0; y < rows.Length; y++)
            for (int x = 0; x < rows[y].Length; x++)
            {
                byte value = rows[y][x] == '#' ? (byte)0 : (byte)255;
                int offset = (y * 8 + x) * 4;
                bgra[offset] = bgra[offset + 1] = bgra[offset + 2] = value;
                bgra[offset + 3] = 255;
            }

        PdfOcrResult result = PdfOcrRecognizer.RecognizeBgra(bgra, 8, 6,
            RecognitionModel(), new PdfOcrOptions(["eng"], deskew: false,
                correctOrientation: false, removeBackground: false,
                removeNoise: false, detectPageSegments: false));

        PdfOcrPixelWord word = Assert.Single(result.Words);
        Assert.Equal("IL", result.Text);
        Assert.Equal("IL", word.Text);
        Assert.Equal((0, 1, 6, 5), (word.Left, word.Top, word.Right, word.Bottom));
        Assert.InRange(result.MeanConfidence, 0.5f, 1f);
    }

    [Fact]
    public void RawBgraRecognitionRestrictsResultsToACharacterWhitelist()
    {
        PdfOcrPreparedImage image = Prepared(4, 4,
        [
            "....",
            ".##.",
            ".##.",
            "...."
        ]);
        PdfOcrRecognitionModel model = PdfOcrRecognitionModel.Create(
            1, 1, ["A", "7"], new float[] { 1, -1 }, new float[] { 0, 0 });
        byte[] bgra = image.Pixels.ToArray().SelectMany(value =>
            new byte[] { value, value, value, 255 }).ToArray();
        var options = new PdfOcrOptions(["eng"], deskew: false,
            correctOrientation: false, removeBackground: false, removeNoise: false,
            detectPageSegments: false);

        PdfOcrResult result = PdfOcrRecognizer.RecognizeBgra(
            bgra, 4, 4, model, options, "0123456789");

        Assert.Equal("7", Assert.Single(result.Words).Text);
        Assert.Throws<ArgumentException>(() => PdfOcrRecognizer.RecognizeBgra(
            bgra, 4, 4, model, options, "xyz"));
    }

    [Fact]
    public void ModelRejectsTruncatedAndNonFinitePayloads()
    {
        Assert.Throws<FormatException>(() => PdfOcrRecognitionModel.Load("bad"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => PdfOcrRecognitionModel.Create(
            1, 1, ["x"], new float[] { float.NaN }, new float[] { 0 }));
        Assert.Throws<ArgumentException>(() => PdfOcrRecognitionModel.Create(
            1, 1, ["\uFFFD"], new float[] { 1 }, new float[] { 0 }));
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

    [Fact]
    public void PageRecognizerSelectsAndMapsRightAngleOrientation()
    {
        string[] upright =
        [
            "........",
            "..#.....",
            "..#.....",
            "..#.....",
            "..####..",
            "........"
        ];
        PdfOcrRecognitionModel model = OrientationModel(upright);
        string[] rotated = RotateRowsClockwise(upright);
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(rotated[0].Length, rotated.Length, new PdfContentStreamBuilder()
                .DrawImage(RasterImage(rotated), 0, 0, rotated[0].Length, rotated.Length))
            .Build());
        var recognizer = new PdfOcrPageRecognizer(document, model);

        PdfOcrPageRecognition result = recognizer.Recognize(0,
            new PdfRenderOptions(rotated[0].Length, rotated.Length,
                includeAnnotations: false, includeFormFields: false),
            new PdfOcrOptions(["eng"], deskew: false, correctOrientation: true,
                removeBackground: false, removeNoise: false, detectPageSegments: false));

        PdfOcrWord word = Assert.Single(result.Review.Words);
        Assert.Equal("L", word.Text);
        Assert.Equal(new PdfContentBounds(1, 2, 5, 6), word.BoundingBox);
        Assert.Empty(result.Diagnostics);
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

    private static PdfOcrRecognitionModel OrientationModel(string[] uprightRows)
    {
        const int size = 6;
        PdfOcrPreparedImage upright = Prepared(
            uprightRows[0].Length, uprightRows.Length, uprightRows);
        PdfOcrImageRegion bounds = Assert.Single(
            PdfOcrLayoutAnalyzer.Analyze(upright).Components);
        float[] features = PdfOcrRecognizer.NormalizeGlyph(upright, bounds, size, size);
        float[][] wrongFeatures = Enumerable.Range(1, 3).Select(turns =>
        {
            string[] rotated = uprightRows;
            for (int turn = 0; turn < turns; turn++) rotated = RotateRowsClockwise(rotated);
            PdfOcrPreparedImage prepared = Prepared(rotated[0].Length, rotated.Length, rotated);
            PdfOcrImageRegion rotatedBounds = Assert.Single(
                PdfOcrLayoutAnalyzer.Analyze(prepared).Components);
            return PdfOcrRecognizer.NormalizeGlyph(prepared, rotatedBounds, size, size);
        }).ToArray();
        var basis = new List<float[]>();
        foreach (float[] wrong in wrongFeatures)
        {
            float[] vector = [.. wrong];
            foreach (float[] existing in basis)
            {
                float projection = Dot(vector, existing);
                for (int index = 0; index < vector.Length; index++)
                    vector[index] -= projection * existing[index];
            }
            float length = MathF.Sqrt(Dot(vector, vector));
            if (length > 0.0001f)
            {
                for (int index = 0; index < vector.Length; index++) vector[index] /= length;
                basis.Add(vector);
            }
        }
        float[] weights = [.. features];
        foreach (float[] vector in basis)
        {
            float projection = Dot(weights, vector);
            for (int index = 0; index < weights.Length; index++)
                weights[index] -= projection * vector[index];
        }
        float scale = 8 / Dot(weights, features);
        for (int index = 0; index < weights.Length; index++) weights[index] *= scale;
        return PdfOcrRecognitionModel.Create(size, size, ["L", "X"],
            weights.Concat(new float[weights.Length]).ToArray(), new float[] { 0, 0 });

        static float Dot(float[] left, float[] right) =>
            left.Zip(right, (a, b) => a * b).Sum();
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

    private static string[] RotateRowsClockwise(string[] rows)
    {
        int width = rows[0].Length;
        var rotated = new string[width];
        for (int x = 0; x < width; x++)
        {
            var row = new char[rows.Length];
            for (int y = 0; y < rows.Length; y++) row[y] = rows[rows.Length - 1 - y][x];
            rotated[x] = new string(row);
        }
        return rotated;
    }
}
