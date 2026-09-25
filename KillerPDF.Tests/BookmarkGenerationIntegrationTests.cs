using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using System.IO;
using Xunit;

namespace KillerPDF.Tests;

public sealed class BookmarkGenerationIntegrationTests
{
    [Fact]
    public void DetectAndApplyBookmarkProposals_UsesReviewedSelection()
    {
        string path = Path.Combine(
            Path.GetTempPath(), $"killerpdf-bookmarks-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, new PdfDocumentBuilder()
                .AddPage(300, 400, new PdfContentStreamBuilder()
                    .BeginText().SetFont(PdfStandardFont.HelveticaBold, 22)
                    .MoveText(30, 350).ShowLatin1Text("Chapter One").EndText()
                    .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                    .MoveText(30, 300).ShowLatin1Text("Body copy").EndText())
                .Build());

            IReadOnlyList<PdfBookmarkProposal> detected =
                PdfEngineIntegration.DetectBookmarkHeadings(path,
                    new PdfBookmarkDetectionOptions { MinimumPointSize = 14 });
            PdfBookmarkProposal proposal = Assert.Single(detected);

            PdfEngineIntegration.ApplyBookmarkProposals(path,
            [
                proposal with
                {
                    Title = "Reviewed chapter",
                    Decision = PdfBookmarkProposalDecision.Accepted
                }
            ]);

            PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(
                PdfDocument.Open(File.ReadAllBytes(path))));
            Assert.Equal("Reviewed chapter", bookmark.Title);
            Assert.Equal(0, bookmark.DestinationPageIndex);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
