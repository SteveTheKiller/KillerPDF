using KillerPdf.Engine.Formats.Epub;
using Xunit;
using static KillerPdf.Engine.Tests.Formats.Epub.EpubCompatibilityTests;

namespace KillerPdf.Engine.Tests.Formats.Epub;

// ISO 32000-1 annex C.2: without UserUnit a page is at least 3 and at most 14,400 default user units
// (1/72 in, so 200 in or 5,080 mm) on each side.
public sealed class EpubPdfPageLimitTests
{
    [Theory]
    [InlineData(14400, 14400, true)]
    [InlineData(3, 3, true)]
    [InlineData(595.2755905511812, 841.8897637795276, true)]
    [InlineData(14400.001, 100, false)]
    [InlineData(100, 14401, false)]
    [InlineData(2.999, 100, false)]
    [InlineData(double.NaN, 100, false)]
    [InlineData(100, double.PositiveInfinity, false)]
    public void PdfPageLimitFollowsIso32000(double width, double height, bool fits)
        => Assert.Equal(fits, EpubPdfConversionOptions.FitsPdfPageLimit(width, height));

    [Fact]
    public void ConversionRefusesAPageAcrobatWouldRefuseBeforeLayingOut()
    {
        var book = EpubDocument.Open(new Book { Chapters = { Chapter("<p>A</p>") } }.Build());

        // 14,401 points is 19,201 CSS pixels, which native layout itself would still accept.
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => book.ConvertToPdf(
            new EpubPdfConversionOptions(Options().DefaultFont) { PageWidthPoints = 14401, PageHeightPoints = 100 }));

        Assert.Contains("14,400", error.Message);
    }
}
