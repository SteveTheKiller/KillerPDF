using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfPageLabelReaderTests
{
    [Fact]
    public void ReadResolvesRangesPrefixesAndDefaultLabels()
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < 8; index++) builder.AddBlankPage();
        PdfDocument document = PdfDocument.Open(builder
            .AddPageLabelRange(2, PdfPageLabelStyle.LowerRoman, "Intro ", 3)
            .AddPageLabelRange(5, PdfPageLabelStyle.UpperLetters, "A-", 25)
            .Build());

        Assert.Equal(["1", "2", "Intro iii", "Intro iv", "Intro v", "A-Y", "A-Z", "A-AA"],
            PdfPageLabelReader.Read(document));
    }

    [Fact]
    public void ReadUsesDecimalLabelsWhenNumberTreeIsAbsent()
    {
        PdfDocument document = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().AddBlankPage().Build());

        Assert.Equal(["1", "2"], PdfPageLabelReader.Read(document));
    }
}
