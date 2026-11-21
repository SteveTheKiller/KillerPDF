using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfTableOfContentsWriterTests
{
    [Fact]
    public void WriteInsertsStyledPaginatedContentsWithShiftedLinks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage()
            .AddPageLabelRange(0, PdfPageLabelStyle.LowerRoman)
            .AddPageLabelRange(2, PdfPageLabelStyle.Decimal, "A-", 3)
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.2, 0.3, 0.4),
                Destination = PdfDestination.FitWidth(700)
            })
            .AddBookmark("Section", 2, level: 1)
            .AddBookmark("Next", 1)
            .Build());

        PdfTableOfContentsWriteResult result = PdfTableOfContentsWriter.Write(source,
            new PdfTableOfContentsWriteOptions
            {
                Title = "Contents",
                PageWidth = 300,
                PageHeight = 150,
                TopMargin = 20,
                BottomMargin = 20,
                LeftMargin = 20,
                RightMargin = 20,
                TitleFontSize = 14,
                TitleGap = 15,
                EntryFontSize = 10,
                RowHeight = 35,
                IndentWidth = 12
            });
        PdfDocument output = PdfDocument.Open(result.Document);

        Assert.Equal(2, result.InsertedPageCount);
        Assert.Equal(3, result.EntryCount);
        Assert.Equal(5, new PdfIncrementalPageEditor(output).PageCount);
        string text = PdfStructuredExport.ToPlainText(output, [0, 1]);
        Assert.Contains("Contents", text);
        Assert.Contains("Chapter", text);
        Assert.Contains("A-3", text);
        Assert.Equal([2, 4], PdfLinkReader.ReadPage(output, 0)
            .Select(link => link.DestinationPageIndex));
        Assert.Equal(3, Assert.Single(PdfLinkReader.ReadPage(output, 1))
            .DestinationPageIndex);
        Assert.All(PdfLinkReader.ReadPage(output, 0), link =>
            Assert.False(string.IsNullOrWhiteSpace(link.Description)));
    }

    [Fact]
    public void ContentsPagesTakeTheDocumentsOwnPageSize()
    {
        // #512: an A4, A6 or 210 x 240 mm document got Letter contents pages.
        static PdfPageInformation FirstPage(PdfTableOfContentsWriteResult result) =>
            PdfPageInformation.Read(PdfDocument.Open(result.Document))[0];

        PdfTableOfContentsWriteResult a4 = PdfTableOfContentsWriter.Write(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(595.28, 841.89).AddBlankPage(595.28, 841.89).Build()));
        Assert.Equal(595.28, FirstPage(a4).Width, 2);
        Assert.Equal(841.89, FirstPage(a4).Height, 2);

        // A landscape-displayed page (portrait media, /Rotate 90) gets an upright landscape contents page.
        PdfTableOfContentsWriteResult rotated = PdfTableOfContentsWriter.Write(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(298, 420).SetPageRotation(0, 90).Build()));
        Assert.Equal(420, FirstPage(rotated).Width, 2);
        Assert.Equal(298, FirstPage(rotated).Height, 2);
        Assert.Equal(0, FirstPage(rotated).Rotation);

        // An explicit size still wins.
        PdfTableOfContentsWriteResult explicitSize = PdfTableOfContentsWriter.Write(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(595.28, 841.89).Build()),
            new PdfTableOfContentsWriteOptions { PageWidth = 612, PageHeight = 792 });
        Assert.Equal(612, FirstPage(explicitSize).Width, 2);
        Assert.Equal(792, FirstPage(explicitSize).Height, 2);

        // A page too small for the margins and one row falls back to Letter instead of failing.
        PdfTableOfContentsWriteResult card = PdfTableOfContentsWriter.Write(PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage(252, 144).Build()));
        Assert.Equal(612, FirstPage(card).Width, 2);
        Assert.Equal(792, FirstPage(card).Height, 2);
    }

    [Fact]
    public void WriteCreatesAnEmptyContentsPageAndRejectsImpossibleLayout()
    {
        PdfDocument source = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        PdfTableOfContentsWriteResult result = PdfTableOfContentsWriter.Write(source);

        Assert.Equal(1, result.InsertedPageCount);
        Assert.Equal(0, result.EntryCount);
        Assert.Equal(2, new PdfIncrementalPageEditor(
            PdfDocument.Open(result.Document)).PageCount);
        Assert.Throws<ArgumentException>(() => PdfTableOfContentsWriter.Write(source,
            new PdfTableOfContentsWriteOptions
            {
                PageHeight = 100,
                TopMargin = 40,
                BottomMargin = 40,
                TitleFontSize = 18,
                TitleGap = 22
            }));
    }
}
