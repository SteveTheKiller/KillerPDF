using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
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
}
