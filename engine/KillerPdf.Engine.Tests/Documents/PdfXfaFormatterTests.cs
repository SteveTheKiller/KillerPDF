using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfaFormatterTests
{
    [Fact]
    public void FormatAppliesNumericPicturesToExactDatasetFields()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="total"><format><picture>num{z,zz9.99}</picture></format></field>
              <field name="ratio"><format><picture>num{9.9z}</picture></format></field>
            </template>
            """);
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "total", Values = ["1234.5"] },
                new PdfFormDataField { Name = "ratio", Values = ["2.5"] }
            ]
        };

        IReadOnlyList<PdfXfaFormatResult> results = PdfXfaFormatter.Format(info, data);

        Assert.Equal("1,234.50", results[0].Value);
        Assert.Equal("2.5", results[1].Value);
        Assert.All(results, result => Assert.Equal(PdfXfaFormatStatus.Formatted, result.Status));
    }

    [Fact]
    public void FormatReportsMissingUnsupportedAndInvalidValues()
    {
        PdfXfaInfo info = Info("""
            <template>
              <field name="missing"><format><picture>num{9}</picture></format></field>
              <field name="date"><format><picture>date{YYYY-MM-DD}</picture></format></field>
              <field name="bad"><format><picture>num{9.99}</picture></format></field>
            </template>
            """);
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "date", Values = ["2026-09-04"] },
                new PdfFormDataField { Name = "bad", Values = ["not-a-number"] }
            ]
        };

        IReadOnlyList<PdfXfaFormatResult> results = PdfXfaFormatter.Format(info, data);

        Assert.Equal(PdfXfaFormatStatus.MissingValue, results[0].Status);
        Assert.Equal(PdfXfaFormatStatus.UnsupportedPicture, results[1].Status);
        Assert.Equal(PdfXfaFormatStatus.InvalidValue, results[2].Status);
        Assert.All(results, result => Assert.Null(result.Value));
    }

    private static PdfXfaInfo Info(string template) => new()
    {
        IsPacketArray = true,
        Packets = [new PdfXfaPacket("template", System.Text.Encoding.UTF8.GetBytes(template))]
    };
}
