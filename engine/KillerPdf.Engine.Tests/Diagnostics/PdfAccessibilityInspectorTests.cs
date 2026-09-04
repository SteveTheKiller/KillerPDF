using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using System.Text.Json;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfAccessibilityInspectorTests
{
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
