using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// HTML: an id should be unique; when it is not, the first element with it is the one getElementById and a fragment find.
public sealed class EpubDuplicateAnchorTests
{
    [Fact]
    public void TheFirstOfTwoEqualIdsIsTheDestinationAndTheSecondIsReported()
    {
        var book = new Book
        {
            Chapters =
            {
                Chapter("<p id='note'>AAA</p><p style='page-break-before:always'><a href='#note'>AA</a></p>"
                    + "<p id='note' style='page-break-before:always'>A</p><p id='other'>A</p>")
            }
        };
        var document = EpubDocument.Open(book.Build());

        var layout = document.CreateLayout(Options());

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal(3, layout.Pages.Count);
        var destination = layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#note");
        Assert.Equal(0, destination.PageIndex);
        Assert.Equal(0, destination.TextOffset);
        Assert.Equal(2, layout.ResolveNavigationTarget("OEBPS/text/c0.xhtml#other").PageIndex);
        var reported = Assert.Single(layout.ContentDiagnostics);
        Assert.Equal(EpubOmissionReason.InvalidContent, reported.Reason);
        Assert.Equal("OEBPS/text/c0.xhtml", reported.Href);
        Assert.Contains("note", reported.Message);
        // The converted PDF keeps the link to the first destination instead of failing.
        Assert.NotEmpty(document.ConvertToPdf(new EpubPdfConversionOptions(Options().DefaultFont) { IsolateChapterFailures = true }));
    }
}
