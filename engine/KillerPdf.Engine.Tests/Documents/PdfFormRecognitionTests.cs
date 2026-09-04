using KillerPdf.Engine.Authoring;
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
                new PdfContentBounds(10, 20, 210, 44), "Customer name")
            .Reject("signature");

        Assert.False(review.IsReadyToApply);
        Assert.True(decided.IsReadyToApply);
        PdfFormFieldProposal accepted = Assert.Single(decided.Accepted);
        Assert.Equal("customer.name", accepted.SuggestedName);
        Assert.Equal("Customer name", accepted.SuggestedTooltip);
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

    [Fact]
    public void ReviewedBasicProposalsPersistAsFormFields()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 300).Build());
        var pending = new PdfFormRecognitionReview([
            Proposal("text", 0, 1),
            new("check", 0, new PdfContentBounds(110, 10, 130, 30),
                PdfRecognizedFieldKind.CheckBox, 1, "check"),
            new("signature", 0, new PdfContentBounds(10, 50, 150, 80),
                PdfRecognizedFieldKind.Signature, 1, "signature")]);
        Assert.Throws<InvalidOperationException>(() => pending.ApplyAccepted(document));

        PdfFormRecognitionReview reviewed = pending.Accept("text", tooltip: "Text value")
            .Accept("check", tooltip: "Check value")
            .Accept("signature", tooltip: "Approval signature");
        PdfDocument reopened = PdfDocument.Open(reviewed.ApplyAccepted(document));
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0);

        Assert.Equal(3, widgets.Count);
        Assert.Contains(widgets, widget => widget.FieldName == "text" && widget.FieldKind == PdfFormFieldKind.Text);
        Assert.Contains(widgets, widget => widget.FieldName == "check" && widget.FieldKind == PdfFormFieldKind.Button);
        Assert.Contains(widgets, widget => widget.FieldName == "signature" && widget.FieldKind == PdfFormFieldKind.Signature);
        Assert.Equal("Text value", widgets.Single(widget => widget.FieldName == "text").Tooltip);
        Assert.Equal("Check value", widgets.Single(widget => widget.FieldName == "check").Tooltip);
        Assert.Equal("Approval signature",
            widgets.Single(widget => widget.FieldName == "signature").Tooltip);
    }

    [Fact]
    public void ApplyRejectsAcceptedKindsThatNeedMoreAuthoringChoices()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var review = new PdfFormRecognitionReview([
            new PdfFormFieldProposal("choice", 0, new PdfContentBounds(0, 0, 100, 20),
                PdfRecognizedFieldKind.DropDown, 1, "choice")]).Accept("choice");

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => review.ApplyAccepted(document));

        Assert.Contains("additional authoring choices", error.Message);
    }

    private static PdfFormFieldProposal Proposal(string id, int page, double confidence) =>
        new(id, page, new PdfContentBounds(0, 0, 100, 20),
            PdfRecognizedFieldKind.Text, confidence, id);
}
