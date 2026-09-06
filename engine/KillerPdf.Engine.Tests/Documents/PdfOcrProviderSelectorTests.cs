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

        public PdfOcrResult RecognizeBgra(ReadOnlyMemory<byte> bgra, int width, int height,
            PdfOcrOptions options, string? characterWhitelist = null,
            CancellationToken cancellationToken = default) => new("", 0, []);
    }
}
