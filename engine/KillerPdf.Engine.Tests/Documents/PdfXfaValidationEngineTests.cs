using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaValidationEngineTests
{
    [Fact]
    public void EvaluateAcceptsAndRejectsDatasetBackedFormCalcRules()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="quantity"><validate><script contentType="application/x-formcalc">$record.invoice.quantity gt 0</script></validate></field>
              <field name="total"><validate><script contentType="application/x-formcalc;version=1.0">$record.invoice.total le 100</script></validate></field>
            </template>
            """);
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "invoice.quantity", Values = ["3"] },
                new PdfFormDataField { Name = "invoice.total", Values = ["125"] }
            ]
        };

        IReadOnlyList<PdfXfaValidationResult> results =
            PdfXfaValidationEngine.Evaluate(info, data);

        Assert.Equal(PdfXfaValidationStatus.Passed, results[0].Status);
        Assert.Equal(PdfXfaValidationStatus.Rejected, results[1].Status);
        Assert.All(results, result => Assert.Null(result.Failure));
    }

    [Fact]
    public void EvaluateReportsUnsupportedMissingAndFailedRulesWithoutExecutingThem()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="unsafe"><validate><script contentType="application/x-javascript">app.openDoc('x')</script></validate></field>
              <field name="empty"><validate><script contentType="application/x-formcalc"> </script></validate></field>
              <field name="missing"><validate><script contentType="application/x-formcalc">unknown gt 0</script></validate></field>
            </template>
            """);

        IReadOnlyList<PdfXfaValidationResult> results =
            PdfXfaValidationEngine.Evaluate(info, new PdfFormDataSet());

        Assert.Equal(PdfXfaValidationStatus.UnsupportedLanguage, results[0].Status);
        Assert.Equal(PdfXfaValidationStatus.MissingExpression, results[1].Status);
        Assert.Equal(PdfXfaValidationStatus.Failed, results[2].Status);
        Assert.Contains("unknown", results[2].Failure, StringComparison.Ordinal);
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes(template))]
    };
}
