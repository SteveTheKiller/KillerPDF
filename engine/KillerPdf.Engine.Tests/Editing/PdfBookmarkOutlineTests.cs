using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using Xunit;

namespace KillerPdf.Engine.Tests.Editing;

public sealed class PdfBookmarkOutlineTests
{
    [Fact]
    public void OutlineRenamesMovesAndRewritesSupportedBookmarks()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Parent", 0, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.1, 0.2, 0.3),
                Destination = PdfDestination.FitWidth(700)
            })
            .AddBookmark("Child", 1, level: 1)
            .AddBookmark("Last", 1)
            .Build());
        PdfBookmarkOutline outline = PdfBookmarkOutline.Read(source);
        int parentId = outline.Items[0].SourceObjectNumber!.Value;
        int lastId = outline.Items[2].SourceObjectNumber!.Value;

        PdfBookmarkOutline changed = outline.Rename(parentId, "Renamed")
            .MoveSubtree(lastId, targetIndex: 1, level: 1);
        PdfDocument reopened = PdfDocument.Open(changed.Apply(source));
        PdfBookmarkInfo root = Assert.Single(PdfBookmarkReader.Read(reopened));

        Assert.Equal("Renamed", root.Title);
        Assert.Equal(PdfBookmarkStyle.Bold, root.Style);
        Assert.Equal(new PdfRgbColor(0.1, 0.2, 0.3), root.Color);
        Assert.Equal(PdfDestinationKind.FitH, root.Destination?.Kind);
        Assert.Equal(["Last", "Child"], root.Children.Select(item => item.Title));
    }

    [Fact]
    public void OutlineDuplicatesCompleteSubtreesWithNewIdentity()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Parent", 0)
            .AddBookmark("Child", 1, level: 1)
            .AddBookmark("Last", 1)
            .Build());
        PdfBookmarkOutline outline = PdfBookmarkOutline.Read(source);

        PdfBookmarkOutline duplicated = outline.DuplicateSubtree(
            outline.Items[0].SourceObjectNumber!.Value);
        PdfDocument reopened = PdfDocument.Open(duplicated.Apply(source));
        IReadOnlyList<PdfBookmarkInfo> roots = PdfBookmarkReader.Read(reopened);

        Assert.Equal(["Parent", "Parent", "Last"], roots.Select(item => item.Title));
        Assert.Equal("Child", Assert.Single(roots[0].Children).Title);
        Assert.Equal("Child", Assert.Single(roots[1].Children).Title);
        Assert.NotEqual(roots[0].ObjectNumber, roots[1].ObjectNumber);
    }
}
