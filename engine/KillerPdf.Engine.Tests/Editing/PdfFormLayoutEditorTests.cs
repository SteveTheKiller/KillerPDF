using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfFormLayoutEditorTests
{
    [Fact]
    public void AlignPreservesSizesValuesAndUsesSelectionBounds()
    {
        PdfDocument source = Document();
        PdfFormWidgetInfo[] original = [.. PdfFormWidgetReader.ReadPage(source, 0)];

        PdfDocument aligned = PdfDocument.Open(PdfFormLayoutEditor.Align(source,
            original.Select(widget => widget.ObjectNumber), PdfFormWidgetAlignment.Left));
        PdfFormWidgetInfo[] widgets = [.. PdfFormWidgetReader.ReadPage(aligned, 0)];

        Assert.All(widgets, widget => Assert.Equal(20, widget.Left));
        Assert.Equal([20d, 30d, 40d], widgets.Select(widget => widget.Right - widget.Left));
        Assert.Equal(["A", "B", "C"], widgets.Select(widget => widget.Value));
    }

    [Fact]
    public void DistributeSpacesCentersAndKeepsEndpoints()
    {
        PdfDocument source = Document();
        PdfFormWidgetInfo[] original = [.. PdfFormWidgetReader.ReadPage(source, 0)];

        PdfDocument distributed = PdfDocument.Open(PdfFormLayoutEditor.Distribute(source,
            original.Select(widget => widget.ObjectNumber),
            PdfFormWidgetDistribution.Horizontal));
        PdfFormWidgetInfo[] widgets = [.. PdfFormWidgetReader.ReadPage(distributed, 0)
            .OrderBy(widget => widget.Left)];
        double[] centers = [.. widgets.Select(widget => (widget.Left + widget.Right) / 2)];

        Assert.Equal(30, centers[0]);
        Assert.Equal(125, centers[1]);
        Assert.Equal(220, centers[2]);
    }

    private static PdfDocument Document() => PdfDocument.Open(
        new PdfDocumentBuilder().AddBlankPage(300, 300)
            .AddTextField(0, "first", 20, 220, 20, 20, "A")
            .AddTextField(0, "second", 80, 170, 30, 20, "B")
            .AddTextField(0, "third", 200, 120, 40, 20, "C")
            .Build());
}
