using System.IO.Compression;
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
    public void FdfAndXfdfFieldsImportAsSingleSafeRecords()
    {
        var data = new PdfFormDataSet
        {
            Fields =
            [
                new PdfFormDataField { Name = "Name", Values = ["Ada"] }
            ]
        };

        IReadOnlyDictionary<string, string?> fdf = PdfDataRecordReader.FromFdf(
            PdfFdfFormData.Write(data));
        IReadOnlyDictionary<string, string?> xfdf = PdfDataRecordReader.FromXfdf(
            PdfXfdfFormData.Write(data));

        Assert.Equal("Ada", fdf["name"]);
        Assert.Equal("Ada", xfdf["name"]);
        Assert.Throws<FormatException>(() => PdfDataRecordReader.FromXfdf(
            Encoding.UTF8.GetBytes(
                "<xfdf xmlns=\"http://ns.adobe.com/xfdf/\"><script>bad</script></xfdf>")));
    }

    [Fact]
    public void XlsxRecordsSupportSheetSelectionAndInlineStrings()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder().AddPage(100, 100,
            new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12)
                .ShowLatin1Text("A & B").EndText()).Build());
        byte[] xlsx = PdfStructuredExport.ToXlsx(source);

        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
            PdfDataRecordReader.FromXlsx(xlsx, "PDF export");

        IReadOnlyDictionary<string, string?> record = Assert.Single(records);
        Assert.Equal("1", record["Page"]);
        Assert.Equal("1", record["Line"]);
        Assert.Equal("A & B", record["Text"]);
        Assert.Throws<KeyNotFoundException>(() =>
            PdfDataRecordReader.FromXlsx(xlsx, "Missing"));
    }

    [Fact]
    public void XlsxRecordsReadSharedStringsScalarsAndSparseCells()
    {
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
            PdfDataRecordReader.FromXlsx(SharedStringWorkbook(), "Records");

        Assert.Equal(2, records.Count);
        Assert.Equal("Ada", records[0]["Name"]);
        Assert.Null(records[0]["Company"]);
        Assert.Equal("true", records[0]["Active"]);
        Assert.Equal("Grace", records[1]["Name"]);
        Assert.Equal("42", records[1]["Company"]);
        Assert.Equal("false", records[1]["Active"]);
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
    public void MappingProfilesRoundTripThroughTypedMacroStepsWithoutRecordData()
    {
        var profile = new PdfDataMergeProfile("Invoices",
            [new PdfDataMergeFieldMapping("Customer", "invoice.customer")],
            "invoice-{{Number}}.pdf");

        PdfMacroStep step = profile.ToMacroStep();
        var macro = new PdfMacro("Invoice merge", [step]);
        PdfMacro restoredMacro = PdfMacro.FromJson(macro.ToJson());
        PdfDataMergeProfile restored = PdfDataMergeProfile.FromMacroStep(
            Assert.Single(restoredMacro.Steps));

        Assert.Equal(PdfMacroOperation.DataMerge, step.Operation);
        Assert.Equal("Invoices", restored.Name);
        Assert.Equal("invoice.customer", Assert.Single(restored.Mappings).TargetField);
        Assert.DoesNotContain("Ada", macro.ToJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => PdfDataMergeProfile.FromMacroStep(
            new PdfMacroStep(PdfMacroOperation.Save)));
    }

    [Fact]
    public void MappingProfilesApplyReusableDefaultsToMissingValues()
    {
        var profile = new PdfDataMergeProfile("Invoices", [
            new PdfDataMergeFieldMapping("Country", "invoice.country", "US"),
            new PdfDataMergeFieldMapping("Customer", "invoice.customer")],
            "invoice-{{Number}}.pdf");

        PdfDataMergeProfile restored = PdfDataMergeProfile.FromJson(profile.ToJson());
        PdfDataMergeMappedRecord mapped = restored.Map(new Dictionary<string, string?>
        {
            ["Customer"] = "Ada",
            ["Number"] = "1001"
        });

        Assert.Equal(["US", "Ada"],
            mapped.FormData.Fields.Select(field => field.Values.Single()));
        Assert.Equal("US", restored.Mappings[0].DefaultValue);
    }

    [Fact]
    public void MappingProfilesFormatNumbersAndDatesWithSavedCultures()
    {
        var profile = new PdfDataMergeProfile("Invoices", [
            new PdfDataMergeFieldMapping("Total", "invoice.total", ValueKind:
                PdfDataMergeValueKind.Number, Format: "N2", CultureName: "de-DE"),
            new PdfDataMergeFieldMapping("Due", "invoice.due", ValueKind:
                PdfDataMergeValueKind.Date, Format: "yyyy-MM-dd", CultureName: "en-US")],
            "invoice.pdf");

        PdfDataMergeProfile restored = PdfDataMergeProfile.FromJson(profile.ToJson());
        PdfDataMergeMappedRecord mapped = restored.Map(new Dictionary<string, string?>
        {
            ["Total"] = "1234,5",
            ["Due"] = "3/14/2027"
        });

        Assert.Equal(["1.234,50", "2027-03-14"],
            mapped.FormData.Fields.Select(field => field.Values.Single()));
        Assert.Throws<FormatException>(() => restored.Map(new Dictionary<string, string?>
        {
            ["Total"] = "not a number",
            ["Due"] = "3/14/2027"
        }));
    }

    [Fact]
    public void MappingProfilesConditionallyIncludeFieldsWithoutSavingRecordValues()
    {
        var profile = new PdfDataMergeProfile("Invitations", [
            new PdfDataMergeFieldMapping("Guest", "guest.name"),
            new PdfDataMergeFieldMapping("Meal", "guest.meal",
                IncludeWhenField: "Attending", IncludeWhenValue: "yes")],
            "invitation.pdf");
        PdfDataMergeProfile restored = PdfDataMergeProfile.FromJson(profile.ToJson());

        PdfDataMergeMappedRecord attending = restored.Map(
            new Dictionary<string, string?>
            {
                ["Guest"] = "Ada", ["Meal"] = "Vegetarian", ["Attending"] = "yes"
            });
        PdfDataMergeMappedRecord absent = restored.Map(
            new Dictionary<string, string?>
            {
                ["Guest"] = "Grace", ["Meal"] = "Standard", ["Attending"] = "no"
            });

        Assert.Equal(["guest.name", "guest.meal"],
            attending.FormData.Fields.Select(field => field.Name));
        Assert.Equal(["guest.name"], absent.FormData.Fields.Select(field => field.Name));
        Assert.DoesNotContain("Vegetarian", profile.ToJson(), StringComparison.Ordinal);
        Assert.Throws<ArgumentException>(() => new PdfDataMergeProfile("Invalid", [
            new PdfDataMergeFieldMapping("Meal", "guest.meal",
                IncludeWhenField: "Attending")], "invalid.pdf"));
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
    public void FormBatchReplacesLatin1PageTextPlaceholdersAndReportsMissingTargets()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder()
            .AddPage(300, 200, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100)
                .ShowPositionedLatin1Text(["Hello {{Name}}", "!"], [0])
                .EndText()).Build());
        var profile = new PdfDataMergeProfile("Letters",
            [new PdfDataMergeFieldMapping("Customer", "Name",
                TargetKind: PdfDataMergeTargetKind.TextPlaceholder)],
            "letter.pdf");

        PdfDataMergeDocumentResult result = Assert.Single(PdfDataMerge.RunFormBatch(
            template, [new Dictionary<string, string?> { ["Customer"] = "Ada" }], profile));

        Assert.True(result.Succeeded);
        Assert.Equal("Hello Ada!", PdfStructuredExport.ToPlainText(
            PdfDocument.Open(result.Data!.Value)));
        Assert.Equal("Hello {{Name}}!", PdfStructuredExport.ToPlainText(template));
        PdfDataMergePreview preview = PdfDataMerge.PreviewFormRecord(template,
            new Dictionary<string, string?> { ["Customer"] = "Ada" }, profile);
        Assert.True(preview.CanGenerate);
        Assert.Equal(1, Assert.Single(preview.TextPlaceholders).OccurrenceCount);
        Assert.DoesNotContain("Ada", JsonSerializer.Serialize(preview),
            StringComparison.Ordinal);

        var missing = new PdfDataMergeProfile("Missing",
            [new PdfDataMergeFieldMapping("Customer", "Unknown",
                TargetKind: PdfDataMergeTargetKind.TextPlaceholder)], "missing.pdf");
        PdfDataMergeDocumentResult failed = Assert.Single(PdfDataMerge.RunFormBatch(
            template, [new Dictionary<string, string?> { ["Customer"] = "Ada" }], missing));
        Assert.False(failed.Succeeded);
        Assert.Contains("{{Unknown}}", failed.Error, StringComparison.Ordinal);
        Assert.False(PdfDataMerge.PreviewFormRecord(template,
            new Dictionary<string, string?> { ["Customer"] = "Ada" }, missing).CanGenerate);
    }

    [Fact]
    public void FormRecordPreviewReportsMappedTypesAndBlockedFieldsWithoutValues()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "customer.name", 20, 20, 140, 24)
            .AddCheckBox(0, "customer.active", 20, 60, 20, 20).Build());
        var validProfile = new PdfDataMergeProfile("Customers", [
            new PdfDataMergeFieldMapping("Name", "customer.name"),
            new PdfDataMergeFieldMapping("Active", "customer.active")],
            "customer-{{Number}}.pdf");
        var record = new Dictionary<string, string?>
        {
            ["Name"] = "Ada",
            ["Active"] = "true",
            ["Number"] = "1"
        };

        PdfDataMergePreview valid = PdfDataMerge.PreviewFormRecord(
            template, record, validProfile);

        Assert.True(valid.CanGenerate);
        Assert.Equal("customer-1.pdf", valid.OutputFileName);
        Assert.Equal([PdfFormFieldKind.Text, PdfFormFieldKind.Button],
            valid.Fields.Select(field => field.FieldKind));
        Assert.DoesNotContain("Ada", JsonSerializer.Serialize(valid), StringComparison.Ordinal);

        var blockedProfile = new PdfDataMergeProfile("Customers",
            [new PdfDataMergeFieldMapping("Name", "missing.field")],
            "customer-{{Number}}.pdf");
        PdfDataMergePreview blocked = PdfDataMerge.PreviewFormRecord(
            template, record, blockedProfile);

        Assert.False(blocked.CanGenerate);
        Assert.Equal(PdfFormDataMatchStatus.Unmatched, Assert.Single(blocked.Fields).Status);
        Assert.Contains("missing.field", blocked.Error, StringComparison.Ordinal);
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

    [Fact]
    public void CombinedOutputIncludesSuccessfulRecordsInOrderAndSkipsFailures()
    {
        static byte[] Document(string text) => new PdfDocumentBuilder()
            .AddPage(200, 200, new PdfContentStreamBuilder().BeginText()
                .SetFont(PdfStandardFont.Helvetica, 12).MoveText(20, 100)
                .ShowLatin1Text(text).EndText()).Build();
        PdfDataMergeDocumentResult[] results =
        [
            new(2, "third.pdf", Document("Third"), null),
            new(1, "bad.pdf", null, "Bad record"),
            new(0, "first.pdf", Document("First"), null)
        ];

        PdfDataMergeCombinedResult combined = PdfDataMerge.CombineSuccessful(results);
        string text = PdfStructuredExport.ToPlainText(PdfDocument.Open(combined.Document));

        Assert.Equal([0, 2], combined.IncludedRecordIndices);
        Assert.Equal("First\fThird", text);
        Assert.Throws<InvalidOperationException>(() => PdfDataMerge.CombineSuccessful(
            [new PdfDataMergeDocumentResult(0, null, null, "Bad record")]));
    }

    private static byte[] SharedStringWorkbook()
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            Write("xl/workbook.xml",
                "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Records\" sheetId=\"1\" r:id=\"rId1\"/></sheets></workbook>");
            Write("xl/_rels/workbook.xml.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Target=\"worksheets/sheet1.xml\"/></Relationships>");
            Write("xl/sharedStrings.xml",
                "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Name</t></si><si><t>Company</t></si><si><t>Active</t></si><si><t>Ada</t></si></sst>");
            Write("xl/worksheets/sheet1.xml",
                "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
                "<row><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>2</v></c></row>" +
                "<row><c r=\"A2\" t=\"s\"><v>3</v></c><c r=\"C2\" t=\"b\"><v>1</v></c></row>" +
                "<row><c r=\"A3\" t=\"inlineStr\"><is><t>Grace</t></is></c><c r=\"B3\"><v>42</v></c><c r=\"C3\" t=\"b\"><v>0</v></c></row>" +
                "</sheetData></worksheet>");

            void Write(string name, string content)
            {
                using Stream stream = archive.CreateEntry(name).Open();
                using var writer = new StreamWriter(stream, new UTF8Encoding(false));
                writer.Write(content);
            }
        }
        return output.ToArray();
    }
}
