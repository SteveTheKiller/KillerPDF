using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Editing;
using System.Text.Json;
using System.Text;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfBookmarkReaderTests
{
    [Fact]
    public void Read_PreservesHierarchyPresentationDestinationsAndIdentity()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                IsOpen = false,
                Style = PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic,
                Color = new PdfRgbColor(0.1, 0.3, 0.8),
                Destination = PdfDestination.At(72, 700, 1.25)
            })
            .AddBookmark("Résumé", 1, 1, new PdfBookmarkOptions
            {
                Destination = PdfDestination.FitWidth(640)
            })
            .AddBookmark("Next", 2)
            .Build());

        IReadOnlyList<PdfBookmarkInfo> bookmarks = PdfBookmarkReader.Read(document);

        Assert.Equal(2, bookmarks.Count);
        PdfBookmarkInfo chapter = bookmarks[0];
        Assert.True(chapter.ObjectNumber > 0);
        Assert.Equal(0, chapter.Generation);
        Assert.Equal("Chapter", chapter.Title);
        Assert.False(chapter.IsOpen);
        Assert.Equal(PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic, chapter.Style);
        Assert.Equal(new PdfRgbColor(0.1, 0.3, 0.8), chapter.Color);
        Assert.Equal(0, chapter.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, chapter.Destination!.Kind);
        Assert.Equal([72, 700, 1.25], chapter.Destination.Values);
        PdfBookmarkInfo child = Assert.Single(chapter.Children);
        Assert.Equal("Résumé", child.Title);
        Assert.Equal(1, child.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitH, child.Destination!.Kind);
        Assert.Equal([640], child.Destination.Values);
        Assert.Empty(bookmarks[1].Children);
        Assert.NotEqual(chapter.ObjectNumber, child.ObjectNumber);
    }

    [Fact]
    public void Read_ResolvesUnicodeNamedDestinationAndRetainsItsName()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddNamedDestination("résumé", 1, PdfDestination.FitBoundingBoxWidth(720))
            .AddNamedDestinationBookmark("Résumé", "résumé")
            .Build());

        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(document));

        Assert.Equal("résumé", bookmark.NamedDestination);
        Assert.Equal(1, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.FitBH, bookmark.Destination!.Kind);
        Assert.Equal([720], bookmark.Destination.Values);
    }

    [Fact]
    public void Read_TreatsExplicitZeroXyzZoomAsRetainCurrentZoom()
    {
        byte[] source = new PdfDocumentBuilder()
            .AddBlankPage()
            .AddBookmark("Typst heading", 0, options: new PdfBookmarkOptions
            {
                Destination = PdfDestination.At(70, 781, 1)
            })
            .Build();
        source = ReplaceAscii(source, "/XYZ 70 781 1", "/XYZ 70 781 0");

        PdfBookmarkInfo bookmark = Assert.Single(
            PdfBookmarkReader.Read(PdfDocument.Open(source)));

        Assert.Equal(0, bookmark.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, bookmark.Destination!.Kind);
        Assert.Equal([70, 781, null], bookmark.Destination.Values);
    }

    [Fact]
    public void Read_ReturnsEmptyListWhenDocumentHasNoBookmarks()
    {
        PdfDocument document = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage().Build());

        Assert.Empty(PdfBookmarkReader.Read(document));
    }

    [Fact]
    public void RenamePreservesBookmarkHierarchyAndDestination()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0)
            .AddBookmark("Old title", 1, level: 1)
            .Build());
        PdfBookmarkInfo child = Assert.Single(
            Assert.Single(PdfBookmarkReader.Read(original)).Children);

        PdfDocument changed = PdfDocument.Open(
            PdfBookmarkEditor.Rename(original, child.ObjectNumber, "Résumé"));
        PdfBookmarkInfo renamed = Assert.Single(
            Assert.Single(PdfBookmarkReader.Read(changed)).Children);

        Assert.Equal("Résumé", renamed.Title);
        Assert.Equal(1, renamed.DestinationPageIndex);
        Assert.Equal("Old title", child.Title);
    }

    [Fact]
    public void AppearanceCanBeChangedAndClearedWithoutChangingDestination()
    {
        PdfDocument original = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBookmark("Chapter", 0).Build());
        PdfBookmarkInfo bookmark = Assert.Single(PdfBookmarkReader.Read(original));

        PdfDocument styled = PdfDocument.Open(PdfBookmarkEditor.SetAppearance(
            original, bookmark.ObjectNumber,
            PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic,
            new PdfRgbColor(0.25, 0.5, 0.75)));
        PdfBookmarkInfo changed = Assert.Single(PdfBookmarkReader.Read(styled));

        Assert.Equal(PdfBookmarkStyle.Bold | PdfBookmarkStyle.Italic, changed.Style);
        Assert.Equal(new PdfRgbColor(0.25, 0.5, 0.75), changed.Color);
        Assert.Equal(0, changed.DestinationPageIndex);
        PdfBookmarkInfo cleared = Assert.Single(PdfBookmarkReader.Read(PdfDocument.Open(
            PdfBookmarkEditor.SetAppearance(styled, bookmark.ObjectNumber))));
        Assert.Equal(PdfBookmarkStyle.Regular, cleared.Style);
        Assert.Null(cleared.Color);
    }

    [Fact]
    public void BookmarkJsonRoundTripPreservesHierarchyDestinationsAndAppearance()
    {
        PdfDocument source = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage()
            .AddBookmark("Chapter", 0, options: new PdfBookmarkOptions
            {
                IsOpen = false,
                Style = PdfBookmarkStyle.Bold,
                Color = new PdfRgbColor(0.25, 0.5, 0.75),
                Destination = PdfDestination.At(72, 700, 1.5)
            })
            .AddBookmark("Section", 1, level: 1, options: new PdfBookmarkOptions
            {
                Style = PdfBookmarkStyle.Italic,
                Destination = PdfDestination.FitWidth(640)
            })
            .Build());

        string json = PdfBookmarkExchange.ToJson(source, indented: true);
        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Chapter", parsed.RootElement.GetProperty("bookmarks")[0]
            .GetProperty("title").GetString());
        PdfDocument target = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage().AddBlankPage().AddBookmark("Old", 0).Build());

        PdfDocument imported = PdfDocument.Open(
            PdfBookmarkExchange.Import(target, json, replaceExisting: true));
        PdfBookmarkInfo chapter = Assert.Single(PdfBookmarkReader.Read(imported));
        PdfBookmarkInfo section = Assert.Single(chapter.Children);

        Assert.Equal("Chapter", chapter.Title);
        Assert.False(chapter.IsOpen);
        Assert.Equal(PdfBookmarkStyle.Bold, chapter.Style);
        Assert.Equal(new PdfRgbColor(0.25, 0.5, 0.75), chapter.Color);
        Assert.Equal(0, chapter.DestinationPageIndex);
        Assert.Equal(PdfDestinationKind.Xyz, chapter.Destination!.Kind);
        Assert.Equal([72, 700, 1.5], chapter.Destination.Values);
        Assert.Equal("Section", section.Title);
        Assert.Equal(1, section.DestinationPageIndex);
        Assert.Equal(PdfBookmarkStyle.Italic, section.Style);
        Assert.Equal(PdfDestinationKind.FitH, section.Destination!.Kind);
        Assert.Equal([640], section.Destination.Values);
    }

    [Fact]
    public void BookmarkImportRejectsUnresolvedAndUnsupportedData()
    {
        PdfDocument target = PdfDocument.Open(
            new PdfDocumentBuilder().AddBlankPage().Build());

        InvalidOperationException unresolved = Assert.Throws<InvalidOperationException>(() =>
            PdfBookmarkExchange.Import(target,
                """{"schemaVersion":1,"bookmarks":[{"title":"Missing","isOpen":true,"style":"regular","children":[]}]}"""));
        Assert.Contains("no resolved page destination", unresolved.Message);
        Assert.Throws<NotSupportedException>(() => PdfBookmarkExchange.Import(target,
            """{"schemaVersion":2,"bookmarks":[]}"""));
    }

    private static byte[] ReplaceAscii(byte[] source, string oldValue, string newValue)
    {
        Assert.Equal(oldValue.Length, newValue.Length);
        string text = Encoding.Latin1.GetString(source);
        Assert.Equal(1, text.Split(oldValue).Length - 1);
        return Encoding.Latin1.GetBytes(text.Replace(oldValue, newValue, StringComparison.Ordinal));
    }
}
