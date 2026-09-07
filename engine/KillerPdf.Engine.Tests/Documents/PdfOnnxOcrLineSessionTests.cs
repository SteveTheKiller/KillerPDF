using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOnnxOcrLineSessionTests
{
    private static readonly string[] Vocabulary = ["<blank>", "H", "I", "O", "K", " "];

    [Fact]
    public void DescriptionRejectsNormalizationThatDoesNotMatchChannels()
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => CreateDescription(
            channelOrder: PdfOnnxOcrChannelOrder.Rgb, mean: [0.5f], scale: [0.5f]));

        Assert.Contains("3 mean and scale", error.Message, StringComparison.Ordinal);
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDescription(blankIndex: 6));
        Assert.Throws<ArgumentException>(() => CreateDescription(inputWidth: 900, maximumInputWidth: 800));
    }

    [Fact]
    public void DescriptionRoundTripsThroughJsonAndRejectsUnknownContracts()
    {
        const string json = """
            {
              "format": "killerpdf-onnx-ocr-ctc-line/1",
              "displayName": "Test CTC",
              "version": "2.1",
              "languages": ["eng", "eng"],
              "inputName": "x",
              "inputLayout": "nchw",
              "channelOrder": "gray",
              "inputHeight": 32,
              "inputWidth": 0,
              "maximumInputWidth": 1024,
              "widthMultiple": 32,
              "mean": [0.5],
              "scale": [0.5],
              "outputName": "y",
              "outputLayout": "batchTimeClass",
              "outputIsProbability": false,
              "vocabulary": ["<blank>", "H", "I"],
              "blankIndex": 0
            }
            """;

        PdfOnnxOcrModelDescription description = PdfOnnxOcrModelDescription.FromJson(json);

        Assert.Equal("Test CTC", description.DisplayName);
        Assert.Equal(new Version(2, 1), description.Version);
        Assert.Equal(["eng"], description.Languages);
        Assert.Equal(1, description.Channels);
        Assert.Equal(64, description.ResolveInputWidth(33));
        Assert.Equal(32, description.ResolveInputWidth(1));
        Assert.Equal(1024, description.ResolveInputWidth(5000));
        Assert.Throws<FormatException>(() => PdfOnnxOcrModelDescription.FromJson(
            json.Replace("ctc-line/1", "attention/1", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => PdfOnnxOcrModelDescription.FromJson(
            json.Replace("\"blankIndex\": 0", "\"blankIndex\": 9", StringComparison.Ordinal)));
        Assert.Throws<FormatException>(() => PdfOnnxOcrModelDescription.FromJson("{ not json"));
    }

    [Fact]
    public void SessionRejectsModelsWithoutTheDescribedTensorsAndDisposesTheRunner()
    {
        var runner = new FakeRunner(["image"], ["logits"], _ => throw new InvalidOperationException());

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new PdfOnnxOcrLineSession(CreateDescription(inputName: "pixels"), runner));

        Assert.Contains("no input named 'pixels'", error.Message, StringComparison.Ordinal);
        Assert.True(runner.WasDisposed);
    }

    [Fact]
    public void SessionDecodesCtcOutputIntoWordsWithPixelBounds()
    {
        int[] steps = [1, 1, 0, 2, 5, 3, 3, 0, 4];
        var runner = new FakeRunner(["image"], ["logits"], input => Logits(steps, input));
        PdfOnnxOcrModelDescription description = CreateDescription();
        (byte[] pixels, int width, int height) = CreateLineImage();
        var options = new PdfOcrOptions(["eng"], deskew: false, correctOrientation: false,
            detectPageSegments: false);

        PdfOcrResult result;
        using (var session = new PdfOnnxOcrLineSession(description, runner))
            result = session.RecognizeBgra(pixels, width, height, width * 4, options);

        Assert.Equal("HI OK", result.Text);
        Assert.Equal(2, result.Words.Count);
        Assert.Equal("HI", result.Words[0].Text);
        Assert.Equal("OK", result.Words[1].Text);
        Assert.All(result.Words, word =>
        {
            Assert.InRange(word.Left, 0, width - 1);
            Assert.InRange(word.Right, word.Left + 1, width);
            Assert.InRange(word.Top, 0, height - 1);
            Assert.InRange(word.Bottom, word.Top + 1, height);
            Assert.InRange(word.Confidence, 0.5f, 1f);
        });
        Assert.True(result.Words[0].Right <= result.Words[1].Left);
        Assert.InRange(result.MeanConfidence, 0.5f, 1f);
        Assert.True(runner.WasDisposed);
        PdfOnnxOcrTensor input = Assert.Single(runner.Inputs);
        Assert.Equal(4, input.Shape.Length);
        Assert.Equal(1, input.Shape[0]);
        Assert.Equal(1, input.Shape[1]);
        Assert.Equal(32, input.Shape[2]);
        Assert.Equal(0, input.Shape[3] % 32);
        Assert.Equal(1f, input.Data[^1]);
        Assert.Contains(input.Data, value => value < 0);
    }

    [Fact]
    public void SessionHonorsCharacterWhitelistDuringDecoding()
    {
        int[] steps = [1, 1, 0, 2, 5, 3, 3, 0, 4];
        var runner = new FakeRunner(["image"], ["logits"], input => Logits(steps, input));
        (byte[] pixels, int width, int height) = CreateLineImage();
        var options = new PdfOcrOptions(["eng"], deskew: false, correctOrientation: false,
            detectPageSegments: false);

        using var session = new PdfOnnxOcrLineSession(CreateDescription(), runner);
        PdfOcrResult result = session.RecognizeBgra(pixels, width, height, width * 4, options, "OK");

        Assert.Equal("OK", result.Text);
        Assert.Throws<ArgumentException>(() => session.RecognizeBgra(
            pixels, width, height, width * 4, options, "Z"));
    }

    [Fact]
    public void SessionRejectsVocabularyThatDoesNotMatchTheModelOutput()
    {
        var runner = new FakeRunner(["image"], ["logits"],
            _ => new PdfOnnxOcrTensor(new float[3 * 2], [1, 3, 2]));
        (byte[] pixels, int width, int height) = CreateLineImage();

        using var session = new PdfOnnxOcrLineSession(CreateDescription(), runner);
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            session.RecognizeBgra(pixels, width, height, width * 4, new PdfOcrOptions(["eng"],
                deskew: false, correctOrientation: false, detectPageSegments: false)));

        Assert.Contains("emits 2 classes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeReportsMissingAndInvalidModelFilesWithoutLoadingThem()
    {
        string missing = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".onnx");
        string invalid = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".onnx");
        File.WriteAllBytes(invalid, [1, 2, 3, 4, 5, 6, 7, 8]);
        try
        {
            Assert.Throws<FileNotFoundException>(() => PdfOnnxOcrRuntime.LoadRunner(missing));
            Assert.Throws<InvalidDataException>(() => PdfOnnxOcrRuntime.LoadRunner(invalid));
            Assert.Throws<ArgumentOutOfRangeException>(() => new PdfOnnxOcrRuntimeOptions(0));
        }
        finally
        {
            File.Delete(invalid);
        }
    }

    private static PdfOnnxOcrModelDescription CreateDescription(
        string inputName = "image", PdfOnnxOcrChannelOrder channelOrder = PdfOnnxOcrChannelOrder.Gray,
        int inputWidth = 0, int maximumInputWidth = 1024, float[]? mean = null,
        float[]? scale = null, int blankIndex = 0) =>
        new("Fake CTC", new Version(1, 0), ["eng"], inputName, PdfOnnxOcrInputLayout.Nchw,
            channelOrder, 32, inputWidth, maximumInputWidth, 32, mean ?? [0.5f],
            scale ?? [0.5f], "logits", PdfOnnxOcrOutputLayout.BatchTimeClass, false,
            Vocabulary, blankIndex);

    private static (byte[] Pixels, int Width, int Height) CreateLineImage()
    {
        const int width = 96, height = 40;
        var pixels = new byte[width * height * 4];
        Array.Fill(pixels, (byte)255);
        foreach ((int left, int right) in new[] { (12, 30), (36, 52), (60, 84) })
            for (int y = 12; y < 28; y++)
                for (int x = left; x < right; x++)
                {
                    int index = (y * width + x) * 4;
                    pixels[index] = pixels[index + 1] = pixels[index + 2] = 0;
                }
        return (pixels, width, height);
    }

    private static PdfOnnxOcrTensor Logits(int[] steps, PdfOnnxOcrTensor input)
    {
        int classes = Vocabulary.Length;
        var data = new float[steps.Length * classes];
        for (int step = 0; step < steps.Length; step++)
        {
            for (int cls = 0; cls < classes; cls++) data[step * classes + cls] = -4f;
            data[step * classes + steps[step]] = 4f;
            data[step * classes + 0] = Math.Max(data[step * classes + 0], 0f);
        }
        return new PdfOnnxOcrTensor(data, [1, steps.Length, classes]);
    }

    private sealed class FakeRunner(string[] inputs, string[] outputs,
        Func<PdfOnnxOcrTensor, PdfOnnxOcrTensor> run) : IPdfOnnxOcrTensorRunner
    {
        public IReadOnlyList<string> InputNames { get; } = inputs;
        public IReadOnlyList<string> OutputNames { get; } = outputs;
        public List<PdfOnnxOcrTensor> Inputs { get; } = [];
        public bool WasDisposed { get; private set; }

        public PdfOnnxOcrTensor Run(string inputName, PdfOnnxOcrTensor input, string outputName,
            CancellationToken cancellationToken = default)
        {
            Inputs.Add(input);
            return run(input);
        }

        public void Dispose() => WasDisposed = true;
    }
}
