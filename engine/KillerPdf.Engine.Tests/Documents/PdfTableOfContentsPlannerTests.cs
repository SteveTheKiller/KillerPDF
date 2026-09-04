using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTableOfContentsPlannerTests
{
    [Fact]
    public void PlanUsesBookmarkOrderDepthStylesAndDisplayedPageLabels()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage()
            .AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)
            .AddPageLabelRange(2, PdfPageLabelStyle.Decimal, "A-", 3)
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.2, 0.3, 0.4),
                Destination = PdfDestination.FitWidth(700)
            })
            .AddBookmark("Section", 2, level: 1)
            .AddBookmark("Detail", 1, level: 2)
            .AddBookmark("Next", 1)
            .Build());

        PdfTableOfContentsPlan plan = PdfTableOfContentsPlanner.Plan(document, maximumDepth: 2);

        Assert.Equal(2, plan.MaximumDepth);
        Assert.Equal(["Chapter", "Section", "Next"],
            plan.Entries.Select(entry => entry.Title));
        Assert.Equal([0, 1, 0], plan.Entries.Select(entry => entry.Depth));
        Assert.Equal(["i", "A-3", "ii"], plan.Entries.Select(entry => entry.PageLabel));
        Assert.Equal(PdfBookmarkStyle.Bold, plan.Entries[0].Style);
        Assert.Equal(new PdfRgbColor(0.2, 0.3, 0.4), plan.Entries[0].Color);
        Assert.Equal(PdfDestinationKind.FitH, plan.Entries[0].Destination?.Kind);
        Assert.All(plan.Entries, entry => Assert.True(entry.SourceObjectNumber > 0));
        Assert.Equal(0, plan.UnresolvedCount);
    }

    [Fact]
    public void PlanRejectsInvalidDepthAndSupportsDocumentsWithoutBookmarks()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Empty(PdfTableOfContentsPlanner.Plan(document).Entries);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTableOfContentsPlanner.Plan(document, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            PdfTableOfContentsPlanner.Plan(document, 257));
    }
}
