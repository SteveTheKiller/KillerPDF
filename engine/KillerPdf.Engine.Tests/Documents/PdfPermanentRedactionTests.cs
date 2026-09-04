using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPermanentRedactionTests
{
    [Fact]
    public void ReviewedAttachmentsArePermanentlyRemovedAndVerified()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("remove.txt", "private"u8.ToArray())
            .AddAttachment("keep.txt", "public"u8.ToArray()).Build());
        PdfRedactionReview all = PdfRedactionReview.FromAttachments(document);
        PdfRedactionReview review = all.Exclude(Assert.Single(all.Matches,
            match => match.Text == "keep.txt").Id);

        PdfAttachmentRedactionResult result =
            PdfPermanentRedaction.ApplyReviewedAttachments(document, review);

        Assert.Equal([Assert.Single(all.Matches,
            match => match.Text == "remove.txt").Id], result.RemovedIds);
        Assert.Equal(["keep.txt"], result.RemainingAttachmentNames);
        Assert.Equal(["keep.txt"], PdfAttachmentReader.Read(
            PdfDocument.Open(result.Document)).Select(item => item.FileName));
        Assert.DoesNotContain("private",
            System.Text.Encoding.Latin1.GetString(result.Document.Span));
        Assert.Contains("\"removedCount\":1", result.ToJson());
        Assert.Throws<ArgumentException>(() =>
            PdfPermanentRedaction.ApplyReviewedAttachments(document,
                PdfRedactionReview.FromRegions([
                    new PdfRedactionRegion(0, new PdfContentBounds(0, 0, 10, 10))])));
    }

    [Fact]
    public void ReviewedCommentsArePermanentlyRemovedAndVerified()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfDocument reviewed = PdfDocument.Open(new PdfIncrementalAnnotationEditor(source)
            .AddTextNote(0, 20, 30, "remove this", name: "remove")
            .AddTextNote(0, 60, 70, "retain this", name: "retain")
            .Build());
        PdfRedactionReview review = PdfRedactionReview.FromComments(reviewed)
            .Exclude("comment:0:1");

        PdfCommentRedactionResult result =
            PdfPermanentRedaction.ApplyReviewedComments(reviewed, review);
        PdfDocument output = PdfDocument.Open(result.Document);

        Assert.Equal(["comment:0:0"], result.RemovedIds);
        Assert.Equal(1, result.RemainingComments);
        Assert.Equal("retain this", Assert.Single(PdfCommentReader.Read(output)).Contents);
        Assert.Single(output.CrossReferences.Sections);
        Assert.DoesNotContain("remove this",
            System.Text.Encoding.Latin1.GetString(result.Document.Span));
        Assert.DoesNotContain("retain this", result.ToJson());
        Assert.Throws<NotSupportedException>(() =>
            PdfPermanentRedaction.ApplyReviewedComments(reviewed,
                PdfRedactionReview.FromComments(reviewed, overlayText: "REMOVED")));
    }

    [Fact]
    public void RebuildCreatesSingleRevisionImageOnlyPagesWithoutSourceData()
    {
        PdfImage blackedOutPage = PdfImage.FromRgb(2, 1, new byte[] { 0, 0, 0, 255, 255, 255 });

        byte[] output = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(612, 792, blackedOutPage)]);
        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, ["account 12345"]);

        Assert.True(report.Succeeded);
        Assert.Contains("Redaction verification: Passed", report.ToText());
        Assert.Contains("No findings.", report.ToText());
        PdfPageContent content = new PdfPageContentReader(PdfDocument.Open(output)).Read(0);
        Assert.Empty(content.Text);
        Assert.Single(content.Images);
    }

    [Fact]
    public void VerifierRejectsSearchableOrNonRasterOutput()
    {
        byte[] output = new PdfDocumentBuilder().AddPage(200, 200,
            new PdfContentStreamBuilder().BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 100).ShowLatin1Text("secret account").EndText()).Build();

        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, ["secret"]);

        Assert.False(report.Succeeded);
        Assert.Contains(report.Findings, finding => finding.Code == "NonRasterContent");
        Assert.Contains(report.Findings, finding => finding.Code == "ProhibitedText");
        Assert.Contains(report.Findings, finding => finding.Code == "ProhibitedObjectText");
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("succeeded").GetBoolean());
        Assert.Equal(report.Findings.Count,
            json.RootElement.GetProperty("findings").GetArrayLength());
    }

    [Fact]
    public void VerifierFindsProhibitedTextInHiddenActiveObjects()
    {
        byte[] clean = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100,
                PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        var update = new PdfIncrementalUpdateBuilder(PdfDocument.Open(clean));
        update.AddObject(new PdfString("hidden secret"u8.ToArray(), PdfStringForm.Literal));

        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            update.Build(), 1, ["secret"]);

        PdfRedactionVerificationFinding finding = Assert.Single(report.Findings,
            item => item.Code == "ProhibitedObjectText");
        Assert.NotNull(finding.ObjectNumber);
        string text = report.ToText();
        Assert.Contains("Redaction verification: Failed", text);
        Assert.Contains("ProhibitedObjectText", text);
        Assert.Contains($"Object {finding.ObjectNumber}", text);
    }

    [Fact]
    public void VerifierRejectsRecoverableCatalogAndPageDataStores()
    {
        byte[] clean = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100,
                PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        PdfDocument document = PdfDocument.Open(clean);
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            document.Trailer[Name("Root")]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            document.Resolve(catalogReference));
        PdfIndirectReference pagesReference = Assert.IsType<PdfIndirectReference>(
            catalog[Name("Pages")]);
        PdfDictionary pages = Assert.IsType<PdfDictionary>(
            document.Resolve(pagesReference));
        PdfIndirectReference pageReference = Assert.IsType<PdfIndirectReference>(
            Assert.IsType<PdfArray>(pages[Name("Kids")])[0]);
        PdfDictionary page = Assert.IsType<PdfDictionary>(
            document.Resolve(pageReference));
        var update = new PdfIncrementalUpdateBuilder(document);
        PdfObject marker = new PdfString("private"u8.ToArray(), PdfStringForm.Literal);
        update.ReplaceObject(catalogReference.ObjectNumber,
            new PdfDictionary(catalog.Concat(new KeyValuePair<PdfName, PdfObject>[]
            {
                new(Name("StructTreeRoot"), marker),
                new(Name("AF"), marker),
                new(Name("Collection"), marker),
                new(Name("OpenAction"), marker),
                new(Name("AA"), marker),
                new(Name("OCProperties"), marker),
                new(Name("PieceInfo"), marker)
            })));
        update.ReplaceObject(pageReference.ObjectNumber,
            new PdfDictionary(page.Concat(new KeyValuePair<PdfName, PdfObject>[]
            {
                new(Name("Metadata"), marker),
                new(Name("PieceInfo"), marker),
                new(Name("AA"), marker),
                new(Name("Thumb"), marker),
                new(Name("AF"), marker)
            })));

        PdfRedactionVerificationReport report =
            PdfPermanentRedaction.VerifySanitizedOutput(update.Build(), 1);

        Assert.False(report.Succeeded);
        string[] codes = [.. report.Findings.Select(item => item.Code)];
        Assert.Contains("StructureTree", codes);
        Assert.Contains("AssociatedFiles", codes);
        Assert.Contains("Collection", codes);
        Assert.Contains("OpenAction", codes);
        Assert.Contains("CatalogActions", codes);
        Assert.Contains("OptionalContent", codes);
        Assert.Contains("CatalogPieceInfo", codes);
        Assert.Contains("PageMetadata", codes);
        Assert.Contains("PagePieceInfo", codes);
        Assert.Contains("PageActions", codes);
        Assert.Contains("PageThumbnail", codes);
        Assert.Contains("PageAssociatedFiles", codes);
    }

    [Fact]
    public void RebuildValidatesPageInputAndVerifierHonorsCancellation()
    {
        Assert.Throws<ArgumentException>(() => PdfPermanentRedaction.RebuildFromSanitizedPages([]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfSanitizedRasterPage(0, 100,
            PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 })));
        byte[] output = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100, PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        Assert.Throws<OperationCanceledException>(() => PdfPermanentRedaction.VerifySanitizedOutput(
            output, 1, cancellationToken: new CancellationToken(true)));
    }

    [Fact]
    public void BatchRebuildIsOrderedAndIsolatesDocumentFailures()
    {
        var page = new PdfSanitizedRasterPage(100, 100,
            PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }));

        IReadOnlyList<PdfRedactionBatchResult> results = PdfPermanentRedaction.RebuildBatch([
            new PdfRedactionBatchInput("first.pdf", [page], ["private"]),
            new PdfRedactionBatchInput("broken.pdf", []),
            new PdfRedactionBatchInput("last.pdf", [page])]);

        Assert.Equal([0, 1, 2], results.Select(result => result.Index));
        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.NotNull(results[1].Error);
        Assert.True(results[2].Succeeded);
        Assert.True(results[2].Document.Length > 0);
        Assert.NotNull(PdfDocument.Open(results[2].Document));
    }

    private static PdfName Name(string value) =>
        new(System.Text.Encoding.ASCII.GetBytes(value));
}
