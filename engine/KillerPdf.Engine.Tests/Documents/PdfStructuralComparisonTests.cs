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
