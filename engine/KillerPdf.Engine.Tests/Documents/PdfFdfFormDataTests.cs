using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfFdfFormDataTests
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
}
