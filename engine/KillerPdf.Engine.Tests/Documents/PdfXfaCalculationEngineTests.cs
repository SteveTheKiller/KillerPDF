using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaCalculationEngineTests
{
    [Fact]
    public void EvaluateUsesDatasetValuesAndEarlierCalculationResultsInTemplateOrder()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="subtotal"><calculate><script contentType="application/x-formcalc">$record.invoice.quantity * $record.invoice.price</script></calculate></field>
              <field name="tax"><calculate><script contentType="application/x-formcalc;version=1.0">subtotal * 0.2</script></calculate></field>
            </template>
            """);
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "invoice.quantity", Values = ["3"] },
                new PdfFormDataField { Name = "invoice.price", Values = ["12.5"] }
            ]
        };

        IReadOnlyList<PdfXfaCalculationResult> results =
            PdfXfaCalculationEngine.Evaluate(info, data);

        Assert.Equal(37.5, results[0].Value);
        Assert.Equal(7.5, results[1].Value);
        Assert.All(results, result => Assert.Equal(
            PdfXfaCalculationStatus.Evaluated, result.Status));
    }

    [Fact]
    public void EvaluateReportsUnsupportedAndFailedCalculationsWithoutExecutingThem()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="unsafe"><calculate><script contentType="application/x-javascript">app.openDoc('x')</script></calculate></field>
              <field name="missing"><calculate><script contentType="application/x-formcalc">unknown + 1</script></calculate></field>
              <field name="empty"><calculate><script contentType="application/x-formcalc"> </script></calculate></field>
            </template>
            """);

        IReadOnlyList<PdfXfaCalculationResult> results =
            PdfXfaCalculationEngine.Evaluate(info, new PdfFormDataSet());

        Assert.Equal(PdfXfaCalculationStatus.UnsupportedLanguage, results[0].Status);
        Assert.Equal(PdfXfaCalculationStatus.Failed, results[1].Status);
        Assert.Contains("unknown", results[1].Failure, StringComparison.Ordinal);
        Assert.Equal(PdfXfaCalculationStatus.MissingExpression, results[2].Status);
        Assert.All(results, result => Assert.Null(result.Value));
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes(template))]
    };
}
