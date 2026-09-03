using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using Xunit;

namespace KillerPdf.Engine.Tests.Diagnostics;

public sealed class PdfPreflightTests
{
    [Fact]
    public void ProfileRoundTripsAndRunsOnlySelectedChecks()
    {
        var profile = new PdfPreflightProfile("Language check",
            [PdfPreflightCheck.DocumentLanguage]);
        PdfPreflightProfile restored = PdfPreflightProfile.FromJson(profile.ToJson());
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();

        PdfPreflightReport report = PdfPreflightRunner.Run(source, restored);

        Assert.Equal("Language check", report.ProfileName);
        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("Accessibility.MissingDocumentLanguage", finding.Code);
        Assert.DoesNotContain(report.Findings,
            item => item.Code.Contains("Structure", StringComparison.Ordinal));
        using JsonDocument json = JsonDocument.Parse(report.ToJson());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
    }

    [Fact]
    public void TaggedProfileReportsBothRequiredCatalogDeclarations()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage().Build();
        var profile = new PdfPreflightProfile("Tagged PDF",
            [PdfPreflightCheck.TaggedStructure]);

        PdfPreflightReport report = PdfPreflightRunner.Run(source, profile);

        Assert.Equal([
            "Accessibility.MissingStructureTree",
            "Accessibility.DocumentNotMarked"
        ], report.Findings.Select(finding => finding.Code));
    }

    [Fact]
    public void GeneralProfileReportsStructuralDamageWithoutThrowing()
    {
        PdfPreflightReport report = PdfPreflightRunner.Run(
            "not a pdf"u8.ToArray(), PdfPreflightProfile.General);

        Assert.False(report.Passed);
        Assert.Contains(report.Findings,
            finding => finding.Code == "Structural.InvalidHeader");
    }
}
