using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using System.Text;
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

    [Theory]
    [InlineData("cross")]
    [InlineData("duplicate")]
    [InlineData("case")]
    public void ReviewedAttachmentRemovalTargetsTheReviewedFileWhateverItsKeyAndListedName(string scenario)
    {
        (byte[] source, _, _) = PdfAttachmentReaderTests.KeyedScenario(scenario);
        PdfDocument document = PdfDocument.Open(source);
        PdfAttachmentInfo[] listed = [.. PdfAttachmentReader.Read(document)];
        for (int row = 0; row < listed.Length; row++)
        {
            PdfRedactionReview review = PdfRedactionReview.FromAttachments(document);
            for (int index = 0; index < listed.Length; index++)
                if (index != row) review = review.Exclude("attachment:" + index);

            PdfAttachmentRedactionResult result =
                PdfPermanentRedaction.ApplyReviewedAttachments(document, review);

            PdfAttachmentInfo kept = Assert.Single(
                PdfAttachmentReader.Read(PdfDocument.Open(result.Document)));
            PdfAttachmentInfo other = listed[1 - row];
            Assert.Equal(other.NameTreeKey, kept.NameTreeKey);
            Assert.Equal(other.Data.ToArray(), kept.Data.ToArray());
            Assert.DoesNotContain(System.Text.Encoding.ASCII.GetString(listed[row].Data.Span),
                System.Text.Encoding.Latin1.GetString(result.Document.Span));
        }
    }

    // #455: three files share the key "Content Credentials". Removing the reviewed one removes exactly it
    // and its payload, and keeps the other two.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReviewedAttachmentRemovalTakesOneOfTheFilesThatShareAKey(bool directSpecifications)
    {
        PdfDocument document = PdfDocument.Open(RawPdfFixtures.RepeatedContentCredentials(directSpecifications));
        string[] payloads = ["manifest 0", "manifest 1", "manifest 2"];
        for (int row = 0; row < payloads.Length; row++)
        {
            PdfRedactionReview review = PdfRedactionReview.FromAttachments(document);
            for (int index = 0; index < payloads.Length; index++)
                if (index != row) review = review.Exclude("attachment:" + index);

            PdfAttachmentRedactionResult result =
                PdfPermanentRedaction.ApplyReviewedAttachments(document, review);

            Assert.Equal(payloads.Where((_, index) => index != row),
                PdfAttachmentReader.Read(PdfDocument.Open(result.Document))
                    .Select(item => System.Text.Encoding.ASCII.GetString(item.Data.Span)));
            Assert.DoesNotContain(payloads[row], System.Text.Encoding.Latin1.GetString(result.Document.Span));
            Assert.Equal(["Content Credentials", "Content Credentials"], result.RemainingAttachmentNames);
        }
    }

    [Theory]
    [InlineData("page-af")]
    [InlineData("annotation-af")]
    [InlineData("form-af")]
    [InlineData("catalog-af")]
    [InlineData("file-annotation")]
    [InlineData("go-to-action")]
    public void SanitizeRemovesAFileWhereverTheDocumentReachesIt(string route)
    {
        PdfDocument document = PdfDocument.Open(ReachedFile(route, listed: false));

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveAttachments = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfOptimizationResult result = plan.Apply();

        Assert.Contains("a.txt", plan.AttachmentNames);
        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments, result.VerifiedRemovals);
        Assert.DoesNotContain(Payload, Encoding.Latin1.GetString(result.Data.Span));
    }

    [Theory]
    [InlineData("page-af")]
    [InlineData("annotation-af")]
    [InlineData("form-af")]
    [InlineData("catalog-af")]
    [InlineData("file-annotation")]
    [InlineData("go-to-action")]
    public void ReviewedRemovalFollowsTheFileToEveryPlaceThatNamesIt(string route)
    {
        PdfDocument document = PdfDocument.Open(ReachedFile(route, listed: true));

        PdfAttachmentRedactionResult result = PdfPermanentRedaction.ApplyReviewedAttachments(
            document, PdfRedactionReview.FromAttachments(document));

        Assert.Empty(result.RemainingAttachmentNames);
        Assert.DoesNotContain(Payload, Encoding.Latin1.GetString(result.Document.Span));
    }

    [Fact]
    public void ReviewedRemovalWithEveryFileChosenTakesOneTheListCannotShow()
    {
        PdfDocument document = PdfDocument.Open(ReachedFile("page-af", listed: false));
        Assert.Empty(PdfAttachmentReader.Read(document));

        PdfAttachmentRedactionResult result = PdfPermanentRedaction.ApplyReviewedAttachments(
            document, PdfRedactionReview.FromAttachments(document));

        Assert.DoesNotContain(Payload, Encoding.Latin1.GetString(result.Document.Span));
    }

    [Fact]
    public void ReviewedRemovalKeepsAFileTheReviewExcluded()
    {
        PdfDocument document = PdfDocument.Open(RawPdf(
            "<< /Type /Catalog /Pages 2 0 R /Names << /EmbeddedFiles << /Names [(a.txt) 6 0 R (b.txt) 8 0 R] >> >> /AF [6 0 R 8 0 R] >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /AF [6 0 R 8 0 R] /Contents 4 0 R >>",
            RawStream("0 0 m"), "null",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /EF << /F 7 0 R >> >>",
            RawStream(Payload, "/Type /EmbeddedFile"),
            "<< /Type /Filespec /F (b.txt) /UF (b.txt) /EF << /F 9 0 R >> >>",
            RawStream("KEPTPAYLOAD", "/Type /EmbeddedFile")));
        PdfRedactionReview all = PdfRedactionReview.FromAttachments(document);

        PdfAttachmentRedactionResult result = PdfPermanentRedaction.ApplyReviewedAttachments(
            document, all.Exclude(Assert.Single(all.Matches, match => match.Text == "b.txt").Id));

        Assert.Equal(["b.txt"], result.RemainingAttachmentNames);
        string output = Encoding.Latin1.GetString(result.Document.Span);
        Assert.DoesNotContain(Payload, output);
        Assert.Contains("KEPTPAYLOAD", output);
    }

    // XFA writers mark their packet streams as embedded files, but a packet is form data.
    [Fact]
    public void AnXfaPacketIsNotAnAttachment()
    {
        PdfDocument document = PdfDocument.Open(RawPdf(
            "<< /Type /Catalog /Pages 2 0 R /AcroForm << /Fields [] /XFA [(template) 5 0 R] >> >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
            RawStream("0 0 m"), RawStream("<template/>", "/Type /EmbeddedFile")));

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveAttachments = true,
            PackObjects = false,
            CompressStructure = false
        });
        PdfOptimizationResult result = plan.Apply();

        Assert.DoesNotContain(PdfOptimizationChangeKind.RemoveAttachments, plan.Changes);
        Assert.Contains("<template/>", Encoding.Latin1.GetString(result.Data.Span));
    }

    [Fact]
    public void AnEmbeddedFileLeftReachableFailsSanitizeVerification()
    {
        PdfDocument document = PdfDocument.Open(PlantedFile());

        PdfOptimizationPlan plan = PdfOptimizer.CreatePlan(document, new PdfOptimizationOptions
        {
            RemoveAttachments = true,
            PackObjects = false,
            CompressStructure = false
        });

        Assert.Contains(PdfOptimizationChangeKind.RemoveAttachments, plan.Changes);
        Assert.Throws<InvalidOperationException>(plan.Apply);
    }

    [Fact]
    public void AnEmbeddedFileLeftReachableFailsReviewedRemoval()
    {
        PdfDocument document = PdfDocument.Open(PlantedFile());

        Assert.Throws<InvalidOperationException>(() => PdfPermanentRedaction.ApplyReviewedAttachments(
            document, PdfRedactionReview.FromAttachments(document)));
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
    public void VerifierFindsUtf16AndHexadecimalTextInHiddenStreams()
    {
        byte[] clean = PdfPermanentRedaction.RebuildFromSanitizedPages([
            new PdfSanitizedRasterPage(100, 100,
                PdfImage.FromRgb(1, 1, new byte[] { 0, 0, 0 }))]);
        PdfDocument document = PdfDocument.Open(clean);
        var update = new PdfIncrementalUpdateBuilder(document);
        byte[] utf16 = Encoding.BigEndianUnicode.GetBytes("secret");
        update.AddObject(new PdfStream(new PdfDictionary([]),
            [.. "prefix "u8, .. utf16, .. " suffix"u8]));
        update.AddObject(new PdfStream(new PdfDictionary([]),
            "value <FEFF007300650063007200650074>"u8));

        PdfRedactionVerificationReport report = PdfPermanentRedaction.VerifySanitizedOutput(
            update.Build(), 1, ["secret"]);

        Assert.Equal(2, report.Findings.Count(item =>
            item.Code == "ProhibitedObjectText"));
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
            new PdfDictionary(catalog.Concat([
                new(Name("StructTreeRoot"), marker),
                new(Name("AF"), marker),
                new(Name("Collection"), marker),
                new(Name("OpenAction"), marker),
                new(Name("AA"), marker),
                new(Name("OCProperties"), marker),
                new(Name("PieceInfo"), marker)
            ])));
        update.ReplaceObject(pageReference.ObjectNumber,
            new PdfDictionary(page.Concat([
                new(Name("Metadata"), marker),
                new(Name("PieceInfo"), marker),
                new(Name("AA"), marker),
                new(Name("Thumb"), marker),
                new(Name("AF"), marker)
            ])));

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

    private const string Payload = "TOPSECRETPAYLOAD";

    // One file reached through a single route, and through the name tree as well when it is listed.
    private static byte[] ReachedFile(string route, bool listed)
    {
        string names = listed ? "/Names << /EmbeddedFiles << /Names [(a.txt) 6 0 R] >> >>" : "";
        string catalog = route switch
        {
            "catalog-af" => "/AF [6 0 R]",
            "go-to-action" => "/OpenAction << /S /GoToE /F 6 0 R /D [0 /Fit] /NewWindow true >>",
            _ => ""
        };
        string page = route switch
        {
            "page-af" => "/AF [6 0 R]",
            "annotation-af" => "/Annots [8 0 R]",
            "file-annotation" => "/Annots [9 0 R]",
            "form-af" => "/Resources << /XObject << /Fm 10 0 R >> >>",
            _ => ""
        };
        return RawPdf($"<< /Type /Catalog /Pages 2 0 R {names} {catalog} >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            $"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] {page} /Contents 4 0 R >>",
            RawStream(route == "form-af" ? "/Fm Do" : "0 0 m"), "null",
            "<< /Type /Filespec /F (a.txt) /UF (a.txt) /AFRelationship /Data /EF << /F 7 0 R >> >>",
            RawStream(Payload, "/Type /EmbeddedFile"),
            "<< /Type /Annot /Subtype /Stamp /Rect [0 0 10 10] /AF [6 0 R] >>",
            "<< /Type /Annot /Subtype /FileAttachment /Rect [0 0 10 10] /FS 6 0 R >>",
            RawStream("0 0 m", "/Type /XObject /Subtype /Form /BBox [0 0 10 10] /AF [6 0 R]"));
    }

    // A payload no file specification owns, so nothing tells the removal how to take it out.
    private static byte[] PlantedFile() => RawPdf("<< /Type /Catalog /Pages 2 0 R /PrivateData 5 0 R >>",
        "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
        "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] /Contents 4 0 R >>",
        RawStream("0 0 m"), RawStream(Payload, "/Type /EmbeddedFile"));

    private static string RawStream(string data, string entries = "") =>
        $"<< {entries} /Length {data.Length} >>\nstream\n{data}\nendstream";

    private static byte[] RawPdf(params string[] objects)
    {
        var pdf = new StringBuilder("%PDF-1.7\n");
        var offsets = new List<int>();
        foreach (string value in objects)
        {
            offsets.Add(pdf.Length);
            pdf.Append($"{offsets.Count} 0 obj\n{value}\nendobj\n");
        }
        int xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (int offset in offsets) pdf.Append($"{offset:0000000000} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.Latin1.GetBytes(pdf.ToString());
    }

    private static PdfName Name(string value) =>
        new(System.Text.Encoding.ASCII.GetBytes(value));
}
