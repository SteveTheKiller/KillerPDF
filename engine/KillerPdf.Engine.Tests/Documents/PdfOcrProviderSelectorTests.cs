using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrProviderSelectorTests
{
    [Fact]
    public void AutomaticSelectionUsesPriorityThenStableIdentifier()
    {
        var tesseract = new StubProvider("tesseract", 10, ["eng", "deu"]);
        var onnx = new StubProvider("onnx", 20, ["eng"]);
        var alternate = new StubProvider("alternate", 20, ["eng"]);

        IPdfOcrProvider selected = PdfOcrProviderSelector.Select(
            [tesseract, onnx, alternate], new PdfOcrOptions(["eng"]));

        Assert.Same(alternate, selected);
    }

    [Fact]
    public void AutomaticSelectionRequiresEveryRequestedLanguage()
    {
        var onnx = new StubProvider("onnx", 20, ["eng"]);
        var tesseract = new StubProvider("tesseract", 10, ["eng", "deu"]);

        IPdfOcrProvider selected = PdfOcrProviderSelector.Select(
            [onnx, tesseract], new PdfOcrOptions(["eng", "deu"]));

        Assert.Same(tesseract, selected);
    }

    [Fact]
    public void ExplicitSelectionDoesNotSilentlyFallBack()
    {
        var onnx = new StubProvider("onnx", 20, ["eng"]);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            PdfOcrProviderSelector.Select([onnx], new PdfOcrOptions(["deu"]),
                new PdfOcrProviderPreference("onnx")));

        Assert.Contains("does not support", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectionRejectsDuplicateProviderIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => PdfOcrProviderSelector.Select(
            [new StubProvider("onnx", 10, ["eng"]),
             new StubProvider("ONNX", 5, ["eng"])], new PdfOcrOptions(["eng"])));
    }

    [Fact]
    public void RasterProvidersUseTheSameSelectionRules()
    {
        var onnx = new StubRasterProvider("onnx", 20, ["eng"]);
        var tesseract = new StubRasterProvider("tesseract", 10, ["eng", "deu"]);

        IPdfOcrRasterProvider selected = PdfOcrProviderSelector.Select(
            [onnx, tesseract], new PdfOcrOptions(["eng", "deu"]));

        Assert.Same(tesseract, selected);
    }

    [Fact]
    public void OnnxProviderPassesRawPixelsAndDisposesItsIsolatedSession()
    {
        var session = new RecordingOnnxSession();
        var provider = new PdfOnnxOcrProvider(new PdfOcrProviderDescriptor(
            "onnx", "ONNX", new Version(1, 0), ["eng"]), () => session);
        byte[] bgra = [1, 2, 3, 4, 5, 6, 7, 8];

        PdfOcrResult result = provider.RecognizeBgra(bgra, 1, 2, 4,
            new PdfOcrOptions(["eng"]));

        Assert.Equal("ONNX", result.Text);
        Assert.Equal(bgra, session.Pixels.ToArray());
        Assert.Equal((1, 2, 4), session.Dimensions);
        Assert.True(session.WasDisposed);
    }

    [Fact]
    public void EngineProviderSelectsPrimaryLanguageAndAcceptsPaddedRows()
    {
        PdfOcrRecognitionModel model = PdfOcrModelTrainer.Train(1, 1,
            [new PdfOcrTrainingSample("A", new float[] { 1f })]);
        var catalog = new PdfOcrRecognitionModelCatalog(
            [new KeyValuePair<string, PdfOcrRecognitionModel>("eng", model)]);
        var provider = new PdfEngineOcrProvider(catalog, new Version(1, 0));
        var options = new PdfOcrOptions(["eng-US"]);
        var pixels = new byte[32 * 132];
        for (int row = 0; row < 32; row++)
            for (int column = 0; column < 32 * 4; column++)
                pixels[row * 132 + column] = 255;

        IPdfOcrRasterProvider selected = PdfOcrProviderSelector.Select([provider], options);
        PdfOcrResult result = selected.RecognizeBgra(pixels, 32, 32, 132, options);

        Assert.Same(provider, selected);
        Assert.Empty(result.Words);
    }

    private sealed class StubProvider(string id, int priority, string[] languages) : IPdfOcrProvider
    {
        public PdfOcrProviderDescriptor Descriptor { get; } = new(
            id, id, new Version(1, 0), languages, priority);

        public bool Supports(PdfOcrOptions options) => true;

        public PdfOcrReview Recognize(PdfOcrBatchPage page, PdfOcrOptions options,
            CancellationToken cancellationToken = default) => new([]);
    }

    private sealed class StubRasterProvider(string id, int priority, string[] languages)
        : IPdfOcrRasterProvider
    {
        public PdfOcrProviderDescriptor Descriptor { get; } = new(
            id, id, new Version(1, 0), languages, priority);

        public bool Supports(PdfOcrOptions options) => true;

        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
            PdfOcrOptions options, string? characterWhitelist = null,
            CancellationToken cancellationToken = default) => new("", 0, []);
    }

    private sealed class RecordingOnnxSession : IPdfOnnxOcrSession
    {
        public ReadOnlyMemory<byte> Pixels { get; private set; }
        public (int Width, int Height, int Stride) Dimensions { get; private set; }
        public bool WasDisposed { get; private set; }

        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height, int stride,
            PdfOcrOptions options, string? characterWhitelist = null,
            CancellationToken cancellationToken = default)
        {
            Pixels = bgra.ToArray();
            Dimensions = (width, height, stride);
            return new PdfOcrResult("ONNX", 1, []);
        }

        public void Dispose() => WasDisposed = true;
    }
}
