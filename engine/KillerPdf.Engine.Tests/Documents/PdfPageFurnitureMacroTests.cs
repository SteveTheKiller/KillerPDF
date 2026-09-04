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
                Font = PdfStandardFont.HelveticaBoldOblique,
                HorizontalMargin = 20,
                VerticalMargin = 16
            })]).ToJson());

        PdfDocument output = PdfDocument.Open(PdfPageFurnitureMacro.Execute(
            Assert.Single(macro.Steps), source));
        PdfPageFurnitureReportEntry mark = Assert.Single(PdfPageFurnitureReport.Inspect(output));

        Assert.Equal(1, mark.PageIndex);
        Assert.Equal("Page II of 2", mark.Text);
        Assert.Equal(12, mark.FontSize);
        Assert.Equal(PdfStandardFont.HelveticaBoldOblique, mark.Font);
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

    [Fact]
    public void NumberingStepExpandsSavedDocumentMetadata()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage(300, 400)
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Quarterly report",
                Author = "Ada"
            }).Build();
        PdfMacroStep step = PdfPageFurnitureMacro.NumberPagesStep(
            new PdfPageNumberMacroOptions
            {
                Template = "{title} | {author}",
                Date = new DateOnly(2026, 9, 4)
            });

        PdfDocument output = PdfDocument.Open(PdfPageFurnitureMacro.Execute(step, source));

        Assert.Equal("Quarterly report | Ada",
            Assert.Single(PdfPageFurnitureReport.Inspect(output)).Text);
    }

    [Fact]
    public void NumberingStepRoundTripsCustomTemplateTokens()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage(300, 400).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Custom tokens", [
            PdfPageFurnitureMacro.NumberPagesStep(new PdfPageNumberMacroOptions
            {
                Template = "{matter} | {client}",
                Date = new DateOnly(2026, 9, 4),
                CustomTokens = new Dictionary<string, string?>
                {
                    ["matter"] = "2026-104",
                    ["client"] = "Example"
                }
            })]).ToJson());

        PdfDocument output = PdfDocument.Open(PdfPageFurnitureMacro.Execute(
            Assert.Single(macro.Steps), source));

        Assert.Equal("2026-104 | Example",
            Assert.Single(PdfPageFurnitureReport.Inspect(output)).Text);
        Assert.Throws<ArgumentException>(() =>
            PdfPageFurnitureMacro.NumberPagesStep(new PdfPageNumberMacroOptions
            {
                Date = new DateOnly(2026, 9, 4),
                CustomTokens = new Dictionary<string, string?> { [" "] = "invalid" }
            }));
    }

    [Fact]
    public void BatesStepContinuesAcrossOrderedMixedSizeDocuments()
    {
        byte[] first = new PdfDocumentBuilder()
            .AddBlankPage(200, 300).AddBlankPage(300, 200).Build();
        byte[] second = new PdfDocumentBuilder().AddBlankPage(400, 500).Build();
        PdfMacro macro = PdfMacro.FromJson(new PdfMacro("Bates", [
            PdfPageFurnitureMacro.BatesBatchStep(new PdfBatesMacroOptions
            {
                StartNumber = 98,
                DigitCount = 4,
                Prefix = "CASE-",
                Alignment = PdfPageFurnitureAlignment.Left,
                Font = PdfStandardFont.CourierBold,
                HorizontalMargin = 12
            })]).ToJson());

        IReadOnlyList<byte[]> output = PdfPageFurnitureMacro.ExecuteBatesBatch(
            Assert.Single(macro.Steps), [first, second]);
        string[] text = [.. output.SelectMany(bytes =>
            PdfPageFurnitureReport.Inspect(PdfDocument.Open(bytes))).Select(mark => mark.Text)];

        Assert.Equal(["CASE-0098", "CASE-0099", "CASE-0100"], text);
        Assert.All(output.Select(bytes => PdfDocument.Open(bytes)), document =>
            Assert.All(PdfPageFurnitureReport.Inspect(document), mark =>
                Assert.Equal(PdfStandardFont.CourierBold, mark.Font)));
    }
}
