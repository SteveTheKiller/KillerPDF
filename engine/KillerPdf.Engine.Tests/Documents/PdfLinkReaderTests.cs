using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.CrossReference;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
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
    public void ReadPage_ResolvesLinksStoredInObjectStreams()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage(300, 400).AddBlankPage(300, 400)
            .AddUriLink(0, 10, 20, 80, 15, "https://example.com")
            .AddPageLink(0, 100, 40, 50, 20, 1)
            .Build());
        byte[] packed = PdfDocumentWriter.Write(source, new PdfDocumentWriteOptions
        {
            CrossReferenceFormat = PdfCrossReferenceFormat.Stream,
            UseObjectStreams = true,
            CompressStructuralStreams = true
        });
        PdfDocument reopened = PdfDocument.Open(packed);

        IReadOnlyList<PdfLinkInfo> links = PdfLinkReader.ReadPage(reopened, 0);

        Assert.Equal(2, links.Count);
        Assert.Equal("https://example.com/", links[0].Uri);
        Assert.Equal(1, links[1].DestinationPageIndex);
        Assert.Contains(links, link => link.ObjectNumber.HasValue
            && reopened.CrossReferences[link.ObjectNumber.Value].Type
                == PdfCrossReferenceEntryType.Compressed);
    }
}
