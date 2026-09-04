using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Tests.Fonts;
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
        Assert.Equal(300, restored.MinimumImageDpi);
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
    public void FormAccessibilityReportsOnlyFieldsWithoutDescriptions()
    {
        byte[] source = new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "undocumented", 10, 10, 100, 20)
            .AddTextField(0, "documented", 10, 40, 100, 20, fieldMetadata:
                new PdfFormFieldMetadata { Tooltip = "Account name" }).Build();
        var profile = new PdfPreflightProfile("Accessible forms",
            [PdfPreflightCheck.FormAccessibility]);

        PdfPreflightFinding finding = Assert.Single(
            PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("Accessibility.MissingFormFieldDescription", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.NotNull(finding.ObjectNumber);
        Assert.Contains("undocumented", finding.Message, StringComparison.Ordinal);
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
    public void TextReportIncludesProfileResultSeverityAndSourceLocation()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0).Rectangle(10, 10, 20, 20).Fill();
        var profile = new PdfPreflightProfile("Color review",
            [PdfPreflightCheck.ColorUsage]);

        string report = PdfPreflightRunner.Run(new PdfDocumentBuilder()
            .AddPage(100, 100, content).Build(), profile).ToText();

        Assert.Contains("Preflight profile: Color review", report);
        Assert.Contains("Result: Passed", report);
        Assert.Contains("[Warning] ColorUsage.DeviceRgb | Page 1:", report);
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

    [Fact]
    public void ImageResolutionUsesTheProfileThresholdAndPageIndex()
    {
        PdfImage image = PdfImage.FromRgb(100, 100, new byte[30_000]);
        byte[] source = new PdfDocumentBuilder().AddPage(300, 300,
            new PdfContentStreamBuilder().DrawImage(image, 20, 30, 144, 72)).Build();
        var profile = new PdfPreflightProfile("Newsprint",
            [PdfPreflightCheck.ImageResolution], minimumImageDpi: 75);

        PdfPreflightProfile restored = PdfPreflightProfile.FromJson(profile.ToJson());
        PdfPreflightReport report = PdfPreflightRunner.Run(source, restored);

        Assert.Equal(75, restored.MinimumImageDpi);
        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("ImageResolution.BelowMinimum", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Warning, finding.Severity);
        Assert.Equal(0, finding.PageIndex);
        Assert.Contains("50 by 100 DPI", finding.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void FontEmbeddingFindsUnembeddedFontsAndAcceptsEmbeddedFonts()
    {
        var standardContent = new PdfContentStreamBuilder()
            .BeginText().SetFont(PdfStandardFont.Helvetica, 12).ShowLatin1Text("A").EndText();
        TrueTypeFont embeddedFont = TrueTypeFont.Load(
            TrueTypeFontTests.BuildTestFont(format12: false));
        var embeddedContent = new PdfContentStreamBuilder()
            .BeginText().SetFont(embeddedFont, 12).ShowUnicodeText("A").EndText();
        var profile = new PdfPreflightProfile("Embedded fonts",
            [PdfPreflightCheck.FontEmbedding]);

        PdfPreflightReport standard = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, standardContent).Build(), profile);
        PdfPreflightReport embedded = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, embeddedContent).Build(), profile);

        PdfPreflightFinding finding = Assert.Single(standard.Findings);
        Assert.Equal("FontEmbedding.NotEmbedded", finding.Code);
        Assert.Equal(0, finding.PageIndex);
        Assert.Contains("Helvetica", finding.Message, StringComparison.Ordinal);
        Assert.Empty(embedded.Findings);
    }

    [Fact]
    public void TransparencyFindsOpacityAndBlendModes()
    {
        var content = new PdfContentStreamBuilder()
            .SetGraphicsState(new PdfGraphicsState(
                fillOpacity: 0.5, blendMode: PdfBlendMode.Multiply))
            .Rectangle(10, 10, 50, 50).Fill();
        var profile = new PdfPreflightProfile("Transparency",
            [PdfPreflightCheck.Transparency]);

        PdfPreflightReport report = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build(), profile);

        PdfPreflightFinding finding = Assert.Single(report.Findings);
        Assert.Equal("Transparency.GraphicsState", finding.Code);
        Assert.Equal(0, finding.PageIndex);
    }

    [Fact]
    public void ColorUsageFindsDeviceRgbAndOverprint()
    {
        var content = new PdfContentStreamBuilder()
            .SetFillRgb(1, 0, 0)
            .SetGraphicsState(new PdfGraphicsState(fillOverprint: true))
            .Rectangle(10, 10, 50, 50).Fill();
        var profile = new PdfPreflightProfile("Color usage",
            [PdfPreflightCheck.ColorUsage]);

        PdfPreflightReport report = PdfPreflightRunner.Run(
            new PdfDocumentBuilder().AddPage(100, 100, content).Build(), profile);

        Assert.Equal(["ColorUsage.DeviceRgb", "ColorUsage.Overprint"],
            report.Findings.Select(finding => finding.Code));
        Assert.All(report.Findings, finding => Assert.Equal(0, finding.PageIndex));
    }

    [Fact]
    public void OptionalContentCheckReportsEffectiveLayerStateAndObject()
    {
        var layer = new PdfOptionalContentGroup("Measurements", initiallyVisible: false,
            visibleWhenPrinting: true, visibleWhenExporting: false);
        byte[] source = new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().BeginOptionalContent(layer)
                .Rectangle(10, 10, 20, 20).Stroke().EndMarkedContent()).Build();
        var profile = new PdfPreflightProfile("Layers", [PdfPreflightCheck.OptionalContent]);

        PdfPreflightFinding finding = Assert.Single(PdfPreflightRunner.Run(source, profile).Findings);

        Assert.Equal("OptionalContent.LayerState", finding.Code);
        Assert.Equal(PdfDiagnosticSeverity.Information, finding.Severity);
        Assert.Contains("visible=False", finding.Message, StringComparison.Ordinal);
        Assert.Contains("print=on", finding.Message, StringComparison.Ordinal);
        Assert.Contains("export=off", finding.Message, StringComparison.Ordinal);
        Assert.NotNull(finding.ObjectNumber);
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
