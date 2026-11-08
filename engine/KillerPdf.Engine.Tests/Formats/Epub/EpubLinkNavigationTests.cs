using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// EP-BF-018, 019, 020, 029: unresolved links, images and outline entries degrade one at a time.
public sealed class EpubLinkNavigationTests
{
    private const string ContentsStart = "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\"><head><title>Contents</title></head><body><nav epub:type=\"toc\"><ol>";
    private const string ContentsEnd = "</ol></nav></body></html>";

    [Fact]
    public void ALinkToAFileThatIsNotInTheBookIsInertAndReported()
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p><a href='missing.xhtml#x'>A</a> <a href='c0.xhtml'>A</a></p>") } });

        Assert.Empty(layout.ChapterDiagnostics);
        var placements = layout.Pages[0].TextPlacements.Where(placement => placement.Width == 6).ToArray();
        Assert.Null(placements[0].LinkTarget);
        Assert.Equal("OEBPS/text/c0.xhtml", placements[1].LinkTarget);
        Assert.Contains("missing.xhtml", Assert.Single(layout.ContentDiagnostics).Message);
    }

    [Fact]
    public void AnImageThatIsNotInTheBookIsOmittedAndReported()
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A<img src='../images/none.png' alt=''/>A</p>") } });

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(2, layout.Pages[0].TextPlacements.Count(placement => placement.Width == 6));
        Assert.Contains("none.png", Assert.Single(layout.ContentDiagnostics).Message);
    }

    [Fact]
    public void AnAnchorInsideADisplayNoneSubtreeStillResolves()
    {
        var layout = Layout(new Book { Chapters = { Chapter("<p>A</p><div style='display:none'><a id='hidden'/></div><p id='after'>A</p>") } });

        var destination = layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#hidden");

        // The hidden anchor sits where the following visible text starts.
        Assert.Equal(layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#after").Top, destination.Top);
    }

    [Fact]
    public void AnOutlineEntryWhoseFileIsMissingBecomesAHeadingAndTheRestStays()
    {
        var book = EpubDocument.Open(new Book
        {
            Chapters = { Chapter("<p>A</p>") },
            Nav = ContentsStart + "<li><a href=\"text/c0.xhtml\">Start</a></li><li><a href=\"text/gone.xhtml\">Gone</a></li>"
                + "<li><a href=\"text/c0.xhtml#none\">Nowhere</a></li>" + ContentsEnd
        }.Build());

        var outline = book.ReadNavigation();

        Assert.Equal(new[] { ("Start", "OEBPS/text/c0.xhtml"), ("Gone", ""), ("Nowhere", "OEBPS/text/c0.xhtml#none") },
            outline.Select(entry => (entry.Label, entry.Target)));
        // Conversion keeps every bookmark: a missing anchor lands on its chapter's first page.
        var pdf = PdfDocument.Open(book.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont)));
        Assert.Equal(1, new PdfPageContentReader(pdf).PageCount);
    }

    [Fact]
    public void AnInvalidNavigationDocumentFallsBackToTheNcx()
    {
        const string ncx = "<ncx xmlns=\"http://www.daisy.org/z3986/2005/ncx/\" version=\"2005-1\"><head/><docTitle><text>T</text></docTitle>"
            + "<navMap><navPoint id=\"p1\" playOrder=\"1\"><navLabel><text>From NCX</text></navLabel><content src=\"text/c0.xhtml\"/></navPoint></navMap></ncx>";
        var book = new Book
        {
            Chapters = { Chapter("<p>A</p>") },
            Nav = "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Contents</title></head><body><p>No toc nav here.</p></body></html>",
            ExtraManifest = "<item id=\"ncx\" href=\"toc.ncx\" media-type=\"application/x-dtbncx+xml\"/>",
            SpineToc = "ncx"
        };
        book.Resources.Add(("toc.ncx", Encoding.UTF8.GetBytes(ncx)));

        var outline = EpubDocument.Open(book.Build()).ReadNavigation();

        Assert.Equal(("From NCX", "OEBPS/text/c0.xhtml"), outline.Select(entry => (entry.Label, entry.Target)).Single());
    }

    [Fact]
    public void AManifestItemWhoseFileIsMissingIsSkippedWithItsSpineReference()
    {
        var book = EpubDocument.Open(new Book
        {
            Chapters = { Chapter("<p>A</p>") },
            ExtraManifest = "<item id=\"lost\" href=\"text/lost.xhtml\" media-type=\"application/xhtml+xml\"/>",
            ExtraSpine = "<itemref idref=\"lost\"/>"
        }.Build());

        Assert.Equal(new[] { "OEBPS/text/c0.xhtml" }, book.Spine.Select(entry => entry.Item.Href));
    }
}
