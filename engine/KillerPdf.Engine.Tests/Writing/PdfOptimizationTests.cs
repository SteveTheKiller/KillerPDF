using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Writing;

public sealed class PdfOptimizationTests
{
    [Fact]
    public void PlanReportsChoicesAndConsolidatesRevisionHistory()
    {
        byte[] original = new PdfDocumentBuilder().SetMetadata(new PdfDocumentMetadata
        {
            Title = "Private", Language = "en-US"
        }).AddPage(200, 200, new PdfContentStreamBuilder().BeginText()
            .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100)
            .ShowLatin1Text("Visible text").EndText()).Build();
        byte[] revised = new PdfIncrementalPageEditor(PdfDocument.Open(original))
            .SetPageDisplayDuration(0, 5).Build();
        PdfDocument document = PdfDocument.Open(revised);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveMetadata = true
        });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument reopened = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.RemoveMetadata, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.PackObjects, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.CompressStructure, plan.Changes);
        Assert.Single(reopened.CrossReferences.Sections);
        Assert.DoesNotContain(reopened.Trailer.Keys, key => key.ValueAsLatin1() == "Info");
        Assert.Equal("Visible text", new PdfPageContentReader(reopened).Read(0).Text);
        Assert.Equal(result.OutputSize - result.OriginalSize, result.SizeDifference);
    }

    [Fact]
    public void PlanDoesNotClaimAbsentMetadataRemoval()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions { RemoveMetadata = true, PackObjects = false, CompressStructure = false });

        Assert.Equal([PdfOptimizationChangeKind.ConsolidateRevisions], plan.Changes);
        Assert.True(PdfDocument.Open(plan.Apply().Data).CrossReferences.Sections.Count == 1);
    }

    [Fact]
    public void SelectiveSanitizationRemovesOnlyRequestedDocumentFeatures()
    {
        byte[] input = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("private.txt", "secret"u8.ToArray())
            .SetOpenAction(0, PdfDestination.FitPage())
            .AddBookmark("Private bookmark", 0).Build();
        PdfDocument document = PdfDocument.Open(input);

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveAttachments = true,
            RemoveOpenAction = true,
            RemoveBookmarks = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfDocument sanitized = PdfDocument.Open(plan.Apply().Data);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(sanitized.Resolve(
            Assert.IsType<PdfIndirectReference>(sanitized.Trailer[
                new PdfName("Root"u8)])));

        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveOpenAction, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveBookmarks, plan.Changes);
        Assert.Empty(PdfAttachmentReader.Read(sanitized));
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "OpenAction");
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "Outlines");
    }
}
