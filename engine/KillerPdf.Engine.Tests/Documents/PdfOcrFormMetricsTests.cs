using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfOcrFormMetricsTests
{
    [Fact]
    public void CompareUsesMappedTextAndChoiceFieldValues()
    {
        PdfFormWidgetInfo text = Widget("name", PdfFormFieldKind.Text, "Ada", 0, 0, 40, 20);
        PdfFormWidgetInfo choice = Widget("region", PdfFormFieldKind.Choice, "S", 40, 0, 80, 20)
            with { Options = [new() { ExportValue = "S", DisplayValue = "South" }] };
        IReadOnlyList<PdfOcrFormRegion> regions = PdfOcrFormLayout.MapRegions(
            [text, choice], 80, 20);
        var result = new PdfOcrResult("Ada Sout", 1,
        [
            Word("Ada", regions[0]),
            Word("Sout", regions[1])
        ]);

        PdfOcrFormMetrics metrics = PdfOcrFormMetrics.Compare(
            [text, choice], result, 80, 20);

        Assert.Equal((2, 2), (metrics.ExpectedFieldCount, metrics.RecognizedFieldCount));
        Assert.Equal(7d / 8, metrics.Text.CharacterAccuracy, 12);
        Assert.Equal(0.5, metrics.Text.WordAccuracy, 12);
    }

    [Fact]
    public void ChoiceNormalizationReusesEditDistanceRows()
    {
        string recognized = new('A', 512);
        string choice = recognized[..^1] + "B";
        PdfOcrFormLayout.NormalizeChoice("warmup", ["warmup"]);

        long before = GC.GetAllocatedBytesForCurrentThread();
        string normalized = PdfOcrFormLayout.NormalizeChoice(recognized, [choice]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(choice, normalized);
        Assert.True(allocated < 32 * 1024,
            $"Choice normalization allocated {allocated:N0} bytes.");
    }

    private static PdfOcrPixelWord Word(string text, PdfOcrFormRegion region) =>
        new(text, 1, region.Left, region.Top, region.Right, region.Bottom);

    private static PdfFormWidgetInfo Widget(string name, PdfFormFieldKind kind,
        string value, double left, double bottom, double right, double top) => new()
        {
            PageIndex = 0, AnnotationIndex = 0, ObjectNumber = 1, Generation = 0,
            FieldName = name, FieldKind = kind, Flags = 0, Value = value,
            DefaultAppearance = "", MaximumLength = 0, OnValue = "",
            HasAction = false, HasAppearanceState = false, Options = [],
            Left = left, Bottom = bottom, Right = right, Top = top,
            PageBoxLeft = 0, PageBoxBottom = 0, PageBoxWidth = 80,
            PageBoxHeight = 20, PageRotation = 0
        };
}
