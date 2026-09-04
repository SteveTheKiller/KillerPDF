using System.Text;
using System.Text.Json;
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
    public void PlanAppliesAndReportsPreviewedHarmlessRepairs()
    {
        const string source = "%PDF-1.7\n"
            + "1 0 obj << /Type /Catalog /Pages 2 0 R /Outlines << >> >> endobj\n"
            + "2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n"
            + "3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >> endobj\n"
            + "xref\n0 4\n0000000000 65535 f \n0000000009 00000 n \n"
            + "0000000073 00000 n \n0000000130 00000 n \n"
            + "trailer << /Size 4 /Root 1 0 R >>\nstartxref\n201\n%%EOF\n";
        PdfDocument damaged = PdfDocument.Open(Encoding.ASCII.GetBytes(source));

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(damaged,
            new PdfOptimizationOptions
            {
                RepairHarmlessArtifacts = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.RepairHarmlessArtifacts, plan.Changes);
        PdfSaveRepairChange repair = Assert.Single(result.Repairs);
        Assert.Equal(PdfSaveRepairKind.RemoveDanglingOutlines, repair.Kind);
        Assert.DoesNotContain("/Outlines", Encoding.Latin1.GetString(result.Data.Span));
        Assert.Contains("repairHarmlessArtifacts", result.ToJson());
        Assert.Contains("removeDanglingOutlines", result.ToJson());
    }

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
        using JsonDocument report = JsonDocument.Parse(result.ToJson());
        Assert.Equal(result.OriginalSize,
            report.RootElement.GetProperty("originalSize").GetInt32());
        Assert.Equal(result.OutputObjectCount,
            report.RootElement.GetProperty("outputObjectCount").GetInt32());
        Assert.Contains("removeMetadata", report.RootElement.GetProperty("verifiedRemovals")
            .EnumerateArray().Select(item => item.GetString()));
        Assert.False(report.RootElement.TryGetProperty("data", out _));
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
        byte[] authored = new PdfDocumentBuilder().AddBlankPage()
            .AddAttachment("private.txt", "secret"u8.ToArray())
            .SetOpenAction(0, PdfDestination.FitPage())
            .AddBookmark("Private bookmark", 0).Build();
        byte[] input = new PdfIncrementalAnnotationEditor(PdfDocument.Open(authored))
            .AddFileAttachmentAnnotation(0, 20, 20, 24, "private.txt")
            .Build();
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
        Assert.Equal(["private.txt"], plan.AttachmentNames);
        using JsonDocument preview = JsonDocument.Parse(plan.ToJson());
        Assert.Equal("private.txt", preview.RootElement.GetProperty("attachmentNames")[0]
            .GetString());
        Assert.Contains(PdfOptimizationChangeKind.RemoveOpenAction, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveBookmarks, plan.Changes);
        Assert.Equal([
            PdfOptimizationChangeKind.RemoveAttachments,
            PdfOptimizationChangeKind.RemoveOpenAction,
            PdfOptimizationChangeKind.RemoveBookmarks], result.VerifiedRemovals);
        Assert.Empty(PdfAttachmentReader.Read(sanitized));
        Assert.Empty(PdfAttachmentReader.ReadPageAnnotations(sanitized, 0));
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "OpenAction");
        Assert.DoesNotContain(catalog.Keys, key => key.ValueAsLatin1() == "Outlines");
    }

    [Fact]
    public void AttachmentSanitizationFindsAndRemovesPageLocalAttachments()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddAttachment("local.txt", "private"u8.ToArray())
            .AddFileAttachmentAnnotation(0, 20, 20, 24, "local.txt")
            .Build());
        PdfDocument document = PdfDocument.Open(new PdfIncrementalPageEditor(
            PdfDocument.Open(new PdfDocumentBuilder().Build()))
            .AddImportedPage(source, 0)
            .Build());
        Assert.Empty(PdfAttachmentReader.Read(document));
        Assert.Single(PdfAttachmentReader.ReadPageAnnotations(document, 0));

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                RemoveAttachments = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument sanitized = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments,
            result.VerifiedRemovals);
        Assert.Empty(PdfAttachmentReader.ReadPageAnnotations(sanitized, 0));
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
        Assert.Equal(["private.name"], plan.FormFieldNames);
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
        Assert.Equal(1, plan.CommentCount);
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
        var nonScriptAction = new PdfDictionary([
            new(new PdfName("S"u8), new PdfName("GoTo"u8)),
            new(new PdfName("D"u8), new PdfArray([new PdfInteger(0), new PdfName("Fit"u8)]))
        ]);
        var additionalActions = new PdfDictionary([
            new(new PdfName("WC"u8), scriptAction),
            new(new PdfName("WS"u8), nonScriptAction)
        ]);
        var catalogEntries = catalog.ToDictionary(item => item.Key, item => item.Value);
        catalogEntries[new PdfName("Names"u8)] = names;
        catalogEntries[new PdfName("OpenAction"u8)] = scriptAction;
        catalogEntries[new PdfName("AA"u8)] = additionalActions;
        PdfDocument document = PdfDocument.Open(new PdfIncrementalUpdateBuilder(original)
            .ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalogEntries)).Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveDocumentJavaScript = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument sanitized = PdfDocument.Open(result.Data);
        PdfDictionary sanitizedCatalog = Assert.IsType<PdfDictionary>(sanitized.Resolve(
            Assert.IsType<PdfIndirectReference>(sanitized.Trailer[new PdfName("Root"u8)])));

        Assert.Contains(PdfOptimizationChangeKind.RemoveDocumentJavaScript, plan.Changes);
        Assert.DoesNotContain(sanitizedCatalog.Keys,
            key => key.ValueAsLatin1() == "Names");
        Assert.DoesNotContain(sanitizedCatalog.Keys,
            key => key.ValueAsLatin1() == "OpenAction");
        string saved = Encoding.Latin1.GetString(result.Data.Span);
        Assert.DoesNotContain("app.alert", saved);
        PdfDictionary savedActions = Assert.IsType<PdfDictionary>(
            sanitizedCatalog[new PdfName("AA"u8)]);
        PdfDictionary savedNonScript = Assert.IsType<PdfDictionary>(
            savedActions[new PdfName("WS"u8)]);
        Assert.Equal("GoTo", Assert.IsType<PdfName>(
            savedNonScript[new PdfName("S"u8)]).ValueAsLatin1());
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
        Assert.Equal([0], plan.ThumbnailPageIndexes);
        Assert.Contains("\"thumbnailPageIndexes\":[0]", plan.ToJson());
        Assert.Contains(PdfOptimizationChangeKind.RemovePageThumbnails,
            result.VerifiedRemovals);
        Assert.DoesNotContain("/Thumb",
            System.Text.Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void SelectiveSanitizationFlattensVisibleOptionalContentAndRemovesHiddenContent()
    {
        var visibleLayer = new PdfOptionalContentGroup("Visible");
        var hiddenLayer = new PdfOptionalContentGroup("Hidden", initiallyVisible: false);
        PdfContentStreamBuilder content = new PdfContentStreamBuilder()
            .BeginOptionalContent(visibleLayer)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 70).ShowLatin1Text("Keep").EndText()
            .EndMarkedContent()
            .BeginOptionalContent(hiddenLayer)
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(10, 40).ShowLatin1Text("Drop").EndText()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                FlattenOptionalContent = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument sanitized = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.FlattenOptionalContent, plan.Changes);
        Assert.Equal(["Hidden", "Visible"], plan.OptionalContentGroupNames);
        Assert.Equal(["Hidden"], plan.HiddenOptionalContentGroupNames);
        Assert.Contains("\"hiddenOptionalContentGroupNames\":[\"Hidden\"]", plan.ToJson());
        Assert.Contains(PdfOptimizationChangeKind.FlattenOptionalContent,
            result.VerifiedRemovals);
        Assert.Empty(PdfOptionalContentReader.Read(sanitized).Groups);
        Assert.Equal("Keep", new PdfPageContentReader(sanitized).Read(0).Text);
    }

    [Fact]
    public void PlanDoesNotClaimAbsentOptionalContentFlattening()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                FlattenOptionalContent = true,
                PackObjects = false,
                CompressStructure = false
            });

        Assert.DoesNotContain(PdfOptimizationChangeKind.FlattenOptionalContent, plan.Changes);
    }

    [Fact]
    public void PlanPrunesOnlyUnreferencedPageResources()
    {
        PdfDocument document = DocumentWithUnusedFontResource();

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                PruneUnusedPageResources = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources,
            result.VerifiedRemovals);
        Assert.Equal("Keep", new PdfPageContentReader(output).Read(0).Text);
        Assert.DoesNotContain("/Unused", Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void PlanConsolidatesUsedAliasesForTheSameResource()
    {
        PdfDocument document = DocumentWithUnusedFontResource(
            "BT /F1 12 Tf (One) Tj /Unused 12 Tf (Two) Tj ET");

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                PruneUnusedPageResources = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources,
            result.VerifiedRemovals);
        Assert.Equal("OneTwo", new PdfPageContentReader(output).Read(0).Text);
        Assert.All(new PdfPageContentReader(output).ReadInstructions(0)
            .Where(instruction => instruction.Operator == "Tf"), instruction =>
                Assert.Equal(new PdfName("F1"u8), instruction.Operands[0]));
    }

    [Fact]
    public void PlanConsolidatesEquivalentResourcesStoredAsSeparateObjects()
    {
        PdfDocument document = DocumentWithUnusedFontResource(
            "BT /F1 12 Tf (One) Tj /Unused 12 Tf (Two) Tj ET",
            distinctDuplicate: true);

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                PruneUnusedPageResources = true,
                PruneUnreachableObjects = true,
                PackObjects = false,
                CompressStructure = false
            }).Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources,
            result.VerifiedRemovals);
        Assert.Equal("OneTwo", new PdfPageContentReader(output).Read(0).Text);
        Assert.All(new PdfPageContentReader(output).ReadInstructions(0)
            .Where(instruction => instruction.Operator == "Tf"), instruction =>
                Assert.Equal(new PdfName("F1"u8), instruction.Operands[0]));
        Assert.Equal(-1, result.ObjectCountDifference);
    }

    [Fact]
    public void PlanPrunesAndConsolidatesPageColorSpaces()
    {
        PdfDocument document = DocumentWithColorSpaceResources();

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                PruneUnusedPageResources = true,
                PackObjects = false,
                CompressStructure = false
            }).Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources,
            result.VerifiedRemovals);
        var selection = Assert.Single(
            new PdfPageContentReader(output).ReadInstructions(0),
            instruction => instruction.Operator == "cs");
        Assert.Equal(new PdfName("CS1"u8), selection.Operands[0]);
    }

    [Fact]
    public void PlanPrunesAndConsolidatesGraphicsShadingAndPropertyResources()
    {
        PdfDocument document = DocumentWithExtendedResources();

        PdfOptimizationResult result = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                PruneUnusedPageResources = true,
                PackObjects = false,
                CompressStructure = false
            }).Apply();
        PdfDocument output = PdfDocument.Open(result.Data);
        IReadOnlyList<KillerPdf.Engine.Parsing.PdfContentInstruction> instructions =
            new PdfPageContentReader(output).ReadInstructions(0);

        Assert.Contains(PdfOptimizationChangeKind.PruneUnusedPageResources,
            result.VerifiedRemovals);
        Assert.Equal(new PdfName("GS1"u8),
            Assert.Single(instructions, item => item.Operator == "gs").Operands[0]);
        Assert.Equal(new PdfName("Shade1"u8),
            Assert.Single(instructions, item => item.Operator == "sh").Operands[0]);
        Assert.Equal(new PdfName("Prop1"u8),
            Assert.Single(instructions, item => item.Operator == "BDC").Operands[1]);
        Assert.Equal(new PdfName("Pattern1"u8),
            Assert.Single(instructions, item => item.Operator == "scn").Operands[^1]);
        Assert.DoesNotContain("/Unused", Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void PlanRemovesAndVerifiesUnreachableObjects()
    {
        PdfDocument document = DocumentWithUnusedFontResource();
        var update = new PdfIncrementalUpdateBuilder(document);
        update.AddObject(new PdfString("unreachable private data"u8.ToArray(),
            PdfStringForm.Literal));
        PdfDocument withOrphan = PdfDocument.Open(update.Build());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(withOrphan,
            new PdfOptimizationOptions
            {
                PruneUnreachableObjects = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();

        Assert.Contains(PdfOptimizationChangeKind.PruneUnreachableObjects, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.PruneUnreachableObjects,
            result.VerifiedRemovals);
        Assert.Equal(-1, result.ObjectCountDifference);
        Assert.DoesNotContain("unreachable private data",
            Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void PlanCompressesSmallerUnfilteredStreamsAndPreservesPageText()
    {
        string text = string.Join(' ', Enumerable.Repeat("Compressible text", 80));
        PdfDocument document = DocumentWithUnusedFontResource(
            $"BT /F1 12 Tf ({text}) Tj ET");

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                CompressUnfilteredStreams = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.CompressUnfilteredStreams, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.CompressUnfilteredStreams,
            result.VerifiedRemovals);
        Assert.Equal(text, new PdfPageContentReader(output).Read(0).Text);
        Assert.Contains("/Filter /FlateDecode",
            Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void PlanRemovesAndVerifiesXfaWithoutRemovingAcroFormFields()
    {
        PdfDocument document = DocumentWithXfaAndTextField();

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document,
            new PdfOptimizationOptions
            {
                RemoveXfaData = true,
                PackObjects = false,
                CompressStructure = false
            });
        PdfOptimizationResult result = plan.Apply();
        PdfDocument output = PdfDocument.Open(result.Data);

        Assert.Contains(PdfOptimizationChangeKind.RemoveXfaData, plan.Changes);
        Assert.Contains(PdfOptimizationChangeKind.RemoveXfaData, result.VerifiedRemovals);
        Assert.Null(PdfXfaReader.Read(output));
        Assert.Single(PdfFormWidgetReader.ReadPage(output, 0));
    }

    private static PdfDocument DocumentWithUnusedFontResource(
        string content = "BT /F1 12 Tf (Keep) Tj ET", bool distinctDuplicate = false)
    {
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            $"<< /Type /Page /Parent 2 0 R /Resources << /Font << /F1 5 0 R /Unused {(distinctDuplicate ? 6 : 5)} 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
            .. distinctDuplicate
                ? ["<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>"]
                : Array.Empty<string>()
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument DocumentWithColorSpaceResources()
    {
        const string content = "/Alias cs 0.5 0.5 0.5 scn";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /ColorSpace << /CS1 5 0 R /Alias 5 0 R /Unused 6 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "/DeviceRGB",
            "/DeviceCMYK"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument DocumentWithExtendedResources()
    {
        const string content = "/GS2 gs /Shade2 sh /Pattern cs /Pattern2 scn /Span /Prop2 BDC EMC";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Resources << /ColorSpace << /Pattern /Pattern >> /ExtGState << /GS1 5 0 R /GS2 5 0 R /UnusedGS 6 0 R >> /Shading << /Shade1 7 0 R /Shade2 7 0 R /UnusedShade 8 0 R >> /Properties << /Prop1 9 0 R /Prop2 9 0 R /UnusedProp 10 0 R >> /Pattern << /Pattern1 11 0 R /Pattern2 11 0 R /UnusedPattern 12 0 R >> >> /Contents 4 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(content)} >>\nstream\n{content}\nendstream",
            "<< /Type /ExtGState /CA 1 >>",
            "<< /Type /ExtGState /CA 0.5 >>",
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 100 0] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>",
            "<< /ShadingType 2 /ColorSpace /DeviceGray /Coords [0 0 0 100] /Function << /FunctionType 2 /Domain [0 1] /C0 [0] /C1 [1] /N 1 >> >>",
            "<< /ActualText (kept) >>",
            "<< /ActualText (unused) >>",
            "<< /Type /Pattern /PatternType 2 /Shading 7 0 R >>",
            "<< /Type /Pattern /PatternType 2 /Shading 8 0 R >>"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }

    private static PdfDocument DocumentWithXfaAndTextField()
    {
        const string template = "<template/>";
        string[] objects =
        [
            "<< /Type /Catalog /Pages 2 0 R /AcroForm 4 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 /MediaBox [0 0 100 100] >>",
            "<< /Type /Page /Parent 2 0 R /Annots [5 0 R] >>",
            "<< /Fields [5 0 R] /XFA [(template) 6 0 R] >>",
            "<< /Type /Annot /Subtype /Widget /FT /Tx /T (name) /Rect [10 10 80 30] /P 3 0 R >>",
            $"<< /Length {Encoding.ASCII.GetByteCount(template)} >>\nstream\n{template}\nendstream"
        ];
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        for (int index = 0; index < objects.Length; index++)
        {
            offsets.Add(Encoding.Latin1.GetByteCount(pdf.ToString()));
            pdf.Append($"{index + 1} 0 obj\n{objects[index]}\nendobj\n");
        }
        int xref = Encoding.Latin1.GetByteCount(pdf.ToString());
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return PdfDocument.Open(Encoding.Latin1.GetBytes(pdf.ToString()));
    }
}
