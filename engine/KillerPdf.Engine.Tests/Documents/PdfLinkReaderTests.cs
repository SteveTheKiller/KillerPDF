using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfLinkReaderTests
{
    [Fact]
    public void ReadPage_ResolvesUriPageAndNamedDestinationLinks()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400)
            .AddNamedDestination("résumé", 1, PdfDestination.FitWidth(350))
            .AddUriLink(0, 10, 20, 80, 15, "https://example.com/résumé")
            .AddPageLink(0, 100, 40, 50, 20, 1)
            .AddNamedDestinationLink(0, 30, 90, 60, 25, "résumé")
            .Build());

        IReadOnlyList<PdfLinkInfo> links = PdfLinkReader.ReadPage(document, 0);

        Assert.Equal(3, links.Count);
        Assert.Equal("https://example.com/r%C3%A9sum%C3%A9", links[0].Uri);
        Assert.Equal((10d, 20d, 90d, 35d),
            (links[0].Left, links[0].Bottom, links[0].Right, links[0].Top));
        Assert.Equal(1, links[1].DestinationPageIndex);
        Assert.Equal("résumé", links[2].NamedDestination);
        Assert.Equal(1, links[2].DestinationPageIndex);
        Assert.Equal([0, 1, 2], links.Select(link => link.AnnotationIndex));
    }

    [Fact]
    public void ReadPage_ReturnsEmptyListWithoutAnnotations()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Empty(PdfLinkReader.ReadPage(document, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfLinkReader.ReadPage(document, 1));
    }

    [Fact]
    public void ReusableReader_ResolvesLinksAcrossPages()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400)
            .AddBlankPage(300, 400).AddBlankPage(300, 400)
            .AddNamedDestination("end", 3, PdfDestination.FitWidth(350))
            .AddUriLink(0, 10, 20, 80, 15, "https://example.com")
            .AddPageLink(2, 20, 30, 80, 15, 0)
            .AddNamedDestinationLink(3, 30, 40, 80, 15, "end")
            .Build());
        var reader = new PdfLinkReader(document);

        Assert.Equal("https://example.com/", Assert.Single(reader.ReadPage(0)).Uri);
        Assert.Empty(reader.ReadPage(1));
        Assert.Equal(0, Assert.Single(reader.ReadPage(2)).DestinationPageIndex);
        PdfLinkInfo named = Assert.Single(reader.ReadPage(3));
        Assert.Equal("end", named.NamedDestination);
        Assert.Equal(3, named.DestinationPageIndex);
        Assert.Equal("https://example.com/", Assert.Single(reader.ReadPage(0)).Uri);
        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadPage(4));
    }
}
