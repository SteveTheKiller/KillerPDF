using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLayerComparisonTests
{
    [Fact]
    public void CompareReportsVisibilityUsageAndConfigurationChangesByLayerName()
    {
        var layer = new PdfOptionalContentGroup("Artwork",
            initiallyVisible: true, visibleWhenPrinting: true);
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder()
                .BeginOptionalContent(layer).Rectangle(0, 0, 10, 10).Fill()
                .EndMarkedContent())
            .Build());
        int objectNumber = Assert.Single(
            PdfOptionalContentReader.Read(original).Groups).ObjectNumber;
        PdfDocument visibilityChanged = PdfDocument.Open(
            PdfOptionalContentEditor.SetInitialVisibility(
                original, objectNumber, false));
        PdfDocument printChanged = PdfDocument.Open(
            PdfOptionalContentEditor.SetPrintVisibility(
                visibilityChanged, objectNumber, false));
        PdfDocument changed = PdfDocument.Open(
            PdfOptionalContentEditor.SetExportVisibility(
                printChanged, objectNumber, true));

        PdfLayerComparison comparison = PdfLayerComparison.Compare(original, changed);

        Assert.Contains(new PdfLayerChange(PdfLayerChangeKind.Visibility, "Artwork"),
            comparison.Changes);
        Assert.Contains(new PdfLayerChange(PdfLayerChangeKind.Usage, "Artwork"),
            comparison.Changes);
        Assert.Contains(new PdfLayerChange(PdfLayerChangeKind.Configuration,
            "Optional content"), comparison.Changes);
        Assert.False(PdfLayerComparison.Compare(original, original).HasChanges);
    }
}
