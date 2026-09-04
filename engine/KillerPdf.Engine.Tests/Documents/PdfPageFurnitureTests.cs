using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Authoring;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageFurnitureTests
{
    [Fact]
    public void FormatterExpandsBuiltInAndCustomTokensDeterministically()
    {
        var context = new PdfPageFurnitureContext
        {
            PageNumber = 3,
            TotalPages = 12,
            PageLabel = "iii",
            FileName = "report.pdf",
            Title = "Quarterly report",
            Author = "Ada",
            Date = new DateOnly(2026, 9, 3),
            CustomTokens = new Dictionary<string, string?> { ["case"] = "A-42" }
        };

        string value = PdfPageFurnitureFormatter.Format(
            "{title} | {label} | {page}/{pages} | {filename} | {author} | {date} | {case}", context);

        Assert.Equal("Quarterly report | iii | 3/12 | report.pdf | Ada | 2026-09-03 | A-42", value);
    }

    [Fact]
    public void FormatterSupportsLiteralOpeningBraceAndRejectsUnknownTokens()
    {
        var context = new PdfPageFurnitureContext
        {
            PageNumber = 1,
            TotalPages = 1,
            Date = new DateOnly(2026, 9, 3)
        };

        Assert.Equal("{Page 1", PdfPageFurnitureFormatter.Format("{{Page {page}", context));
        Assert.Throws<KeyNotFoundException>(() =>
            PdfPageFurnitureFormatter.Format("{missing}", context));
    }

    [Theory]
    [InlineData(PdfPageNumberFormat.Decimal, 27, "27")]
    [InlineData(PdfPageNumberFormat.UpperRoman, 27, "XXVII")]
    [InlineData(PdfPageNumberFormat.LowerRoman, 27, "xxvii")]
    [InlineData(PdfPageNumberFormat.UpperLetters, 27, "AA")]
    [InlineData(PdfPageNumberFormat.LowerLetters, 27, "aa")]
    public void FormatterUsesIndependentVisiblePageNumberFormats(
        PdfPageNumberFormat format, int number, string expected)
    {
        var context = new PdfPageFurnitureContext
        {
            PageNumber = number,
            TotalPages = 30,
            PageLabel = "A-7",
            PageNumberFormat = format,
            Date = new DateOnly(2026, 9, 4)
        };

        Assert.Equal(expected + " / A-7",
            PdfPageFurnitureFormatter.Format("{page} / {label}", context));
    }

    [Fact]
    public void FormatterContextsUseSavedLogicalPageLabels()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage()
            .AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)
            .AddPageLabelRange(2, PdfPageLabelStyle.Decimal, "A-", 7)
            .Build());

        IReadOnlyList<PdfPageFurnitureContext> contexts =
            PdfPageFurnitureFormatter.CreateContexts(document,
                new DateOnly(2027, 3, 14), "report.pdf", "Report", "Ada");

        Assert.Equal(["i", "ii", "A-7"], contexts.Select(context => context.PageLabel));
        Assert.Equal(["i / 3", "ii / 3", "A-7 / 3"], contexts.Select(context =>
            PdfPageFurnitureFormatter.Format("{label} / {pages}", context)));
        Assert.All(contexts, context => Assert.Equal("report.pdf", context.FileName));
    }

    [Fact]
    public void BatesPlanContinuesAcrossDocumentsAndPreservesOrder()
    {
        IReadOnlyList<PdfBatesNumber> result = PdfBatesNumbering.Plan([2, 0, 2],
            new PdfBatesNumberingOptions
            {
                StartNumber = 98,
                DigitCount = 4,
                Prefix = "CASE-",
                Suffix = "-A"
            });

        Assert.Equal(["CASE-0098-A", "CASE-0099-A", "CASE-0100-A", "CASE-0101-A"],
            result.Select(value => value.Text));
        Assert.Equal([(0, 0), (0, 1), (2, 0), (2, 1)],
            result.Select(value => (value.DocumentIndex, value.PageIndex)));
    }

    [Fact]
    public void BatesPlanRejectsInvalidCountsAndNumericOverflow()
    {
        Assert.Throws<ArgumentException>(() =>
            PdfBatesNumbering.Plan([1, -1], new PdfBatesNumberingOptions()));
        Assert.Throws<OverflowException>(() => PdfBatesNumbering.Plan([2],
            new PdfBatesNumberingOptions { StartNumber = long.MaxValue }));
    }

    [Fact]
    public void PlacementPlannerAlignsHeadersAndReportsContentCollisions()
    {
        var existing = new PdfContentBounds(430, 740, 570, 780);

        PdfPageFurniturePlacement placement = PdfPageFurniturePlacementPlanner.Plan(
            612, 792, 120, 20, 36, 18, PdfPageFurnitureEdge.Header,
            PdfPageFurnitureAlignment.Right, [existing]);

        Assert.Equal(new PdfContentBounds(456, 754, 576, 774), placement.Bounds);
        Assert.True(placement.HasCollision);
        Assert.Equal(existing, Assert.Single(placement.Collisions));
    }

    [Fact]
    public void PlacementPlannerHandlesFootersAndRejectsOversizedFurniture()
    {
        PdfPageFurniturePlacement placement = PdfPageFurniturePlacementPlanner.Plan(
            300, 400, 100, 12, 20, 24, PdfPageFurnitureEdge.Footer,
            PdfPageFurnitureAlignment.Center);

        Assert.Equal(new PdfContentBounds(100, 24, 200, 36), placement.Bounds);
        Assert.False(placement.HasCollision);
        Assert.Throws<ArgumentException>(() => PdfPageFurniturePlacementPlanner.Plan(
            100, 100, 90, 10, 6, 5, PdfPageFurnitureEdge.Header,
            PdfPageFurnitureAlignment.Left));
    }

    [Fact]
    public void WriterAddsReviewedFurnitureToSelectedPages()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400).Build());

        PdfDocument reopened = PdfDocument.Open(PdfPageFurnitureWriter.Apply(document, [
            new PdfPageFurnitureMark(0, "CASE-000001", 20, 20),
            new PdfPageFurnitureMark(1, "CASE-000002", 20, 20)]));

        Assert.Equal("CASE-000001", new PdfPageContentReader(reopened).Read(0).Text);
        Assert.Equal("CASE-000002", new PdfPageContentReader(reopened).Read(1).Text);
    }

    [Fact]
    public void WriterAppliesReviewedColorOpacityAndRotation()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(300, 400).Build());

        PdfDocument reopened = PdfDocument.Open(PdfPageFurnitureWriter.Apply(document, [
            new PdfPageFurnitureMark(0, "DRAFT", 20, 30, 12,
                new PdfRgbColor(0.8, 0.1, 0.2), 0.4, 90)]));
        PdfPageContent content = new PdfPageContentReader(reopened).Read(0);

        Assert.Equal("DRAFT", content.Text);
        Assert.Contains(content.Instructions, instruction => instruction.Operator == "cm");
        Assert.Contains(content.Instructions, instruction => instruction.Operator == "gs");
        Assert.Contains(content.Instructions, instruction => instruction.Operator == "rg");
    }

    [Fact]
    public void ReportRecoversOnlyKillerPdfCreatedFurnitureAfterReopen()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(300, 400).Build());
        var mark = new PdfPageFurnitureMark(0, "CASE-000042", 21, 31, 11,
            new PdfRgbColor(0.2, 0.3, 0.4), 0.65, 12);
        PdfDocument reopened = PdfDocument.Open(PdfPageFurnitureWriter.Apply(document, [mark]));

        PdfPageFurnitureReportEntry entry = Assert.Single(
            PdfPageFurnitureReport.Inspect(reopened));

        Assert.Equal(mark.PageIndex, entry.PageIndex);
        Assert.Equal(mark.Text, entry.Text);
        Assert.Equal(mark.X, entry.X);
        Assert.Equal(mark.Baseline, entry.Baseline);
        Assert.Equal(mark.FontSize, entry.FontSize);
        Assert.Equal(mark.Color, entry.Color);
        Assert.Equal(mark.Opacity, entry.Opacity);
        Assert.Equal(mark.RotationDegrees, entry.RotationDegrees);
        Assert.Empty(PdfPageFurnitureReport.Inspect(document));
        using JsonDocument json = JsonDocument.Parse(
            PdfPageFurnitureReport.ToJson(reopened));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("CASE-000042", json.RootElement.GetProperty("entries")[0]
            .GetProperty("text").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("entries")[0]
            .GetProperty("pageIndex").GetInt32());
    }

    [Fact]
    public void EditorRemovesAndReplacesOnlyKillerPdfCreatedFurniture()
    {
        var originalContent = new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 200)
            .ShowLatin1Text("Original").EndText();
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, originalContent).Build());
        PdfDocument numbered = PdfDocument.Open(PdfPageFurnitureWriter.Apply(original, [
            new PdfPageFurnitureMark(0, "CASE-000001", 20, 20)]));

        PdfDocument removed = PdfDocument.Open(PdfPageFurnitureEditor.RemoveAll(numbered));
        Assert.Equal("Original", new PdfPageContentReader(removed).Read(0).Text);
        Assert.Empty(PdfPageFurnitureReport.Inspect(removed));

        PdfDocument replaced = PdfDocument.Open(PdfPageFurnitureEditor.ReplaceAll(numbered, [
            new PdfPageFurnitureMark(0, "CASE-000099", 30, 30)]));
        Assert.Equal("Original CASE-000099",
            new PdfPageContentReader(replaced).Read(0).Text);
        Assert.Equal("CASE-000099",
            Assert.Single(PdfPageFurnitureReport.Inspect(replaced)).Text);
    }

    [Fact]
    public void BatesBatchWriterAppliesContinuousNumbersInDocumentOrder()
    {
        PdfDocument first = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400).Build());
        PdfDocument middle = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).Build());
        PdfDocument second = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).Build());

        IReadOnlyList<byte[]> output = PdfBatesNumbering.ApplyBatch(
            [first, middle, second],
            new PdfBatesNumberingOptions
            {
                StartNumber = 8,
                DigitCount = 3,
                Prefix = "CASE-"
            },
            number => new PdfPageFurnitureMark(
                number.PageIndex, number.Text, 20, 20));

        Assert.Equal("CASE-008", new PdfPageContentReader(
            PdfDocument.Open(output[0])).Read(0).Text);
        Assert.Equal("CASE-009", new PdfPageContentReader(
            PdfDocument.Open(output[0])).Read(1).Text);
        Assert.Equal("CASE-010", new PdfPageContentReader(
            PdfDocument.Open(output[1])).Read(0).Text);
        Assert.Equal("CASE-011", new PdfPageContentReader(
            PdfDocument.Open(output[2])).Read(0).Text);
        Assert.Throws<InvalidOperationException>(() => PdfBatesNumbering.ApplyBatch(
            [first], new PdfBatesNumberingOptions(),
            number => new PdfPageFurnitureMark(number.PageIndex + 1, number.Text, 20, 20)));
    }
}
