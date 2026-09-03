using System.Buffers.Binary;
using System.Text;
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

    [Fact]
    public void PrintProductionChecksPageBoxesAndOutputIntent()
    {
        byte[] withoutIntent = new PdfDocumentBuilder().AddBlankPage(200, 300).Build();
        byte[] withIntent = new PdfDocumentBuilder()
            .SetOutputIntent(PdfIccProfile.Load(Profile()), "Test RGB")
            .AddBlankPage(200, 300).Build();

        PdfPreflightReport missing = PdfPreflightRunner.Run(
            withoutIntent, PdfPreflightProfile.PrintProduction);
        PdfPreflightReport complete = PdfPreflightRunner.Run(
            withIntent, PdfPreflightProfile.PrintProduction);

        Assert.Contains(missing.Findings, finding => finding.Code == "OutputIntent.Missing");
        Assert.DoesNotContain(complete.Findings,
            finding => finding.Code.StartsWith("PageBoxes.", StringComparison.Ordinal)
                || finding.Code.StartsWith("OutputIntent.", StringComparison.Ordinal));
    }

    private static byte[] Profile()
    {
        byte[] result = new byte[132];
        BinaryPrimitives.WriteUInt32BigEndian(result, (uint)result.Length);
        Encoding.ASCII.GetBytes("RGB ").CopyTo(result, 16);
        "acsp"u8.CopyTo(result.AsSpan(36, 4));
        return result;
    }
}
