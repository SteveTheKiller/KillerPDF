using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFormDataImporterTests
{
    [Fact]
    public void PreviewReportsMatchedReadOnlyAndUnmatchedFields()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "name", 10, 10, 100, 20)
            .AddTextField(0, "locked", 10, 40, 100, 20, options:
                new PdfTextFieldOptions { ReadOnly = true })
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "name", Values = ["Ada"] },
            new PdfFormDataField { Name = "locked", Values = ["No"] },
            new PdfFormDataField { Name = "missing", Values = ["No"] }
        ] };

        IReadOnlyList<PdfFormDataMatch> result = PdfFormDataImporter.Preview(document, data);

        Assert.Equal([PdfFormDataMatchStatus.Matched, PdfFormDataMatchStatus.ReadOnly,
            PdfFormDataMatchStatus.Unmatched], result.Select(match => match.Status));
    }

    [Fact]
    public void CreateReportSummarizesStatusesAndOmitsImportedValues()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "required", 10, 10, 100, 20, options:
                new PdfTextFieldOptions { Required = true })
            .AddTextField(0, "excluded", 10, 40, 100, 20, options:
                new PdfTextFieldOptions { NoExport = true, ReadOnly = true })
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "required", Values = ["private value"] },
            new PdfFormDataField { Name = "excluded", Values = ["blocked value"] },
            new PdfFormDataField { Name = "missing", Values = ["unknown value"] }
        ] };

        PdfFormDataImportReport report = PdfFormDataImporter.CreateReport(document, data);
        string json = report.ToJson();

        Assert.Equal(3, report.TotalFieldCount);
        Assert.Equal(1, report.ApplicableFieldCount);
        Assert.Equal(2, report.BlockedFieldCount);
        Assert.Equal(1, report.RequiredFieldCount);
        Assert.Equal(1, report.NoExportFieldCount);
        Assert.Contains("\"status\":\"readOnly\"", json);
        Assert.DoesNotContain("private value", json);
        Assert.DoesNotContain("blocked value", json);
        Assert.DoesNotContain("unknown value", json);
    }

    [Fact]
    public void ApplyUpdatesTextChoiceCheckBoxAndRadioValues()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "name", 10, 10, 100, 20)
            .AddComboBox(0, "region", 10, 40, 100, 20, ["US", "CA"])
            .AddCheckBox(0, "approved", 10, 70, 20, 20, false, "Accepted")
            .AddRadioGroup("priority", [new PdfRadioButtonOption(0, 10, 100, 20, 20, "High"),
                new PdfRadioButtonOption(0, 40, 100, 20, 20, "Low")])
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "name", Values = ["Ada"] },
            new PdfFormDataField { Name = "region", Values = ["CA"] },
            new PdfFormDataField { Name = "approved", Values = ["Accepted"] },
            new PdfFormDataField { Name = "priority", Values = ["Low"] }
        ] };

        PdfDocument reopened = PdfDocument.Open(PdfFormDataImporter.Apply(document, data));
        Dictionary<string, PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0)
            .GroupBy(widget => widget.FieldName)
            .ToDictionary(group => group.Key, group => group.First());

        Assert.Equal("Ada", widgets["name"].Value);
        Assert.Equal("CA", widgets["region"].Value);
        Assert.Equal("/Accepted", widgets["approved"].Value);
        Assert.Equal("/Low", widgets["priority"].Value);
    }
}
