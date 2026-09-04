using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaEventEngineTests
{
    [Fact]
    public void EvaluateRunsOnlyTheRequestedFormCalcActivity()
    {
        PdfXfaInfo info = Info("""
            <template><field name="total">
              <event activity="initialize"><script contentType="application/x-formcalc">$record.quantity * 2</script></event>
              <event activity="click"><script contentType="application/x-formcalc">99</script></event>
            </field></template>
            """);
        var data = new PdfFormDataSet
        {
            Fields = [new PdfFormDataField { Name = "quantity", Values = ["4"] }]
        };

        PdfXfaEventResult result = Assert.Single(
            PdfXfaEventEngine.Evaluate(info, data, "INITIALIZE"));

        Assert.Equal("total", result.FieldPath);
        Assert.Equal("initialize", result.Activity);
        Assert.Equal(PdfXfaEventStatus.Evaluated, result.Status);
        Assert.Equal(8, result.Value);
    }

    [Fact]
    public void EvaluateReportsUnsafeAndUnsupportedEventExpressions()
    {
        PdfXfaInfo info = Info("""
            <template><field name="action">
              <event activity="click"><script contentType="application/x-javascript">app.openDoc('x')</script></event>
              <event activity="click"><script contentType="application/x-formcalc">unknown + 1</script></event>
            </field></template>
            """);

        IReadOnlyList<PdfXfaEventResult> results =
            PdfXfaEventEngine.Evaluate(info, new PdfFormDataSet(), "click");

        Assert.Equal(PdfXfaEventStatus.UnsupportedLanguage, results[0].Status);
        Assert.Equal(PdfXfaEventStatus.Failed, results[1].Status);
        Assert.All(results, result => Assert.Null(result.Value));
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("template", Encoding.UTF8.GetBytes(template))]
    };
}
