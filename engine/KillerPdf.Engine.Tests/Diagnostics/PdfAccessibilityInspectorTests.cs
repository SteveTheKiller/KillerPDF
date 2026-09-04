using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfAccessibilityInspectorTests
{
    [Fact]
    public void TaggingProposalInfersVisualOrderAndRequiresReview()
    {
        PdfImage image = PdfImage.FromRgb(1, 1, new byte[] { 255, 0, 0 });
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 400, new PdfContentStreamBuilder()
                .BeginText().SetFont(PdfStandardFont.HelveticaBold, 20)
                .MoveText(20, 350).ShowLatin1Text("Heading").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(20, 300).ShowLatin1Text("Body text").EndText()
                .BeginText().SetFont(PdfStandardFont.Helvetica, 10)
                .MoveText(20, 270).ShowLatin1Text("1. First item").EndText()
                .DrawImage(image, 20, 200, 40, 40))
            .Build());

        IReadOnlyList<PdfAccessibilityTaggingProposalItem> proposals =
            PdfAccessibilityTaggingProposal.Inspect(document);

        Assert.Equal([PdfAccessibilityProposedRole.Heading,
            PdfAccessibilityProposedRole.Paragraph,
            PdfAccessibilityProposedRole.ListItem,
            PdfAccessibilityProposedRole.Figure], proposals.Select(item => item.Role));
        Assert.Equal([0, 1, 2, 3], proposals.Select(item => item.Order));
        Assert.Equal("Heading", proposals[0].Text);
        Assert.Equal("1. First item", proposals[2].Text);
        Assert.Null(proposals[3].Text);
        Assert.All(proposals, item => Assert.True(item.RequiresReview));
        Assert.All(proposals, item => Assert.InRange(item.Confidence, 0.5, 0.75));
    }

    [Fact]
    public void InspectReportsMissingDocumentLevelAccessibilityRequirements()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        PdfAccessibilityReport report = PdfAccessibilityInspector.Inspect(document);

        Assert.False(report.PassesImplementedChecks);
        Assert.Equal([
            PdfAccessibilityFindingCode.MissingDocumentLanguage,
            PdfAccessibilityFindingCode.MissingStructureTree,
            PdfAccessibilityFindingCode.DocumentNotMarked
        ], report.Findings.Select(finding => finding.Code));
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("passesImplementedChecks").GetBoolean());
        Assert.Equal("missingDocumentLanguage",
            json.RootElement.GetProperty("findings")[0].GetProperty("code").GetString());
        Assert.Contains("[Error] MissingDocumentLanguage", report.ToText());
    }

    [Fact]
    public void RepairsMissingLanguageAfterExplicitPreviewWithoutReplacingMetadata()
    {
        byte[] source = new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata
            {
                Title = "Preserved title",
                Author = "Preserved author"
            })
            .AddBlankPage()
            .Build();
        PdfDocument document = PdfDocument.Open(source);

        PdfAccessibilityLanguageRepair preview =
            PdfAccessibilityRepair.PreviewDocumentLanguage(document, "en-US");
        PdfAccessibilityRepairResult result =
            PdfAccessibilityRepair.ApplyDocumentLanguage(document, preview);
        PdfDocument reopened = PdfDocument.Open(result.Document);
        PdfDocumentInformation information = PdfDocumentInformation.Read(reopened);

        Assert.True(preview.WillChange);
        Assert.Equal("en-US", information.Language);
        Assert.Equal("Preserved title", information.Title);
        Assert.Equal("Preserved author", information.Author);
        Assert.DoesNotContain(result.After.Findings, finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage);
        Assert.True(result.Document.Span[..source.Length].SequenceEqual(source));
        Assert.False(PdfAccessibilityRepair.PreviewDocumentLanguage(reopened, "fr-FR").WillChange);
    }

    [Fact]
    public void InspectAcceptsPdfUaDocumentLevelDeclarations()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Paragraph, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Accessible").EndText()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Accessible", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Paragraph, 0, 0, 1)
            .Build());

        PdfAccessibilityReport report = PdfAccessibilityInspector.Inspect(document);

        Assert.True(report.PassesImplementedChecks);
        Assert.Empty(report.Findings);
    }

    [Fact]
    public void ReadingOrderReportsStructureSequenceRolesAndMarkedContent()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Heading1, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 14)
            .ShowLatin1Text("Heading").EndText().EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.Paragraph, 1)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Paragraph").EndText().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Order", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Heading1, 0, 0, 1,
                actualText: "Reviewed heading")
            .AddStructureElement(PdfStructureType.Paragraph, 0, 1, 1)
            .Build());

        PdfAccessibilityReadingOrderReport report =
            PdfAccessibilityReadingOrder.Read(document);

        Assert.Equal(["H1", "P"], report.Items.Select(item => item.Role));
        Assert.Equal([0, 1], report.Items.Select(item => item.MarkedContentId));
        Assert.All(report.Items, item => Assert.Equal(0, item.PageIndex));
        Assert.All(report.Items, item => Assert.NotNull(item.StructureObjectNumber));
        Assert.Equal("Reviewed heading", report.Items[0].ActualText);
        Assert.Contains("\"markedContentId\":0", report.ToJson());
        Assert.Contains("1. H1 | Page 1 | MCID 0", report.ToText());
    }

    [Fact]
    public void InspectReportsFigureWithoutAlternateDescription()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Figure", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1)
            .Build());

        PdfAccessibilityFinding finding = Assert.Single(
            PdfAccessibilityInspector.Inspect(document).Findings);

        Assert.Equal(PdfAccessibilityFindingCode.MissingFigureAlternateDescription,
            finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void RepairsOneMissingFigureDescriptionAfterExplicitPreview()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Figure", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1)
            .Build());
        int objectNumber = Assert.Single(PdfAccessibilityInspector.Inspect(document).Findings)
            .ObjectNumber!.Value;

        PdfAccessibilityFigureRepair preview =
            PdfAccessibilityRepair.PreviewFigureAlternateDescription(
                document, objectNumber, "A filled square");
        PdfAccessibilityRepairResult result =
            PdfAccessibilityRepair.ApplyFigureAlternateDescription(document, preview);

        Assert.True(preview.WillChange);
        Assert.DoesNotContain(result.After.Findings, finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingFigureAlternateDescription);
        PdfDocument reopened = PdfDocument.Open(result.Document);
        Assert.False(PdfAccessibilityRepair.PreviewFigureAlternateDescription(
            reopened, objectNumber, "Replacement").WillChange);
        Assert.Throws<InvalidOperationException>(() =>
            PdfAccessibilityRepair.ApplyFigureAlternateDescription(reopened, preview));
    }

    [Fact]
    public void InspectAcceptsFigureAlternateDescription()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.Figure, 0)
            .Rectangle(10, 10, 20, 20).Fill()
            .EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Figure", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureElement(PdfStructureType.Figure, 0, 0, 1,
                alternateDescription: "A square")
            .Build());

        Assert.Empty(PdfAccessibilityInspector.Inspect(document).Findings);
    }

    [Fact]
    public void InspectReportsFormFieldsWithoutDescriptions()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 10, 10, 100, 20).Build());

        PdfAccessibilityFinding finding = Assert.Single(
            PdfAccessibilityInspector.Inspect(document).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription);

        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void InspectAcceptsFormFieldDescriptions()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 10, 10, 100, 20,
                fieldMetadata: new PdfFormFieldMetadata { Tooltip = "Customer name" }).Build());

        Assert.DoesNotContain(PdfAccessibilityInspector.Inspect(document).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription);
    }

    [Fact]
    public void RepairsMissingFormFieldDescriptionAndPreservesMappingName()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 10, 10, 100, 20,
                fieldMetadata: new PdfFormFieldMetadata
                {
                    MappingName = "customer_name"
                }).Build());

        PdfAccessibilityFormFieldRepair preview =
            PdfAccessibilityRepair.PreviewFormFieldDescription(
                document, "customer.name", "Customer name");
        PdfAccessibilityRepairResult result =
            PdfAccessibilityRepair.ApplyFormFieldDescription(document, preview);
        PdfFormWidgetInfo widget = Assert.Single(PdfFormWidgetReader.ReadPage(
            PdfDocument.Open(result.Document), 0));

        Assert.True(preview.WillChange);
        Assert.Equal("Customer name", widget.Tooltip);
        Assert.Equal("customer_name", widget.MappingName);
        Assert.DoesNotContain(result.After.Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingFormFieldDescription);
        Assert.False(PdfAccessibilityRepair.PreviewFormFieldDescription(
            PdfDocument.Open(result.Document), "customer.name", "Replacement").WillChange);
    }

    [Fact]
    public void InspectReportsOnlyLinksWithoutDescriptions()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddUriLink(0, 10, 10, 50, 20, "https://example.test/missing")
            .AddUriLink(0, 10, 40, 50, 20, "https://example.test/described",
                contents: "Account help").Build());

        PdfAccessibilityFinding finding = Assert.Single(
            PdfAccessibilityInspector.Inspect(document).Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingLinkDescription);

        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void RepairsMissingLinkDescriptionAfterExplicitPreview()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddUriLink(0, 10, 10, 50, 20, "https://example.test/help").Build());
        PdfLinkInfo link = Assert.Single(PdfLinkReader.ReadPage(document, 0));

        PdfAccessibilityLinkRepair preview = PdfAccessibilityRepair.PreviewLinkDescription(
            document, 0, link.AnnotationIndex, "Account help");
        PdfAccessibilityRepairResult result =
            PdfAccessibilityRepair.ApplyLinkDescription(document, preview);
        PdfDocument reopened = PdfDocument.Open(result.Document);

        Assert.True(preview.WillChange);
        Assert.Equal(link.ObjectNumber, preview.ObjectNumber);
        Assert.Equal("Account help", Assert.Single(
            PdfLinkReader.ReadPage(reopened, 0)).Description);
        Assert.DoesNotContain(result.After.Findings,
            item => item.Code == PdfAccessibilityFindingCode.MissingLinkDescription);
        Assert.False(PdfAccessibilityRepair.PreviewLinkDescription(
            reopened, 0, link.AnnotationIndex, "Replacement").WillChange);
        Assert.Throws<InvalidOperationException>(() =>
            PdfAccessibilityRepair.ApplyLinkDescription(reopened, preview));
    }

    [Fact]
    public void InspectReportsTableDataWithoutHeaders()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.TableDataCell, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Value").EndText().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Table", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureContainer(PdfStructureType.Table, 1)
            .AddStructureContainer(PdfStructureType.TableRow, 2)
            .AddStructureElement(PdfStructureType.TableDataCell, 0, 0, 3)
            .Build());

        PdfAccessibilityFinding finding = Assert.Single(
            PdfAccessibilityInspector.Inspect(document).Findings);

        Assert.Equal(PdfAccessibilityFindingCode.MissingTableHeader, finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
    }

    [Fact]
    public void RepairsReviewedTableDataCellAsHeader()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.TableDataCell, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Name").EndText().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Table", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureContainer(PdfStructureType.Table, 1)
            .AddStructureContainer(PdfStructureType.TableRow, 2)
            .AddStructureElement(PdfStructureType.TableDataCell, 0, 0, 3)
            .Build());
        int tableObjectNumber = Assert.Single(
            PdfAccessibilityInspector.Inspect(document).Findings).ObjectNumber!.Value;
        PdfDictionary table = Assert.IsType<PdfDictionary>(document.Resolve(
            new PdfIndirectReference(tableObjectNumber, 0)));
        PdfDictionary row = Assert.IsType<PdfDictionary>(document.Resolve(
            Assert.IsType<PdfIndirectReference>(table[new PdfName("K"u8)])));
        int cellObjectNumber = Assert.IsType<PdfIndirectReference>(
            row[new PdfName("K"u8)]).ObjectNumber;

        PdfAccessibilityTableHeaderRepair preview =
            PdfAccessibilityRepair.PreviewTableHeader(
                document, tableObjectNumber, cellObjectNumber);
        PdfAccessibilityRepairResult result =
            PdfAccessibilityRepair.ApplyTableHeader(document, preview);

        Assert.True(preview.WillChange);
        Assert.DoesNotContain(result.After.Findings, finding =>
            finding.Code == PdfAccessibilityFindingCode.MissingTableHeader);
        PdfDictionary repaired = Assert.IsType<PdfDictionary>(PdfDocument.Open(
            result.Document).Resolve(new PdfIndirectReference(cellObjectNumber, 0)));
        Assert.Equal("TH", Assert.IsType<PdfName>(
            repaired[new PdfName("S"u8)]).ValueAsLatin1());
        Assert.Throws<InvalidOperationException>(() =>
            PdfAccessibilityRepair.ApplyTableHeader(
                PdfDocument.Open(result.Document), preview));
    }

    [Fact]
    public void InspectAcceptsTableWithHeaderAndDataCells()
    {
        var content = new PdfContentStreamBuilder()
            .BeginMarkedContent(PdfStructureType.TableHeaderCell, 0)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Name").EndText().EndMarkedContent()
            .BeginMarkedContent(PdfStructureType.TableDataCell, 1)
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12)
            .ShowLatin1Text("Value").EndText().EndMarkedContent();
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .SetMetadata(new PdfDocumentMetadata { Title = "Table", Language = "en-US" })
            .AddPage(100, 100, content)
            .AddStructureContainer(PdfStructureType.Document)
            .AddStructureContainer(PdfStructureType.Table, 1)
            .AddStructureContainer(PdfStructureType.TableRow, 2)
            .AddStructureElement(PdfStructureType.TableHeaderCell, 0, 0, 3)
            .AddStructureElement(PdfStructureType.TableDataCell, 0, 1, 3)
            .Build());

        Assert.Empty(PdfAccessibilityInspector.Inspect(document).Findings);
    }
}
