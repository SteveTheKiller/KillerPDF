using KillerLauncher;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// The installer and portable launcher draw their text in the face Windows uses for the language
/// they show, the same rule the application follows. Locales are the names LauncherStrings
/// resolves to, one per embedded Strings.*.xml.
/// </summary>
public sealed class LauncherFontsTests
{
    private const string LatinUi = "./Fonts/#Killer Sans UI, Segoe UI";
    private const string LatinHeading = "./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI";

    [Fact]
    public void SimplifiedChineseDrawsInYaHeiUi()
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Ui("zh-CN"));
        Assert.Equal("./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Heading("zh-CN"));
    }

    [Fact]
    public void TraditionalChineseKeepsJhengHeiUi()
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Ui("zh-TW"));
        Assert.Equal(LatinHeading, LauncherFonts.Heading("zh-TW"));
    }

    [Fact]
    public void JapaneseDrawsInYuGothicUi()
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Yu Gothic UI, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Ui("ja-JP"));
        Assert.Equal("./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Yu Gothic UI, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Heading("ja-JP"));
    }

    [Fact]
    public void KoreanDrawsInMalgunGothic()
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Malgun Gothic, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Ui("ko-KR"));
        Assert.Equal("./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Malgun Gothic, Microsoft JhengHei UI, Nirmala UI", LauncherFonts.Heading("ko-KR"));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    [InlineData("ru-RU")]
    [InlineData("bn")]
    public void EveryOtherLanguageKeepsTheStacksItAlwaysHad(string locale)
    {
        Assert.Equal(LatinUi, LauncherFonts.Ui(locale));
        Assert.Equal(LatinHeading, LauncherFonts.Heading(locale));
    }
}
