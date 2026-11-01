using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfAnnotationDateRoundTripTests
{
    [Theory]
    [InlineData("D:2026", "D:20260101000000+00'00'")]
    [InlineData("D:202611", "D:20261101000000+00'00'")]
    [InlineData("D:20261101", "D:20261101000000+00'00'")]
    [InlineData("D:2026110110", "D:20261101100000+00'00'")]
    [InlineData("D:202611011020", "D:20261101102000+00'00'")]
    [InlineData("D:20261101102030Z", "D:20261101102030+00'00'")]
    [InlineData("2026-11-01T10:20:30-08:00", "D:20261101182030+00'00'")]
    public void ImportRetainsPartialPdfAndIsoDateSupport(string date, string expected)
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var data = new PdfFormDataSet { Annotations = [new PdfFormDataAnnotation
        {
            Subtype = "text", PageIndex = 0, Rectangle = [10, 10, 34, 34],
            Contents = "dated", CreationDate = date, ModifiedDate = date
        }] };
        PdfDocument imported = PdfDocument.Open(PdfFormDataImporter.Apply(blank,
            PdfXfdfFormData.Read(PdfXfdfFormData.Write(data))));
        var actual = Assert.Single(PdfFormDataExporter.Export(imported).Annotations);
        Assert.Equal(expected, actual.CreationDate);
        Assert.Equal(expected, actual.ModifiedDate);
    }

    [Theory]
    [InlineData(-480)]
    [InlineData(0)]
    [InlineData(330)]
    public void ExportedNoteDatesSurviveXfdfImport(int offsetMinutes)
    {
        PdfDocument blank = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());
        var date = new DateTimeOffset(2026, 11, 1, 10, 20, 30, TimeSpan.FromMinutes(offsetMinutes));
        PdfDocument source = PdfDocument.Open(new PdfIncrementalAnnotationEditor(blank)
            .AddTextNote(0, 10, 10, "date round trip", annotationMetadata: new PdfAnnotationMetadata
            { CreationDate = date, ModificationDate = date }, name: "dated-note").Build());
        PdfFormDataSet exported = PdfFormDataExporter.Export(source);
        PdfDocument imported = PdfDocument.Open(PdfFormDataImporter.Apply(blank,
            PdfXfdfFormData.Read(PdfXfdfFormData.Write(exported))));
        var actual = Assert.Single(PdfFormDataExporter.Export(imported).Annotations);
        var expected = Assert.Single(exported.Annotations);
        Assert.Equal(expected.CreationDate, actual.CreationDate);
        Assert.Equal(expected.ModifiedDate, actual.ModifiedDate);
        Assert.Equal("date round trip", actual.Contents);
    }
}
