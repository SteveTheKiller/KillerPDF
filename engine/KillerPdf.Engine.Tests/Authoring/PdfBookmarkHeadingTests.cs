using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Authoring;

public sealed class PdfBookmarkHeadingTests
{
    [Fact]
    public void GroupingHeadingPreservesAppearanceAndNestedDestinationsWhenReopened()
    {
        var document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddBookmarkHeading("Part", options: new PdfBookmarkOptions
            {
                IsOpen = false, Style = PdfBookmarkStyle.Bold, Color = new PdfRgbColor(1, 0, 0)
            })
            .AddBookmarkHeading("Section", 1)
            .AddBookmark("Chapter", 0, 2)
            .AddBookmark("Next part", 0)
            .Build());

        var bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(new[] { "Part", "Next part" }, bookmarks.Select(item => item.Title));
        var heading = bookmarks[0];
        Assert.Null(heading.DestinationPageIndex);
        Assert.Null(heading.Destination);
        Assert.False(heading.IsOpen);
        Assert.Equal(PdfBookmarkStyle.Bold, heading.Style);
        Assert.Equal(new PdfRgbColor(1, 0, 0), heading.Color);
        var section = Assert.Single(heading.Children);
        Assert.Equal("Section", section.Title);
        Assert.Null(section.DestinationPageIndex);
        Assert.Equal(0, Assert.Single(section.Children).DestinationPageIndex);
        Assert.Equal(0, bookmarks[1].DestinationPageIndex);
    }
}
