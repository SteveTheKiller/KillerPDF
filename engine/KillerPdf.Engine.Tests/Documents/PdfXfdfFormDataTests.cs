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
}
