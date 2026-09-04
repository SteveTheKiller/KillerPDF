using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfBookmarkGenerationTests
{
    [Fact]
    public void DetectHeadingsBuildsReviewableSizeBasedHierarchy()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 22)
                .MoveText(30, 350).ShowLatin1Text("Chapter One").EndText()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 16)
                .MoveText(30, 300).ShowLatin1Text("First Topic").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(30, 260).ShowLatin1Text("Body copy").EndText())
            .Build());

        IReadOnlyList<PdfBookmarkProposal> proposals =
            PdfBookmarkGeneration.DetectHeadings(document);

        Assert.Equal(["Chapter One", "First Topic"],
            proposals.Select(item => item.Title));
        Assert.Equal([0, 1], proposals.Select(item => item.Level));
        Assert.All(proposals, item =>
            Assert.Equal(PdfBookmarkProposalDecision.Pending, item.Decision));
        Assert.True(proposals[0].Bounds.Top > proposals[1].Bounds.Top);
    }

    [Fact]
    public void ApplyRequiresReviewAndAuthorsAcceptedVisibleLocations()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().Build());
        PdfBookmarkProposal[] pending =
        [
            new()
            {
                Title = "Keep", PageIndex = 1, Level = 0,
                Bounds = new PdfContentBounds(10, 600, 100, 620),
                PointSize = 18
            }
        ];

        Assert.Throws<InvalidOperationException>(() =>
            PdfBookmarkGeneration.Apply(document, pending));

        byte[] output = PdfBookmarkGeneration.Apply(document,
        [
            pending[0] with { Decision = PdfBookmarkProposalDecision.Accepted },
            new PdfBookmarkProposal
            {
                Title = "Skip", PageIndex = 0, Level = 1,
                Bounds = new PdfContentBounds(10, 500, 100, 520),
                PointSize = 16,
                Decision = PdfBookmarkProposalDecision.Rejected
            }
        ]);
        PdfBookmarkInfo bookmark = Assert.Single(
            PdfBookmarkReader.Read(PdfDocument.Open(output)));

        Assert.Equal("Keep", bookmark.Title);
        Assert.Equal(1, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitH, bookmark.Destination?.Kind);
        Assert.Equal(620, bookmark.Destination?.Values[0]);
    }
}
