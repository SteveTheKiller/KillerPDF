using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PageSizeFormatterTests
{
    // Issue 462: a 500 pixel image read as 708 because every page was sized at 150 DPI.
    [Fact]
    public void APictureShowsItsOwnPixelsAndNotAnAssumedResolution()
    {
        var size = PageSizeFormatter.Format(340, 340, imagePixels: (500, 500));
        Assert.Equal("500 x 500 px", size.Pixels);
        Assert.StartsWith("500 x 500 px | ", size.Details, StringComparison.Ordinal);
    }

    [Fact]
    public void APageThatIsNotOneImageSaysWhatItsPixelsAssume() =>
        Assert.Equal("708 x 708 px at 150 DPI", PageSizeFormatter.Format(340, 340).Pixels);

    [Theory]
    [InlineData(612, 792, "Letter", false)]
    [InlineData(792, 612, "Letter", false)]
    [InlineData(612, 1008, "Legal", false)]
    [InlineData(792, 1224, "Tabloid", false)]
    [InlineData(1224, 792, "Ledger", false)]
    [InlineData(1584, 2448, "ANSI D", false)]
    // Architectural sheets (#338: a 24 x 36 in ARCH D drawing showed its size with no name).
    [InlineData(648, 864, "ARCH A", false)]
    [InlineData(864, 1296, "ARCH B", false)]
    [InlineData(1296, 1728, "ARCH C", false)]
    [InlineData(1728, 2592, "ARCH D", false)]
    [InlineData(2592, 1728, "ARCH D", false)]
    [InlineData(2160, 3024, "ARCH E1", false)]
    [InlineData(2592, 3456, "ARCH E", false)]
    [InlineData(595.28, 841.89, "A4", true)]
    [InlineData(841.89, 595.28, "A4", true)]
    [InlineData(2383.94, 3370.39, "A0", true)]
    public void CommonPaperSizesAreNamedInEveryFooterUnit(double width, double height, string name, bool metric)
    {
        var size = PageSizeFormatter.Format(width, height);

        Assert.Equal(metric, size.Metric);
        Assert.StartsWith(name + "  ", size.Label, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Pixels, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Inches, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Millimeters, StringComparison.Ordinal);
        Assert.StartsWith(name + "  ", size.Points, StringComparison.Ordinal);
    }

    [Fact]
    public void OddSizesShowDimensionsWithoutAName()
    {
        var size = PageSizeFormatter.Format(500, 500);

        Assert.EndsWith(" in", size.Label, StringComparison.Ordinal);
        Assert.False(char.IsLetter(size.Label[0]));
        Assert.EndsWith(" pt", size.Points, StringComparison.Ordinal);
    }
}
