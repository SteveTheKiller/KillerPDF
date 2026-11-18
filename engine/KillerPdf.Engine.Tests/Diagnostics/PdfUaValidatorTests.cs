using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfUaValidatorTests
{
    [Fact]
    public void ReportCoversEveryMatterhornCheckpointInOrder()
    {
        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(TaggedDocument()));

        Assert.Equal(31, report.Checkpoints.Count);
        Assert.Equal(Enumerable.Range(1, 31).Select(number => number.ToString("00")),
            report.Checkpoints.Select(checkpoint => checkpoint.Number));
        Assert.Equal("Real content tagged", report.Checkpoints[0].Name);
        Assert.Equal("Fonts", report.Checkpoints[30].Name);
        Assert.Equal(1, report.DeclaredPart);
    }

    [Fact]
    public void WellFormedTaggedDocumentPassesEveryImplementedMachineCheck()
    {
        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(TaggedDocument()));

        Assert.Empty(report.Failures);
        Assert.NotEqual(PdfUaVerdict.Failed, report.Verdict);
        // Checkpoint 06 keeps 06-004 (does the title identify the document?) for a person.
        Assert.Equal(PdfUaCheckpointStatus.NeedsHumanReview, Checkpoint(report, "06").Status);
        Assert.Equal(PdfUaCheckpointStatus.Passed, Checkpoint(report, "07").Status);
        Assert.Equal(PdfUaCheckpointStatus.Passed, Checkpoint(report, "26").Status);
        // Checkpoint 13 has human-only conditions, such as whether the alt text is any good.
        PdfUaCheckpointResult graphics = Checkpoint(report, "13");
        Assert.Equal(PdfUaCheckpointStatus.NeedsHumanReview, graphics.Status);
        Assert.Contains("13-005", graphics.HumanReviewConditions);
        Assert.DoesNotContain("13-004", graphics.HumanReviewConditions);
        // Checkpoint 03 (flicker) has nothing a machine can decide.
        Assert.Equal(PdfUaCheckpointStatus.NeedsHumanReview, Checkpoint(report, "03").Status);
    }

    [Fact]
    public void VerdictNeverClaimsConformanceWhileHumanChecksRemain()
    {
        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(TaggedDocument()));

        Assert.True(report.Verdict is PdfUaVerdict.Incomplete or PdfUaVerdict.PassedMachineChecks);
        Assert.Contains(report.Checkpoints,
            checkpoint => checkpoint.Status == PdfUaCheckpointStatus.NeedsHumanReview);
    }

    [Fact]
    public void UntaggedDocumentFailsMetadataTitleLanguageContentAndFontCheckpoints()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(200, 200, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
                .MoveText(20, 100).ShowLatin1Text("Plain").EndText())
            .Build());

        PdfUaReport report = PdfUaValidator.Validate(document);

        Assert.Equal(PdfUaVerdict.Failed, report.Verdict);
        Assert.Null(report.DeclaredPart);
        string[] conditions = [.. report.Failures.Select(failure => failure.Condition)];
        Assert.Contains("01-005", conditions);
        Assert.Contains("06-001", conditions);
        Assert.Contains("07-001", conditions);
        Assert.Contains("11-001", conditions);
        Assert.Contains("31-009", conditions);
        PdfUaFailure untagged = Assert.Single(report.Failures, failure => failure.Condition == "01-005");
        Assert.Equal(0, untagged.PageIndex);
        Assert.Equal("UA1:7.1-2", untagged.Clause);
        Assert.Equal(PdfUaCheckpointStatus.Failed, Checkpoint(report, "01").Status);
    }

    [Fact]
    public void MetadataWithoutPdfUaIdentifierOrTitleFailsCheckpointSix()
    {
        byte[] pdf = TaggedDocument(xmp: Xmp(part: null, title: null));

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(pdf));

        Assert.Contains(report.Failures, failure => failure.Condition == "06-002");
        Assert.Contains(report.Failures, failure => failure.Condition == "06-003");
        Assert.DoesNotContain(report.Failures, failure => failure.Condition == "06-001");
    }

    [Fact]
    public void DisplayDocTitleFalseFailsSevenTwo()
    {
        byte[] pdf = Patch(TaggedDocument(), (document, update, catalog) =>
            catalog[Name("ViewerPreferences")] = new PdfDictionary(
                [new(Name("DisplayDocTitle"), new PdfBoolean(false))]));

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(pdf));

        Assert.Equal("07-002", Assert.Single(Checkpoint(report, "07").Failures).Condition);
    }

    [Fact]
    public void SuspectsTrueFailsOneSeven()
    {
        byte[] pdf = Patch(TaggedDocument(), (document, update, catalog) =>
            catalog[Name("MarkInfo")] = new PdfDictionary(
            [
                new(Name("Marked"), new PdfBoolean(true)),
                new(Name("Suspects"), new PdfBoolean(true))
            ]));

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(pdf));

        Assert.Contains(report.Failures, failure => failure.Condition == "01-007");
    }

    [Fact]
    public void FigureWithoutAltAndUnmappedTypesFailStructureCheckpoints()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill()
            .EndMarkedContent();
        byte[] pdf = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Language = "en-US", Title = "Shapes" })
            .SetViewerPreferences(new PdfViewerPreferences { DisplayDocumentTitle = true })
            .AddPage(200, 200, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1)
            .Build();
        pdf = DeclarePdfUa1(pdf, Xmp(1, "Shapes"));
        pdf = Patch(pdf, (document, update, catalog) =>
        {
            PdfIndirectReference rootReference =
                (PdfIndirectReference)catalog[Name("StructTreeRoot")];
            var root = ((PdfDictionary)document.Resolve(rootReference))
                .ToDictionary(item => item.Key, item => item.Value);
            root[Name("RoleMap")] = new PdfDictionary(
            [
                new(Name("Chapter"), Name("Section")),
                new(Name("Section"), Name("Chapter")),
                new(Name("P"), Name("Span"))
            ]);
            update.ReplaceObject(rootReference.ObjectNumber, new PdfDictionary(root));
        });

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(pdf));

        string[] conditions = [.. report.Failures.Select(failure => failure.Condition)];
        Assert.Contains("13-004", conditions);
        Assert.Contains("02-003", conditions);
        Assert.Contains("02-004", conditions);
        Assert.Equal(0, Assert.Single(report.Failures,
            failure => failure.Condition == "13-004").PageIndex);
    }

    [Fact]
    public void HeadingThatSkipsALevelFailsFourteenThree()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Heading1, 0)
            .Rectangle(10, 10, 5, 5).Fill().EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Heading3, 1)
            .Rectangle(10, 30, 5, 5).Fill().EndMarkedContent();
        byte[] pdf = ConformingShell(new PdfDocumentBuilder(), content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Heading1, 0, 0, 1)
            .AddStructureElement(PdfStructureType.Heading3, 0, 1, 1)
            .Build();

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(DeclarePdfUa1(pdf)));

        Assert.Equal("14-003", Assert.Single(Checkpoint(report, "14").Failures).Condition);
    }

    [Fact]
    public void ArtifactInsideTaggedContentFailsOneThree()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Paragraph, 0)
            .Rectangle(10, 10, 5, 5).Fill()
            .BeginArtifact().Rectangle(10, 30, 5, 5).Fill().EndMarkedContent()
            .EndMarkedContent();
        byte[] pdf = ConformingShell(new PdfDocumentBuilder(), content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
            .Build();

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(DeclarePdfUa1(pdf)));

        Assert.Contains(report.Failures, failure => failure.Condition == "01-003");
        Assert.DoesNotContain(report.Failures, failure => failure.Condition == "01-005");
    }

    [Fact]
    public void LinkWithoutDescriptionAndPageWithoutTabsFailAnnotationCheckpoint()
    {
        byte[] pdf = ConformingShell(new PdfDocumentBuilder(), FigureContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "A box")
            .AddUriLink(0, 20, 150, 80, 20, "https://example.com")
            .Build();

        PdfUaReport report = PdfUaValidator.Validate(PdfDocument.Open(DeclarePdfUa1(pdf)));

        string[] conditions = [.. Checkpoint(report, "28").Failures.Select(failure => failure.Condition)];
        Assert.Contains("28-008", conditions);
        Assert.Contains("28-011", conditions);
        Assert.Contains("28-012", conditions);
        Assert.Equal(PdfUaCheckpointStatus.Failed, Checkpoint(report, "28").Status);
    }

    [Fact]
    public void EncryptedDocumentWithoutAccessibilityPermissionFailsTwentySixTwo()
    {
        byte[] pdf = ConformingShell(new PdfDocumentBuilder(), FigureContent())
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1, alternateDescription: "A box")
            .SetPasswordEncryption(new KillerPdf.Engine.Security.PdfPasswordEncryptionOptions
            {
                UserPassword = "",
                OwnerPassword = "owner",
                AllowAccessibilityExtraction = false
            })
            .Build();
        PdfDocument document = PdfDocument.Open(pdf, "");

        PdfUaReport report = PdfUaValidator.Validate(document);

        Assert.Equal("26-002", Assert.Single(Checkpoint(report, "26").Failures).Condition);
    }

    [Fact]
    public void TextAndJsonReportsCarryVerdictCheckpointsAndConditions()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        PdfUaReport report = PdfUaValidator.Validate(document);

        string text = report.ToText();
        Assert.StartsWith("PDF/UA-1 result: Failed", text);
        Assert.Contains("06 Metadata: Failed", text);
        Assert.Contains("06-001", text);

        using JsonDocument json = JsonDocument.Parse(report.ToJson(indented: true));
        JsonElement root = json.RootElement;
        Assert.Equal("failed", root.GetProperty("verdict").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        JsonElement metadata = root.GetProperty("checkpoints")[5];
        Assert.Equal("06", metadata.GetProperty("number").GetString());
        Assert.Equal("failed", metadata.GetProperty("status").GetString());
        Assert.Equal("06-001", metadata.GetProperty("failures")[0]
            .GetProperty("condition").GetString());
    }

    [Fact]
    public void PreflightRoutesDeclaredPdfUa1ToTheValidator()
    {
        byte[] pdf = TaggedDocument(xmp: Xmp(part: 1, title: null));

        IReadOnlyList<PdfPreflightFinding> findings = PdfPreflightRunner.Run(pdf,
            new PdfPreflightProfile("Conformance", [PdfPreflightCheck.ConformanceDeclarations])).Findings;

        Assert.DoesNotContain(findings, finding => finding.Code == "Conformance.UnsupportedPdfUa");
        Assert.Contains(findings, finding => finding.Code == "Conformance.PdfUa1.06-003");
    }

    private static PdfUaCheckpointResult Checkpoint(PdfUaReport report, string number) =>
        Assert.Single(report.Checkpoints, checkpoint => checkpoint.Number == number);

    private static PdfContentStreamBuilder FigureContent() => new PdfContentStreamBuilder()
        .BeginMarkedContent(PdfStructureType.Figure, 0)
        .Rectangle(10, 10, 20, 20).Fill()
        .EndMarkedContent();

    private static PdfDocumentBuilder ConformingShell(
        PdfDocumentBuilder builder, PdfContentStreamBuilder content) => builder
        .SetMetadata(new PdfDocumentMetadata { Language = "en-US", Title = "Quarterly report" })
        .SetViewerPreferences(new PdfViewerPreferences { DisplayDocumentTitle = true })
        .AddPage(300, 300, content);

    private static byte[] TaggedDocument(string? xmp = null)
    {
        var content = new PdfContentStreamBuilder()
            .BeginArtifact().Rectangle(0, 290, 300, 2).Fill().EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Heading1, 0)
            .BeginText().SetFont(LiberationSans(), 18)
            .MoveText(20, 250).ShowUnicodeText("Quarterly report").EndText()
            .EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Paragraph, 1)
            .BeginText().SetFont(LiberationSans(), 12)
            .MoveText(20, 220).ShowUnicodeText("Sales rose.").EndText()
            .EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Figure, 2)
            .Rectangle(20, 100, 40, 40).Fill()
            .EndMarkedContent();
        byte[] pdf = ConformingShell(new PdfDocumentBuilder(), content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Heading1, 0, 0, 1)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 1, 1)
            .AddStructureElement(PdfStructureType.Figure, 0, 2, 1,
                alternateDescription: "A black square")
            .Build();
        return DeclarePdfUa1(pdf, xmp ?? Xmp(1, "Quarterly report"));
    }

    private static TrueTypeFont LiberationSans()
    {
        using Stream resource = typeof(PdfReadSnapshot).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.Fonts.LiberationSans-Regular.ttf")!;
        byte[] font = new byte[resource.Length];
        resource.ReadExactly(font);
        return TrueTypeFont.Load(font);
    }

    private static string Xmp(int? part, string? title)
    {
        string titleXml = title is null ? "" :
            $"<dc:title><rdf:Alt><rdf:li xml:lang=\"x-default\">{title}</rdf:li></rdf:Alt></dc:title>";
        string partXml = part is null ? "" : $"<pdfuaid:part>{part}</pdfuaid:part>";
        return "<?xpacket begin=\"\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>"
            + "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">"
            + "<rdf:Description rdf:about=\"\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\""
            + " xmlns:pdfuaid=\"http://www.aiim.org/pdfua/ns/id/\">"
            + titleXml + partXml
            + "</rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=\"w\"?>";
    }

    private static byte[] DeclarePdfUa1(byte[] pdf, string? xmp = null) =>
        Patch(pdf, (document, update, catalog) =>
        {
            byte[] data = Encoding.UTF8.GetBytes(xmp ?? Xmp(1, "Quarterly report"));
            PdfIndirectReference metadata = update.AddObject(new PdfStream(new PdfDictionary(
            [
                new(Name("Type"), Name("Metadata")),
                new(Name("Subtype"), Name("XML")),
                new(Name("Length"), new PdfInteger(data.Length))
            ]), data));
            catalog[Name("Metadata")] = metadata;
        });

    private static byte[] Patch(byte[] pdf,
        Action<PdfDocument, PdfIncrementalUpdateBuilder, Dictionary<PdfName, PdfObject>> edit)
    {
        PdfDocument document = PdfDocument.Open(pdf);
        PdfIndirectReference catalogReference =
            Assert.IsType<PdfIndirectReference>(document.Trailer[Name("Root")]);
        var catalog = ((PdfDictionary)document.Resolve(catalogReference))
            .ToDictionary(item => item.Key, item => item.Value);
        var update = new PdfIncrementalUpdateBuilder(document);
        edit(document, update, catalog);
        update.ReplaceObject(catalogReference.ObjectNumber, new PdfDictionary(catalog));
        return update.Build();
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
