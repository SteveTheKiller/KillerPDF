using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFormRecognitionTests
{
    [Fact]
    public void ReviewRequiresEveryProposalToBeDecidedBeforeApply()
    {
        var review = new PdfFormRecognitionReview([
            Proposal("name", 0, 0.98), Proposal("signature", 1, 0.72)]);

        PdfFormRecognitionReview decided = review
            .Accept("name", "customer.name", PdfRecognizedFieldKind.Text,
                new PdfContentBounds(10, 20, 210, 44))
            .Reject("signature");

        Assert.False(review.IsReadyToApply);
        Assert.True(decided.IsReadyToApply);
        PdfFormFieldProposal accepted = Assert.Single(decided.Accepted);
        Assert.Equal("customer.name", accepted.SuggestedName);
        Assert.Equal(200, accepted.Bounds.Width);
        Assert.All(review.Proposals, item => Assert.Equal(PdfFormProposalStatus.Proposed, item.Status));
    }

    [Fact]
    public void ReviewOrdersProposalsAndRejectsDuplicateIds()
    {
        var review = new PdfFormRecognitionReview([
            new("lower", 0, new PdfContentBounds(10, 10, 30, 20), PdfRecognizedFieldKind.CheckBox, 1, "lower"),
            new("later", 1, new PdfContentBounds(0, 0, 10, 10), PdfRecognizedFieldKind.Text, 1, "later"),
            new("upper", 0, new PdfContentBounds(5, 50, 25, 60), PdfRecognizedFieldKind.Text, 1, "upper")]);

        Assert.Equal(["upper", "lower", "later"], review.Proposals.Select(item => item.Id));
        Assert.Throws<ArgumentException>(() => new PdfFormRecognitionReview([
            Proposal("same", 0, 1), Proposal("same", 1, 1)]));
    }

    [Fact]
    public void ProposalRejectsInvalidGeometryAndConfidence()
    {
        Assert.Throws<ArgumentException>(() => new PdfFormFieldProposal("bad", 0,
            new PdfContentBounds(10, 10, 10, 20), PdfRecognizedFieldKind.Text, 1, "bad"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfFormFieldProposal("bad", 0,
            new PdfContentBounds(0, 0, 10, 20), PdfRecognizedFieldKind.Text, 1.01, "bad"));
    }

    private static PdfFormFieldProposal Proposal(string id, int page, double confidence) =>
        new(id, page, new PdfContentBounds(0, 0, 100, 20),
            PdfRecognizedFieldKind.Text, confidence, id);
}
