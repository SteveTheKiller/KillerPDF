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
        Assert.Throws<ArgumentException>(() => new PdfFormFieldProposal("bad", 0,
            new PdfContentBounds(0, 0, 10, 20), PdfRecognizedFieldKind.Text, 1, "bad",
            suggestedChecked: true));
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

        PdfFormRecognitionReview reviewed = pending.Accept("text", tooltip: "Text value",
                readOnly: true, required: true)
            .Accept("check", tooltip: "Check value")
            .Accept("signature", tooltip: "Approval signature");
        PdfDocument reopened = PdfDocument.Open(reviewed.ApplyAccepted(document));
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0);

        Assert.Equal(3, widgets.Count);
        Assert.Contains(widgets, widget => widget.FieldName == "text" && widget.FieldKind == PdfFormFieldKind.Text);
        Assert.Contains(widgets, widget => widget.FieldName == "check" && widget.FieldKind == PdfFormFieldKind.Button);
        Assert.Contains(widgets, widget => widget.FieldName == "signature" && widget.FieldKind == PdfFormFieldKind.Signature);
        Assert.Equal("Text value", widgets.Single(widget => widget.FieldName == "text").Tooltip);
        Assert.Equal(3, widgets.Single(widget => widget.FieldName == "text").Flags & 3);
        Assert.Equal("Check value", widgets.Single(widget => widget.FieldName == "check").Tooltip);
        Assert.Equal("Approval signature",
            widgets.Single(widget => widget.FieldName == "signature").Tooltip);
    }

    [Fact]
    public void ReviewedTextAndCheckboxValuesPersist()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(300, 300).Build());
        var review = new PdfFormRecognitionReview([
            new("name", 0, new PdfContentBounds(10, 10, 150, 30),
                PdfRecognizedFieldKind.Text, 1, "name", suggestedValue: "Alice"),
            new("approved", 0, new PdfContentBounds(10, 50, 30, 70),
                PdfRecognizedFieldKind.CheckBox, 1, "approved",
                suggestedValue: "Approved", suggestedChecked: true)])
            .Accept("name")
            .Accept("approved");

        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(
            PdfDocument.Open(review.ApplyAccepted(document)), 0);

        Assert.Equal("Alice", widgets.Single(widget => widget.FieldName == "name").Value);
        PdfFormWidgetInfo approved = widgets.Single(widget => widget.FieldName == "approved");
        Assert.Equal("/Approved", approved.Value);
        Assert.Equal("/Approved", approved.OnValue);
    }

    [Fact]
    public void ReviewedChoiceProposalsPersistAsFormFields()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 300).Build());
        var review = new PdfFormRecognitionReview([
            new("country", 0, new PdfContentBounds(10, 10, 150, 30),
                PdfRecognizedFieldKind.DropDown, 1, "country", suggestedOptions: ["US", "CA"],
                suggestedValue: "CA"),
            new("custom", 0, new PdfContentBounds(10, 50, 150, 70),
                PdfRecognizedFieldKind.EditableComboBox, 1, "custom"),
            new("region", 0, new PdfContentBounds(10, 90, 150, 140),
                PdfRecognizedFieldKind.ListBox, 1, "region")])
            .Accept("country", tooltip: "Country")
            .Accept("custom", options: ["North", "South"], value: "Custom")
            .Accept("region", options: ["East", "West"], value: "West");

        PdfDocument reopened = PdfDocument.Open(review.ApplyAccepted(document));
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0);

        Assert.Equal(3, widgets.Count);
        Assert.All(widgets, widget => Assert.Equal(PdfFormFieldKind.Choice, widget.FieldKind));
        Assert.Equal("Country", widgets.Single(widget => widget.FieldName == "country").Tooltip);
    }

    [Fact]
    public void ReviewedRadioOptionsPersistAsOneGroup()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage(300, 300).Build());
        var review = new PdfFormRecognitionReview([
            new("basic", 0, new PdfContentBounds(10, 10, 30, 30),
                PdfRecognizedFieldKind.RadioButton, 1, "plan", suggestedValue: "Basic"),
            new("pro", 0, new PdfContentBounds(10, 40, 30, 60),
                PdfRecognizedFieldKind.RadioButton, 1, "plan", suggestedValue: "Pro")])
            .Accept("basic", tooltip: "Plan")
            .Accept("pro");

        PdfDocument reopened = PdfDocument.Open(review.ApplyAccepted(document));
        IReadOnlyList<PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0);

        Assert.Equal(2, widgets.Count);
        Assert.All(widgets, widget =>
        {
            Assert.Equal("plan", widget.FieldName);
            Assert.Equal(PdfFormFieldKind.Button, widget.FieldKind);
        });
        Assert.Equal(["/Basic", "/Pro"], widgets.Select(widget => widget.OnValue).Order());
    }

    [Fact]
    public void ApplyRejectsIncompleteRadioGroupsBeforeChangingDocument()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var review = new PdfFormRecognitionReview([
            new PdfFormFieldProposal("only", 0, new PdfContentBounds(0, 0, 20, 20),
                PdfRecognizedFieldKind.RadioButton, 1, "choice", suggestedValue: "Only")])
            .Accept("only");

        NotSupportedException error = Assert.Throws<NotSupportedException>(
            () => review.ApplyAccepted(document));

        Assert.Contains("at least two unique options", error.Message);
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
