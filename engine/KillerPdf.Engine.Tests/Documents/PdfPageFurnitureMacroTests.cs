using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageFurnitureMacroTests
{
    [Fact]
    public void NumberingStepRoundTripsAndWritesSelectedFormattedPages()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(400, 300).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Number", [
            PdfPageFurnitureMacro.NumberPagesStep(new PdfPageNumberMacroOptions
            {
                Template = "Page {page} of {pages}",
                Date = new DateOnly(2026, 9, 4),
                PageIndices = [1],
                Edge = PdfPageFurnitureEdge.Header,
                Alignment = PdfPageFurnitureAlignment.Right,
                NumberFormat = PdfPageNumberFormat.UpperRoman,
                FontSize = 12,
                HorizontalMargin = 20,
                VerticalMargin = 16
            })]).ToJson());

        PdfDocument output = PdfDocument.Open(PdfPageFurnitureMacro.Execute(
            Assert.Single(macro.Steps), source));
        PdfPageFurnitureReportEntry mark = Assert.Single(PdfPageFurnitureReport.Inspect(output));

        Assert.Equal(1, mark.PageIndex);
        Assert.Equal("Page II of 2", mark.Text);
        Assert.Equal(12, mark.FontSize);
        Assert.True(mark.X > 300);
        Assert.True(mark.Baseline > 270);
    }

    [Fact]
    public void NumberingStepBlocksUnreviewedContentCollisions()
    {
        byte[] source = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(70, 18).ShowLatin1Text("Existing").EndText()).Build();
        PdfMacroStep step = PdfPageFurnitureMacro.NumberPagesStep(
            new PdfPageNumberMacroOptions
            {
                Date = new DateOnly(2026, 9, 4),
                FontSize = 12,
                VerticalMargin = 10
            });

        Assert.Throws<InvalidOperationException>(() =>
            PdfPageFurnitureMacro.Execute(step, source));
        Assert.Throws<ArgumentException>(() => PdfPageFurnitureMacro.Execute(
            new PdfMacroStep(PdfMacroOperation.Save), source));
    }
}
