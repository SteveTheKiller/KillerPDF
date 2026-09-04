using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfStructuralComparisonTests
{
    [Fact]
    public void CompareReportsChangedContentCategoriesAndAddedPages()
    {
        PdfDocument original = Document("Original", includePath: false, pages: 1);
        PdfDocument changed = Document("Changed", includePath: true, pages: 2);

        PdfStructuralComparison comparison = PdfStructuralComparison.Compare(original, changed);

        Assert.True(comparison.HasChanges);
        Assert.Contains(comparison.Changes, change =>
            change.PageIndex == 0 && change.Kind == PdfStructuralChangeKind.Text);
        Assert.Contains(comparison.Changes, change =>
            change.PageIndex == 0 && change.Kind == PdfStructuralChangeKind.Paths);
        Assert.Contains(comparison.Changes, change =>
            change.PageIndex == 1 && change.Kind == PdfStructuralChangeKind.PageAdded);
    }

    [Fact]
    public void CompareFindsNoChangesForEquivalentDocumentsAndHonorsCancellation()
    {
        PdfDocument first = Document("Same", includePath: true, pages: 1);
        PdfDocument second = Document("Same", includePath: true, pages: 1);

        Assert.False(PdfStructuralComparison.Compare(first, second).HasChanges);
        Assert.Throws<OperationCanceledException>(() => PdfStructuralComparison.Compare(
            first, second, new CancellationToken(canceled: true)));
    }

    [Fact]
    public void CompareReportsChangedEffectiveResourcesWithoutUsingObjectNumbers()
    {
        PdfDocument original = Document("Same", includePath: false, pages: 1,
            PdfStandardFont.Helvetica);
        PdfDocument changed = Document("Same", includePath: false, pages: 1,
            PdfStandardFont.Courier);

        PdfStructuralComparison comparison = PdfStructuralComparison.Compare(original, changed);

        Assert.Contains(comparison.Changes, change =>
            change.PageIndex == 0 && change.Kind == PdfStructuralChangeKind.Resources);
    }

    [Fact]
    public void CompareReportsInstructionChangesWithoutVisibleContentChanges()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(100, 100).Build());
        PdfDocument changed = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, new PdfContentStreamBuilder().SaveState().RestoreState())
            .Build());

        PdfStructuralComparison comparison = PdfStructuralComparison.Compare(original, changed);

        PdfStructuralChange change = Assert.Single(comparison.Changes);
        Assert.Equal(PdfStructuralChangeKind.Instructions, change.Kind);
        Assert.Equal(0, change.OriginalCount);
        Assert.Equal(2, change.ChangedCount);
    }

    [Fact]
    public void ComparisonExportsReadableAndMachineReadableReports()
    {
        PdfStructuralComparison comparison = PdfStructuralComparison.Compare(
            Document("Original", includePath: false, pages: 1),
            Document("Changed", includePath: true, pages: 2));

        string text = comparison.ToText();
        Assert.Contains("Structural comparison: changes found", text, StringComparison.Ordinal);
        Assert.Contains("Page 1: Text", text, StringComparison.Ordinal);
        Assert.Contains("Page 2: PageAdded (original 0, changed 1)", text,
            StringComparison.Ordinal);

        using JsonDocument json = JsonDocument.Parse(comparison.ToJson(indented: true));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        Assert.True(json.RootElement.GetProperty("hasChanges").GetBoolean());
        JsonElement changes = json.RootElement.GetProperty("changes");
        Assert.Contains(changes.EnumerateArray(), change =>
            change.GetProperty("kind").GetString() == nameof(PdfStructuralChangeKind.Text));
    }

    [Fact]
    public void UnchangedComparisonExportsAnEmptyReport()
    {
        PdfStructuralComparison comparison = PdfStructuralComparison.Compare(
            Document("Same", includePath: true, pages: 1),
            Document("Same", includePath: true, pages: 1));

        Assert.Equal("Structural comparison: no changes\r\nChanges: 0", comparison.ToText());
        using JsonDocument json = JsonDocument.Parse(comparison.ToJson());
        Assert.False(json.RootElement.GetProperty("hasChanges").GetBoolean());
        Assert.Empty(json.RootElement.GetProperty("changes").EnumerateArray());
    }

    private static PdfDocument Document(string text, bool includePath, int pages,
        PdfStandardFont font = PdfStandardFont.Helvetica)
    {
        var content = new PdfContentStreamBuilder()
            .BeginText().SetFont(font, 12)
            .MoveText(10, 20).ShowLatin1Text(text).EndText();
        if (includePath) content.Rectangle(5, 5, 20, 10).Stroke();
        var builder = new PdfDocumentBuilder().AddPage(100, 100, content);
        for (int index = 1; index < pages; index++) builder.AddBlankPage(100, 100);
        return PdfDocument.Open(builder.Build());
    }
}
