using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDataMergeTests
{
    [Fact]
    public void ExpandSupportsMissingValuePolicies()
    {
        var record = new Dictionary<string, string?> { ["Name"] = "Ada" };

        Assert.Equal("Hello Ada.", PdfDataMerge.Expand("Hello {{ Name }}.", record));
        Assert.Equal("Ada/", PdfDataMerge.Expand("{{Name}}/{{Code}}", record,
            PdfMissingMergeValueBehavior.Empty));
        Assert.Equal("Ada/{{Code}}", PdfDataMerge.Expand("{{Name}}/{{Code}}", record,
            PdfMissingMergeValueBehavior.KeepPlaceholder));
        Assert.Throws<KeyNotFoundException>(() => PdfDataMerge.Expand("{{Code}}", record));
        Assert.Throws<FormatException>(() => PdfDataMerge.Expand("{{Name", record));
    }

    [Fact]
    public void RunBatchIsolatesBadRecords()
    {
        IReadOnlyDictionary<string, string?>[] records =
        [
            new Dictionary<string, string?> { ["Value"] = "first" },
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["Value"] = "third" }
        ];

        IReadOnlyList<PdfDataMergeResult> results = PdfDataMerge.RunBatch(records, record =>
            Encoding.UTF8.GetBytes(PdfDataMerge.Expand("{{Value}}", record)));

        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.Contains("Value", results[1].Error, StringComparison.Ordinal);
        Assert.True(results[2].Succeeded);
        Assert.Equal("third", Encoding.UTF8.GetString(results[2].Data!.Value.Span));
    }

    [Fact]
    public void CsvRecordsSupportQuotedValuesAndMissingColumns()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
            PdfDataRecordReader.FromCsv(
                "Name,Company,Note\r\nAda,Analytical Engines,\"First, \"\"programmer\"\"\"\r\nGrace,Navy");

        Assert.Equal(2, records.Count);
        Assert.Equal("First, \"programmer\"", records[0]["Note"]);
        Assert.Equal("Grace", records[1]["name"]);
        Assert.Null(records[1]["Note"]);
        Assert.Throws<FormatException>(() =>
            PdfDataRecordReader.FromCsv("Name,name\nAda,Lovelace"));
    }

    [Fact]
    public void JsonRecordsConvertScalarValuesWithoutStoringSourceData()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
            PdfDataRecordReader.FromJson(
                """
                [{"Name":"Ada","Count":3,"Enabled":true,"Note":null}]
                """);

        IReadOnlyDictionary<string, string?> record = Assert.Single(records);
        Assert.Equal("Ada", record["name"]);
        Assert.Equal("3", record["Count"]);
        Assert.Equal("true", record["Enabled"]);
        Assert.Null(record["Note"]);
        Assert.Throws<FormatException>(() =>
            PdfDataRecordReader.FromJson("[{\"Nested\":{}}]"));
    }

    [Fact]
    public void MappingProfilesRoundTripWithoutSourceValuesAndMapRecords()
    {
        var profile = new PdfDataMergeProfile("Invoices", [
            new PdfDataMergeFieldMapping("Customer", "invoice.customer"),
            new PdfDataMergeFieldMapping("Total", "invoice.total")],
            "invoice-{{Number}}.pdf");
        string json = profile.ToJson();
        PdfDataMergeProfile restored = PdfDataMergeProfile.FromJson(json);

        PdfDataMergeMappedRecord mapped = restored.Map(new Dictionary<string, string?>
        {
            ["Customer"] = "Ada",
            ["Total"] = "$42.00",
            ["Number"] = "1001"
        });

        Assert.DoesNotContain("Ada", json, StringComparison.Ordinal);
        Assert.Equal("invoice-1001.pdf", mapped.OutputFileName);
        Assert.Equal(["Ada", "$42.00"],
            mapped.FormData.Fields.Select(field => field.Values.Single()));
        Assert.Equal(["invoice.customer", "invoice.total"],
            mapped.FormData.Fields.Select(field => field.Name));
    }

    [Fact]
    public void FormBatchGeneratesNamedPdfsAndIsolatesBadRecords()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 20, 20, 140, 24).Build());
        var profile = new PdfDataMergeProfile("Customers",
            [new PdfDataMergeFieldMapping("Name", "customer.name")],
            "customer-{{Number}}.pdf");
        IReadOnlyDictionary<string, string?>[] records = [
            new Dictionary<string, string?> { ["Name"] = "Ada", ["Number"] = "1" },
            new Dictionary<string, string?> { ["Number"] = "2" },
            new Dictionary<string, string?> { ["Name"] = "Grace", ["Number"] = "1" }];

        IReadOnlyList<PdfDataMergeDocumentResult> results =
            PdfDataMerge.RunFormBatch(template, records, profile);

        Assert.True(results[0].Succeeded);
        Assert.Equal("customer-1.pdf", results[0].OutputFileName);
        PdfDocument generated = PdfDocument.Open(results[0].Data!.Value);
        Assert.Equal("Ada", Assert.Single(PdfFormWidgetReader.ReadPage(generated, 0)).Value);
        Assert.False(results[1].Succeeded);
        Assert.Contains("Name", results[1].Error, StringComparison.Ordinal);
        Assert.False(results[2].Succeeded);
        Assert.Contains("already used", results[2].Error, StringComparison.Ordinal);
        Assert.Equal(string.Empty, Assert.Single(PdfFormWidgetReader.ReadPage(template, 0)).Value);
    }

    [Fact]
    public void FormBatchCancellationStopsBeforeAnotherRecord()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 20, 20, 140, 24).Build());
        var profile = new PdfDataMergeProfile("Customers",
            [new PdfDataMergeFieldMapping("Name", "customer.name")],
            "customer-{{Name}}.pdf");
        using var cancellation = new CancellationTokenSource();

        IEnumerable<IReadOnlyDictionary<string, string?>> Records()
        {
            yield return new Dictionary<string, string?> { ["Name"] = "Ada" };
            cancellation.Cancel();
            yield return new Dictionary<string, string?> { ["Name"] = "Grace" };
        }

        IReadOnlyList<PdfDataMergeDocumentResult> results = PdfDataMerge.RunFormBatch(
            template, Records(), profile, cancellation.Token);

        Assert.Single(results);
        Assert.Equal("customer-Ada.pdf", results[0].OutputFileName);
        Assert.Equal(string.Empty, Assert.Single(PdfFormWidgetReader.ReadPage(template, 0)).Value);
    }

    [Fact]
    public void BatchReportsSummarizeOutcomesWithoutPdfData()
    {
        PdfDataMergeDocumentResult[] results =
        [
            new(0, "customer-1.pdf", "PDF payload"u8.ToArray(), null),
            new(1, "customer-2.pdf", null, "Missing customer name")
        ];

        PdfDataMergeBatchReport report = PdfDataMergeBatchReport.Create(results);
        string json = report.ToJson();
        using JsonDocument parsed = JsonDocument.Parse(json);

        Assert.Equal(2, report.TotalRecords);
        Assert.Equal(1, report.SucceededRecords);
        Assert.Equal(1, report.FailedRecords);
        Assert.Equal("customer-2.pdf", report.Results[1].OutputFileName);
        Assert.DoesNotContain("PDF payload", json, StringComparison.Ordinal);
        Assert.Equal(1, parsed.RootElement.GetProperty("failedRecords").GetInt32());
    }
}
