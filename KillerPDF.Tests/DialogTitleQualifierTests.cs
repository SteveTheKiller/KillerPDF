using KillerPDF;
using Xunit;

namespace KillerPDF.Tests;

// The main title bar shows the wordmark and then the file name with no dash between them,
// so a dialog's wordmark caption shows its name the same way.
public sealed class DialogTitleQualifierTests
{
    [Theory]
    [InlineData("KillerPDF - Field Border", "Field Border")]
    [InlineData("KillerPDF - Color Correction", "Color Correction")]
    [InlineData("KillerPDF -Transform", "Transform")]
    [InlineData("KillerPDF", "")]
    [InlineData("KillerPDF - Page 1-2", "Page 1-2")]
    public void WordmarkCaptionDropsTheDashBeforeTheName(string title, string expected)
    {
        Assert.Equal(expected, DialogTitleText.Qualifier(title));
    }

    // The color picker's caption is the ColorPDF wordmark alone, with nothing after it.
    [Fact]
    public void ColorPdfCaptionHasNoQualifier()
    {
        Assert.Equal("", DialogTitleText.Qualifier("ColorPDF", "ColorPDF"));
    }
}
