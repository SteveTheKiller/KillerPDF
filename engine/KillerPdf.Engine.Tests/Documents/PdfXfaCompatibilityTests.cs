using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaCompatibilityTests
{
    [Fact]
    public void AnalyzeReportsDynamicLayoutUnsafeScriptsAndUnknownControls()
    {
        PdfXfaInfo info = new()
        {
            IsPacketArray = true,
            FormType = PdfXfaFormType.Dynamic,
            Packets = [Packet("template", """
                <template><field name="custom"><ui><barcode/></ui><validate>
                  <script contentType="application/x-javascript">app.openDoc('x')</script>
                </validate></field></template>
                """)]
        };

        PdfXfaCompatibilityReport report = PdfXfaCompatibility.Analyze(info);

        Assert.False(report.IsSupported);
        Assert.Equal(["dynamic-layout", "unsupported-control", "unsafe-script-language"],
            report.Findings.Select(finding => finding.Code));
        Assert.All(report.Findings.Skip(1), finding => Assert.Equal("custom", finding.FieldPath));
        Assert.Contains("XFA compatibility: unsupported content found", report.ToText(),
            StringComparison.Ordinal);
        Assert.Contains("unsupported-control at custom", report.ToText(), StringComparison.Ordinal);
        Assert.DoesNotContain("app.openDoc", report.ToText(), StringComparison.Ordinal);
        Assert.Contains("\"version\":1", report.ToJson(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnalyzeAcceptsStaticPacketArraysUsingSafeConstructs()
    {
        PdfXfaInfo info = new()
        {
            IsPacketArray = true,
            FormType = PdfXfaFormType.Static,
            Packets = [Packet("template", """
                <template><field name="amount"><ui><numericEdit/></ui><calculate>
                  <script contentType="application/x-formcalc">1 + 2</script>
                </calculate></field></template>
                """)]
        };

        PdfXfaCompatibilityReport report = PdfXfaCompatibility.Analyze(info);

        Assert.True(report.IsSupported);
        Assert.Empty(report.Findings);
        Assert.Equal("XFA compatibility: supported\r\nFindings: 0", report.ToText());
    }

    [Fact]
    public void AnalyzeAcceptsSupportedDynamicFlowLayouts()
    {
        PdfXfaInfo info = new()
        {
            IsPacketArray = true,
            FormType = PdfXfaFormType.Dynamic,
            Packets = [Packet("template", """
                <template><subform name="items" layout="tb">
                  <subform name="row" layout="row"><field name="value"><ui><textEdit/></ui></field></subform>
                </subform></template>
                """)]
        };

        PdfXfaCompatibilityReport report = PdfXfaCompatibility.Analyze(info);

        Assert.True(report.IsSupported);
        Assert.Empty(report.Findings);
    }

    private static PdfXfaPacket Packet(string name, string xml) =>
        new(name, System.Text.Encoding.UTF8.GetBytes(xml));
}
