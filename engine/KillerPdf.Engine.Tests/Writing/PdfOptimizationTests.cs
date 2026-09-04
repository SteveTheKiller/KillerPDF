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
        Assert.True(result.OriginalObjectCount > 0);
        Assert.True(result.OutputObjectCount > 0);
        Assert.Equal(result.OutputObjectCount - result.OriginalObjectCount,
            result.ObjectCountDifference);
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
        PdfOptimizationResult result = plan.Apply();
        PdfDocument sanitized = PdfDocument.Open(result.Data);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(sanitized.Resolve(
            Assert.IsType<PdfIndirectReference>(sanitized.Trailer[
                new PdfName("Root"u8)])));

        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveOpenAction, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveBookmarks, plan.Changes);
        Assert.Equal([
            PdfOptimizationChangeKind.RemoveAttachments,
            PdfOptimizationChangeKind.RemoveOpenAction,
            PdfOptimizationChangeKind.RemoveBookmarks], result.VerifiedRemovals);
        Assert.Empty(PdfAttachmentReader.Read(sanitized));
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "OpenAction");
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "Outlines");
    }

    [Fact]
    public void SelectiveSanitizationRemovesFormFieldsAndWidgets()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "private.name", 20, 20, 120, 24, "Secret").Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveFormFields = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfDocument sanitized = PdfDocument.Open(plan.Apply().Data);

        Assert.Contains(PdfOptimizationChangeKind.RemoveFormFields, plan.Changes);
        Assert.Empty(PdfFormWidgetReader.ReadPage(sanitized, 0));
    }

    [Fact]
    public void SelectiveSanitizationRemovesComments()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(source)).AddTextNote(0, 20, 20, "Private review note").Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveComments = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfDocument sanitized = PdfDocument.Open(plan.Apply().Data);

        Assert.Contains(PdfOptimizationChangeKind.RemoveComments, plan.Changes);
        Assert.Empty(PdfCommentReader.Read(sanitized));
    }

    [Fact]
    public void CommentRemovalStillTargetsCommentsAfterFormWidgetsAreRemoved()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "private.name", 20, 20, 120, 24, "Secret").Build();
        PdfDocument document = PdfDocument.Open(new PdfIncrementalAnnotationEditor(
            PdfDocument.Open(source)).AddTextNote(0, 20, 60, "Private review note").Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveFormFields = true,
            RemoveComments = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfDocument sanitized = PdfDocument.Open(plan.Apply().Data);

        Assert.Empty(PdfFormWidgetReader.ReadPage(sanitized, 0));
        Assert.Empty(PdfCommentReader.Read(sanitized));
    }

    [Fact]
    public void SelectiveSanitizationRemovesDocumentJavaScriptNameTree()
    {
        PdfDocument original = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());
        PdfIndirectReference catalogReference = Assert.IsType<PdfIndirectReference>(
            original.Trailer[new PdfName("Root"u8)]);
        PdfDictionary catalog = Assert.IsType<PdfDictionary>(
            original.Resolve(catalogReference));
        var scriptAction = new PdfDictionary([
            new(new PdfName("S"u8), new PdfName("JavaScript"u8)),
            new(new PdfName("JS"u8), new PdfString("app.alert('private')"u8.ToArray(),
                PdfStringForm.Literal))
        ]);
        var scripts = new PdfDictionary([
            new(new PdfName("Names"u8), new PdfArray([
                new PdfString("startup"u8.ToArray(), PdfStringForm.Literal), scriptAction]))
        ]);
        var names = new PdfDictionary([
            new(new PdfName("JavaScript"u8), scripts)
        ]);
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog.Append(
                new KeyValuePair<PdfName, PdfObject>(new PdfName("Names"u8), names)))).Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveDocumentJavaScript = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfDocument sanitized = PdfDocument.Open(plan.Apply().Data);
        PdfDictionary sanitizedCatalog = Assert.IsType<PdfDictionary>(sanitized.Resolve(
            Assert.IsType<PdfIndirectReference>(sanitized.Trailer[new PdfName("Root"u8)])));

        Assert.Contains(PdfOptimizationChangeKind.RemoveDocumentJavaScript, plan.Changes);
        Assert.DoesNotContain(sanitizedCatalog.Keys,
            key => key.ValueAsLatin1() == "Names");
    }

    [Fact]
    public void SelectiveSanitizationRemovesEmbeddedPageThumbnails()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(source)
            .SetPageThumbnail(0, PdfImage.FromRgb(
                1, 1, new byte[] { 20, 40, 60 }))
            .Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                RemovePageThumbnails = true,
                PackObjects = false,
                CompressStructure = false
        });
        PdfOptimizationResult result = plan.Apply();

        Assert.Contains(PdfOptimizationChangeKind.RemovePageThumbnails, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemovePageThumbnails,
            result.VerifiedRemovals);
        Assert.DoesNotContain("/Thumb",
            System.Text.Encoding.Latin1.GetString(result.Data.Span));
    }
}
