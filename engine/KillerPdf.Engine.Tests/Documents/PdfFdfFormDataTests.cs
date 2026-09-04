using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFdfFormDataTests
{
    [Fact]
    public void RoundTripsAnEmbeddedSourcePdfThroughAFileSpecification()
    {
        byte[] pdf = new PdfDocumentBuilder().AddBlankPage().Build();
        var source = new PdfFormDataSet
        {
            SourcePdfPath = "forms/source.pdf",
            EmbeddedSourcePdf = pdf,
            Fields = [new PdfFormDataField { Name = "name", Values = ["Alice"] }]
        };

        PdfFormDataSet result = PdfFdfFormData.Read(PdfFdfFormData.Write(source));

        Assert.Equal("forms/source.pdf", result.SourcePdfPath);
        Assert.Equal(pdf, result.EmbeddedSourcePdf!.Value.ToArray());
        Assert.Single(result.Fields);
        Assert.Single(PdfPageBoxInformation.Read(
            PdfDocument.Open(result.EmbeddedSourcePdf.Value)));
    }

    [Fact]
    public void WriteRejectsInvalidEmbeddedSourceData()
    {
        Assert.Throws<ArgumentException>(() => PdfFdfFormData.Write(new PdfFormDataSet
        {
            EmbeddedSourcePdf = "not a pdf"u8.ToArray()
        }));
    }

    [Fact]
    public void WriteAndReadRoundTripUnicodeEmptyAndMultipleValues()
    {
        var source = new PdfFormDataSet
        {
            SourcePdfPath = "forms/Résumé.pdf",
            Fields =
            [
                new PdfFormDataField { Name = "person.name", Values = ["Zoë"] },
                new PdfFormDataField { Name = "notes", Values = [""] },
                new PdfFormDataField { Name = "colors", Values = ["red", "blue"] }
            ]
        };

        PdfFormDataSet result = PdfFdfFormData.Read(PdfFdfFormData.Write(source));

        Assert.Equal(source.SourcePdfPath, result.SourcePdfPath);
        Assert.Equal(source.Fields.Select(field => field.Name), result.Fields.Select(field => field.Name));
        Assert.Equal(source.Fields.Select(field => field.Values.ToArray()),
            result.Fields.Select(field => field.Values.ToArray()));
        Assert.False(result.ContainsJavaScript);
    }

    [Fact]
    public void ReadRejectsNonFdfAndBrokenReferences()
    {
        Assert.Throws<InvalidOperationException>(() => PdfFdfFormData.Read("%PDF-1.7\n"u8.ToArray()));
        byte[] valid = PdfFdfFormData.Write(new PdfFormDataSet());
        string brokenText = Encoding.Latin1.GetString(valid).Replace("/Root 1 0 R", "/Root 9 0 R");

        Assert.Throws<InvalidOperationException>(() =>
            PdfFdfFormData.Read(Encoding.Latin1.GetBytes(brokenText)));
    }

    [Fact]
    public void ReadReportsSignedDataAndIncrementalDifferencesWithoutRewritingThem()
    {
        PdfFormDataSet result = PdfFdfFormData.Read(FdfWithEntries(
            "/Fields [] /Sig << /Type /Sig >> /Differences [<0102>]"));

        Assert.True(result.ContainsSignature);
        Assert.True(result.ContainsIncrementalDifferences);
        Assert.Throws<NotSupportedException>(() => PdfFdfFormData.Write(result));
        Assert.Throws<NotSupportedException>(() => PdfFdfFormData.Write(
            result with { ContainsSignature = false }));
        Assert.Throws<NotSupportedException>(() => PdfXfdfFormData.Write(result));
    }

    [Fact]
    public void WriteCanExportAnOrderedFieldSelection()
    {
        var source = new PdfFormDataSet
        {
            SourcePdfPath = "source.pdf",
            Fields = [
                new PdfFormDataField { Name = "first", Values = ["1"] },
                new PdfFormDataField { Name = "second", Values = ["2"] }]
        };

        PdfFormDataSet selected = PdfFdfFormData.Read(
            PdfFdfFormData.Write(source, ["second"]));

        Assert.Equal("second", Assert.Single(selected.Fields).Name);
        Assert.Equal("source.pdf", selected.SourcePdfPath);
        Assert.Throws<KeyNotFoundException>(() =>
            PdfFdfFormData.Write(source, ["missing"]));
    }

    [Fact]
    public void WriteCanSelectFieldsAndAnnotationPagesTogether()
    {
        var source = SelectionSource("Text");

        PdfFormDataSet selected = PdfFdfFormData.Read(
            PdfFdfFormData.Write(source, ["second"], [2]));

        Assert.Equal("second", Assert.Single(selected.Fields).Name);
        Assert.Equal(2, Assert.Single(selected.Annotations).PageIndex);
        Assert.Throws<ArgumentException>(() =>
            source.SelectAnnotationPages([2, 2]));
    }

    [Fact]
    public void WriteAndReadRoundTripAnnotationsAndReplies()
    {
        var source = new PdfFormDataSet
        {
            Annotations =
            [
                new PdfFormDataAnnotation
                {
                    Subtype = "Highlight", PageIndex = 1, Rectangle = [10.5, 20, 30, 40],
                    Name = "review-1", Contents = "Replace this", Author = "Reviewer",
                    Subject = "Translation", Color = "#FFD319", Opacity = 0.5,
                    CreationDate = "D:20260904120000Z", ModifiedDate = "D:20260904123000Z"
                },
                new PdfFormDataAnnotation
                {
                    Subtype = "Text", PageIndex = 1, Rectangle = [30, 40, 50, 60],
                    Name = "reply-1", Contents = "Done", ReplyToName = "review-1"
                }
            ]
        };

        PdfFormDataSet result = PdfFdfFormData.Read(PdfFdfFormData.Write(source));

        Assert.Equal(source.Annotations.Count, result.Annotations.Count);
        for (int index = 0; index < source.Annotations.Count; index++)
        {
            Assert.Equal(source.Annotations[index] with { Rectangle = [] },
                result.Annotations[index] with { Rectangle = [] });
            Assert.Equal(source.Annotations[index].Rectangle, result.Annotations[index].Rectangle);
        }
    }

    [Fact]
    public void ResolvesLocalSourceReferencesRelativeToTheInterchangeFile()
    {
        var relative = new PdfFormDataSet { SourcePdfPath = "forms/source.pdf" };
        string interchange = Path.Combine(Path.GetTempPath(), "case", "values.fdf");

        string? resolved = PdfFormDataSourceResolver.Resolve(relative, interchange);

        Assert.Equal(Path.GetFullPath(Path.Combine(
            Path.GetTempPath(), "case", "forms", "source.pdf")), resolved);
        Assert.Throws<NotSupportedException>(() => PdfFormDataSourceResolver.Resolve(
            new PdfFormDataSet { SourcePdfPath = "https://example.com/source.pdf" }, interchange));
        Assert.Null(PdfFormDataSourceResolver.Resolve(new PdfFormDataSet(), interchange));
    }


    private static PdfFormDataSet SelectionSource(string subtype) => new()
    {
        Fields =
        [
            new PdfFormDataField { Name = "first", Values = ["1"] },
            new PdfFormDataField { Name = "second", Values = ["2"] }
        ],
        Annotations =
        [
            new PdfFormDataAnnotation { Subtype = subtype, PageIndex = 0, Rectangle = [0, 0, 10, 10] },
            new PdfFormDataAnnotation { Subtype = subtype, PageIndex = 2, Rectangle = [0, 0, 10, 10] }
        ]
    };

    private static byte[] FdfWithEntries(string entries)
    {
        string body = $"1 0 obj\n<< /Type /Catalog /FDF << {entries} >> >>\nendobj\n";
        int xref = Encoding.ASCII.GetByteCount("%FDF-1.2\n" + body);
        string source = "%FDF-1.2\n" + body
            + "xref\n0 2\n0000000000 65535 f \n0000000009 00000 n \n"
            + $"trailer\n<< /Root 1 0 R /Size 2 >>\nstartxref\n{xref}\n%%EOF\n";
        return Encoding.ASCII.GetBytes(source);
    }
}
