using System.Globalization;
using KillerPDF.Controls;
using Xunit;

namespace KillerPDF.Tests;

// The Stamp window reads typed sizes and keeps the watermark font through StampInput; the window
// itself is not linked into this project, so StampWindowSourceTests pins that it uses these.
public sealed class StampInputTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    // controls BF-024: NumberStyles.Any took "1,5" as 15 in en-US and "1.5" as 15 in de-DE.
    [Theory]
    [InlineData("1,5", "en-US")]
    [InlineData("1.5", "de-DE")]
    [InlineData("1,500", "en-US")]
    [InlineData("$12", "en-US")]
    public void ASeparatorFromTheOtherLocaleIsNeverReadAsAThousandsGroup(string text, string culture)
    {
        Assert.False(StampInput.TryParseNumber(text, CultureInfo.GetCultureInfo(culture), out double value),
            "'" + text + "' was read as " + value);
    }

    [Theory]
    [InlineData("1.5", "en-US", 1.5)]
    [InlineData("1,5", "de-DE", 1.5)]
    [InlineData(" 24 ", "en-US", 24.0)]
    public void ADecimalInTheCurrentCultureIsRead(string text, string culture, double expected)
    {
        Assert.True(StampInput.TryParseNumber(text, CultureInfo.GetCultureInfo(culture), out double value));
        Assert.Equal(expected, value);
    }

    [Fact]
    public void ATypedSizeTheSeparatorCannotMeanFallsBackInsteadOfBecomingTenTimesLarger()
    {
        Assert.Equal(12, StampInput.ReadSize("1,5", En, 12, StampInput.NumFontMin, StampInput.NumFontMax));
    }

    // controls BF-015: a typed 5000 reached the spec unclamped while the slider showed its maximum.
    [Theory]
    [InlineData("5000", 96)]
    [InlineData("2", 6)]
    [InlineData("40", 40)]
    [InlineData("96", 96)]
    [InlineData("6", 6)]
    public void ANumberSizeIsHeldToTheSlidersRange(string text, double expected)
    {
        Assert.Equal(expected, StampInput.ReadSize(text, En, 12, StampInput.NumFontMin, StampInput.NumFontMax));
    }

    [Theory]
    [InlineData("5000", 200)]
    [InlineData("3", 12)]
    [InlineData("64", 64)]
    public void AWatermarkSizeIsHeldToTheSlidersRange(string text, double expected)
    {
        Assert.Equal(expected, StampInput.ReadSize(text, En, 64, StampInput.WmFontMin, StampInput.WmFontMax));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0")]
    [InlineData("-8")]
    public void AnUnreadableOrNonPositiveSizeUsesTheFallback(string text)
    {
        Assert.Equal(12, StampInput.ReadSize(text, En, 12, StampInput.NumFontMin, StampInput.NumFontMax));
    }

    // controls BF-025: a saved font that is not installed was replaced by Segoe UI on Apply.
    [Fact]
    public void ASavedFontThatIsNotInTheListIsKeptWhenNothingIsSelected()
    {
        Assert.Equal("Garamond Premier Pro", StampInput.FontToKeep(null, "Garamond Premier Pro"));
    }

    [Fact]
    public void AFontTheUserChoseWinsOverTheSavedOne()
    {
        Assert.Equal("Arial", StampInput.FontToKeep("Arial", "Garamond Premier Pro"));
    }

    [Fact]
    public void WithNeitherAFontTheDefaultIsUsed()
    {
        Assert.Equal("Segoe UI", StampInput.FontToKeep(null, ""));
    }
}


