using System.Text;
using System.Xml;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfXfdfFormDataTests
{
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

        PdfFormDataSet result = PdfXfdfFormData.Read(PdfXfdfFormData.Write(source));

        Assert.Equal(source.SourcePdfPath, result.SourcePdfPath);
        Assert.Equal(source.Fields.Select(field => field.Name), result.Fields.Select(field => field.Name));
        Assert.Equal(source.Fields.Select(field => field.Values.ToArray()),
            result.Fields.Select(field => field.Values.ToArray()));
        Assert.False(result.ContainsJavaScript);
    }

    [Fact]
    public void ReadCombinesNestedNamesAndReportsScriptsWithoutExecutingThem()
    {
        byte[] source = Encoding.UTF8.GetBytes("""
            <?xml version="1.0" encoding="UTF-8"?>
            <xfdf xmlns="http://ns.adobe.com/xfdf/">
              <fields><field name="person"><field name="name"><value>Ada</value></field></field></fields>
              <javascript>app.alert('ignored')</javascript>
            </xfdf>
            """);

        PdfFormDataSet result = PdfXfdfFormData.Read(source);

        PdfFormDataField field = Assert.Single(result.Fields);
        Assert.Equal("person.name", field.Name);
        Assert.Equal(["Ada"], field.Values);
        Assert.True(result.ContainsJavaScript);
    }

    [Fact]
    public void ReadRejectsDtdAndWrongRootNamespace()
    {
        Assert.Throws<XmlException>(() => PdfXfdfFormData.Read(
            "<!DOCTYPE xfdf [<!ENTITY x 'bad'>]><xfdf xmlns='http://ns.adobe.com/xfdf/'>&x;</xfdf>"u8.ToArray()));
        Assert.Throws<InvalidOperationException>(() =>
            PdfXfdfFormData.Read("<xfdf/>"u8.ToArray()));
    }

    [Fact]
    public void WriteCanExportAnOrderedFieldSelection()
    {
        var source = new PdfFormDataSet
        {
            Fields = [
                new PdfFormDataField { Name = "first", Values = ["1"] },
                new PdfFormDataField { Name = "second", Values = ["2"] }]
        };

        PdfFormDataSet selected = PdfXfdfFormData.Read(
            PdfXfdfFormData.Write(source, ["second", "first"]));

        Assert.Equal(["second", "first"], selected.Fields.Select(field => field.Name));
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
                    Subtype = "highlight", PageIndex = 2, Rectangle = [10.5, 20, 30.25, 40],
                    Name = "review-1", Contents = "Replace this translation", Author = "Reviewer",
                    Subject = "Terminology", Color = "#FFD319", Opacity = 0.65,
                    CreationDate = "D:20260904120000Z", ModifiedDate = "D:20260904123000Z"
                },
                new PdfFormDataAnnotation
                {
                    Subtype = "text", PageIndex = 2, Rectangle = [30, 40, 50, 60],
                    Name = "reply-1", Contents = "Updated", ReplyToName = "review-1"
                }
            ]
        };

        PdfFormDataSet result = PdfXfdfFormData.Read(PdfXfdfFormData.Write(source));

        Assert.Equal(source.Annotations.Count, result.Annotations.Count);
        for (int index = 0; index < source.Annotations.Count; index++)
        {
            Assert.Equal(source.Annotations[index] with { Rectangle = [] },
                result.Annotations[index] with { Rectangle = [] });
            Assert.Equal(source.Annotations[index].Rectangle, result.Annotations[index].Rectangle);
        }
    }

    [Fact]
    public void ReadRejectsInvalidAnnotationGeometryAndOpacity()
    {
        Assert.Throws<InvalidOperationException>(() => PdfXfdfFormData.Read(Encoding.UTF8.GetBytes("""
            <xfdf xmlns="http://ns.adobe.com/xfdf/"><annots>
              <text page="0" rect="10,20,5,30" opacity="2" />
            </annots></xfdf>
            """)));
    }
}
