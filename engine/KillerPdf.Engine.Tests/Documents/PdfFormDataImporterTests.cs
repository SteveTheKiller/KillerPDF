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

    [Fact]
    public void ApplyCanFlattenImportedValues()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "name", 10, 10, 100, 20)
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "name", Values = ["Ada"] }
        ] };

        PdfDocument reopened = PdfDocument.Open(PdfFormDataImporter.Apply(
            document, data, PdfFormDataImportOutputMode.Flattened));

        Assert.Empty(PdfFormWidgetReader.ReadPage(reopened, 0));
        Assert.Contains("Ada", new PdfPageContentReader(reopened).Read(0).Text);
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfFormDataImporter.Apply(
            document, data, (PdfFormDataImportOutputMode)int.MaxValue));
    }

    [Fact]
    public void PreviewRejectsValuesThatGenerationCannotApply()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "code", 10, 10, 100, 20,
                options: new PdfTextFieldOptions { MaximumLength = 3 })
            .AddComboBox(0, "region", 10, 40, 100, 20, ["US", "CA"])
            .AddCheckBox(0, "approved", 10, 70, 20, 20, false, "Accepted")
            .AddRadioGroup("priority", [
                new PdfRadioButtonOption(0, 10, 100, 20, 20, "High"),
                new PdfRadioButtonOption(0, 40, 100, 20, 20, "Low")])
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "code", Values = ["LONG"] },
            new PdfFormDataField { Name = "region", Values = ["EU"] },
            new PdfFormDataField { Name = "approved", Values = ["maybe"] },
            new PdfFormDataField { Name = "priority", Values = ["Medium"] }
        ] };

        IReadOnlyList<PdfFormDataMatch> result = PdfFormDataImporter.Preview(document, data);

        Assert.All(result, match =>
            Assert.Equal(PdfFormDataMatchStatus.InvalidValue, match.Status));
        Assert.Throws<InvalidOperationException>(() => PdfFormDataImporter.Apply(document, data));
    }

    [Fact]
    public void PreviewAcceptsEveryRadioOptionAndExplicitCheckboxFalseValues()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddCheckBox(0, "approved", 10, 70, 20, 20, true, "Accepted")
            .AddRadioGroup("priority", [
                new PdfRadioButtonOption(0, 10, 100, 20, 20, "High"),
                new PdfRadioButtonOption(0, 40, 100, 20, 20, "Low")])
            .Build());
        var data = new PdfFormDataSet { Fields =
        [
            new PdfFormDataField { Name = "approved", Values = ["false"] },
            new PdfFormDataField { Name = "priority", Values = ["Low"] }
        ] };

        IReadOnlyList<PdfFormDataMatch> preview = PdfFormDataImporter.Preview(document, data);
        PdfDocument reopened = PdfDocument.Open(PdfFormDataImporter.Apply(document, data));
        Dictionary<string, PdfFormWidgetInfo> widgets = PdfFormWidgetReader.ReadPage(reopened, 0)
            .GroupBy(widget => widget.FieldName)
            .ToDictionary(group => group.Key, group => group.First());

        Assert.All(preview, match => Assert.Equal(PdfFormDataMatchStatus.Matched, match.Status));
        Assert.Equal("/Off", widgets["approved"].Value);
        Assert.Equal("/Low", widgets["priority"].Value);
    }

    [Fact]
    public void ApplyImportsHighlightAndNoteReplyMetadata()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(200, 200).Build());
        var data = new PdfFormDataSet
        {
            Annotations =
            [
                new PdfFormDataAnnotation
                {
                    Subtype = "highlight", PageIndex = 0,
                    Rectangle = [10, 20, 80, 35], Name = "review-1",
                    Contents = "Replace this", Author = "Reviewer",
                    Subject = "Translation", Color = "#FFD319", Opacity = 0.65,
                    CreationDate = "D:20260904120000Z",
                    ModifiedDate = "D:20260904123000Z"
                },
                new PdfFormDataAnnotation
                {
                    Subtype = "underline", PageIndex = 0,
                    Rectangle = [10, 50, 80, 65], Name = "review-2",
                    Contents = "Check this", ReplyToName = "review-1"
                },
                new PdfFormDataAnnotation
                {
                    Subtype = "text", PageIndex = 0,
                    Rectangle = [90, 20, 114, 44], Name = "reply-1",
                    Contents = "Updated", ReplyToName = "review-1"
                }
            ]
        };

        PdfDocument reopened = PdfDocument.Open(PdfFormDataImporter.Apply(document, data));
        IReadOnlyList<PdfCommentInfo> comments = PdfCommentReader.Read(reopened);

        Assert.Equal(3, comments.Count);
        Assert.Equal("review-1", comments[0].Name);
        Assert.Equal("Reviewer", comments[0].Author);
        Assert.Equal("Translation", comments[0].Subject);
        Assert.Equal("review-2", comments[1].Name);
        Assert.Equal(comments[0].ObjectNumber, comments[1].ReplyToObjectNumber);
        Assert.Equal("reply-1", comments[2].Name);
        Assert.Equal(comments[0].ObjectNumber, comments[2].ReplyToObjectNumber);
        Assert.Throws<NotSupportedException>(() => PdfFormDataImporter.Apply(document,
            new PdfFormDataSet { Annotations = [data.Annotations[0] with { Subtype = "stamp" }] }));
    }
}
