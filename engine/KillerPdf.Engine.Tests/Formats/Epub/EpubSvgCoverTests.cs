using KillerPdf.Engine.Formats.Epub;
using KillerPdf.Engine.Rendering;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// EP-BF-026, SVG 1.1 section 7.8: the cover's viewBox maps into the 56 by 56 content box of a 60 by 60 page
// with 2px margins, and preserveAspectRatio chooses stretch (none), fit (meet) or fill and crop (slice).
// The packaged raster is 10 by 20 pixels and the image element covers the 100 by 200 viewBox.
public sealed class EpubSvgCoverTests
{
    [Theory]
    // none stretches the viewBox to the viewport in each direction.
    [InlineData("none", 2, 2, 56, 56, 20)]
    // meet scales by min(56/100, 56/200) = 0.28: 28 by 56, centered (xMid) or at the left edge (xMin) or right (xMax).
    [InlineData("xMidYMid meet", 16, 2, 28, 56, 20)]
    [InlineData("xMinYMin meet", 2, 2, 28, 56, 20)]
    [InlineData("xMaxYMax", 30, 2, 28, 56, 20)]
    // slice scales by max(...) = 0.56: 56 by 112, centered, so only the middle 10 of the 20 source rows show.
    [InlineData("xMidYMid slice", 2, 2, 56, 56, 10)]
    public void CoverAspectModesPlaceTheImageAsSvgDoes(string mode, double left, double top, double width, double height, int sourceRows)
    {
        var paint = Cover("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' version='1.1' width='100%' height='100%' viewBox='0 0 100 200' preserveAspectRatio='"
            + mode + "'><image width='100' height='200' xlink:href='../images/c.png'/></svg>");

        Assert.Equal((left, top, width, height), Rounded(paint));
        Assert.Equal(sourceRows, paint.Image.Height);
    }

    [Fact]
    public void ACalibreTitlePageWithANonzeroOriginAndHeadStylesLaysOut()
    {
        // The viewBox origin (50, 0) shifts the image, drawn at x = 50, back to the left edge.
        const string head = "<style type='text/css'>svg { display: block } @page { margin: 0 }</style>";
        var paint = Cover("<svg xmlns='http://www.w3.org/2000/svg' xmlns:xlink='http://www.w3.org/1999/xlink' id='cover' class='svg' width='600px' height='800px' viewBox='50 0 100 200' preserveAspectRatio='none'>"
            + "<image x='50' y='0' width='100' height='200' xlink:href='../images/c.png'/></svg>", head);

        Assert.Equal((2d, 2d, 56d, 56d), Rounded(paint));
    }

    private static (double, double, double, double) Rounded(NativeImagePaint paint)
        => (Math.Round(paint.Left, 9), Math.Round(paint.Top, 9), Math.Round(paint.Width, 9), Math.Round(paint.Height, 9));

    private static NativeImagePaint Cover(string svg, string head = "")
    {
        var book = new Book
        {
            Chapters = { Chapter("<div>" + svg + "</div>", head) },
            ExtraManifest = "<item id=\"c\" href=\"images/c.png\" media-type=\"image/png\"/>"
        };
        book.Resources.Add(("images/c.png", EpubImageCompatibilityTests.Png(10, 20, 2)));
        var layout = Layout(book, 60, 60);
        Assert.Empty(layout.ChapterDiagnostics);
        Assert.Empty(layout.ContentDiagnostics);
        return Assert.Single(layout.Pages.SelectMany(page => page.Glyphs.OfType<NativeImagePaint>()));
    }
}
