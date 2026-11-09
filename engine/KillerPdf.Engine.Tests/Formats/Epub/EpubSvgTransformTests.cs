using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// SVG 1.1 section 7.6: an image's transform attribute maps its rectangle before the viewBox maps into the viewport.
// The 100 by 200 viewBox stretches (preserveAspectRatio none) into the 56 by 56 content box of a 60 by 60 page with
// 2px margins: 0.56 per user unit across and 0.28 down. The packaged raster is 10 by 20 pixels.
public sealed class EpubSvgTransformTests
{
    [Theory]
    // The image covers user units 50 to 100 across and 100 to 200 down: 2 + 50 * 0.56 = 30, 2 + 100 * 0.28 = 30.
    [InlineData("<image width='50' height='100' transform='translate(50,100)' xlink:href='../images/c.png'/>", 30, 30, 28, 28)]
    [InlineData("<image width='100' height='200' transform='translate(50 100) scale(.5)' xlink:href='../images/c.png'/>", 30, 30, 28, 28)]
    [InlineData("<image width='100' height='200' transform='matrix(0.5,0,0,0.5,50,100)' xlink:href='../images/c.png'/>", 30, 30, 28, 28)]
    // scale(0.5) alone keeps the origin: 50 by 100 user units at the top left.
    [InlineData("<image width='100' height='200' transform='scale(0.5)' xlink:href='../images/c.png'/>", 2, 2, 28, 28)]
    // A quarter turn about the origin, then a shift back into view, still lands on whole axes.
    [InlineData("<image width='200' height='100' preserveAspectRatio='none' transform='translate(100,0) rotate(90)' xlink:href='../images/c.png'/>", 2, 2, 56, 56)]
    public void AnImageTransformMovesAndScalesTheCover(string image, double left, double top, double width, double height)
    {
        var paint = Cover("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 100 200' preserveAspectRatio='none'>"
            + image + "</svg>");

        Assert.Equal((left, top, width, height), (Math.Round(paint.Left, 9), Math.Round(paint.Top, 9), Math.Round(paint.Width, 9), Math.Round(paint.Height, 9)));
    }

    // A quarter turn swaps the raster's sides: the 10 by 20 source is drawn as 20 by 10 pixels.
    [Fact]
    public void AQuarterTurnRotatesTheRaster()
    {
        var paint = Cover("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' viewBox='0 0 100 200' preserveAspectRatio='none'>"
            + "<image width='200' height='100' preserveAspectRatio='none' transform='translate(100,0) rotate(90)' xlink:href='../images/c.png'/></svg>");

        Assert.Equal((20, 10), (paint.Image.Width, paint.Image.Height));
    }

    // SVG content the reader does not draw is left out as one item; the chapter's text stays.
    [Theory]
    [InlineData("<foreignObject/>")]
    [InlineData("<use href='../images/c.png'/>")]
    [InlineData("<image width='2' height='2' href='https://example.invalid/image.png'/>")]
    [InlineData("<image width='2' height='2' href='../images/c.png'><animate/></image>")]
    [InlineData("<image width='2' height='2' href='../images/c.png'/><image width='2' height='2' href='../images/c.png'/>")]
    [InlineData("<image width='2' height='2'/>")]
    [InlineData("<rect width='2' height='2'/>")]
    [InlineData("")]
    public void SvgContentTheReaderCannotDrawIsAnOmittedItem(string content)
    {
        var book = new Book
        {
            Chapters = { Chapter("<p>A</p><svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 2 2'>" + content + "</svg><p>A</p>") },
            ExtraManifest = "<item id=\"c\" href=\"images/c.png\" media-type=\"image/png\"/>"
        };
        book.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 20, 2)));

        var layout = Layout(book, 60, 60);

        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Equal("A\n\nA", string.Concat(layout.Pages.Select(page => page.Text)).Trim('\n'));
        var omitted = Assert.Single(layout.ContentDiagnostics);
        Assert.Equal(EpubOmissionReason.UnsupportedLayout, omitted.Reason);
        Assert.Equal("OEBPS/text/c0.xhtml", omitted.Href);
        Assert.Empty(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
    }

    private static NativeImagePaint Cover(string svg)
    {
        var book = new Book
        {
            Chapters = { Chapter("<div>" + svg + "</div>") },
            ExtraManifest = "<item id=\"c\" href=\"images/c.png\" media-type=\"image/png\"/>"
        };
        book.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 20, 2)));
        var layout = Layout(book, 60, 60);
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Empty(layout.ContentDiagnostics);
        return Assert.Single(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
    }
}
