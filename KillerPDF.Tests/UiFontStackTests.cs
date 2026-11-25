using System;
using System.IO;
using System.Text.RegularExpressions;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// Neither Killer Sans UI nor Segoe UI has CJK glyphs, so the first CJK face after them draws all
/// Chinese and Japanese interface text. The interface language picks that face; every other
/// language keeps the stack it always had.
/// </summary>
public sealed class UiFontStackTests
{
    private const string Today = "./Fonts/#Killer Sans UI, Segoe UI, Microsoft JhengHei UI, Nirmala UI";

    [Theory]
    [InlineData("zh-CN")]
    [InlineData("zh-Hans")]
    [InlineData("zh-SG")]
    public void SimplifiedChineseDrawsInYaHeiUi(string language)
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.For(language));
    }

    [Theory]
    [InlineData("zh-TW")]
    [InlineData("zh-HK")]
    [InlineData("zh-Hant")]
    public void TraditionalChineseKeepsJhengHeiUi(string language)
    {
        Assert.Equal(Today, UiFontStack.For(language));
    }

    [Fact]
    public void JapaneseDrawsInYuGothicUi()
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Yu Gothic UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.For("ja-JP"));
    }

    // Hangul is in none of the faces before it, and JhengHei's Hangul is a Chinese face's take on it. Malgun Gothic is
    // the face Windows draws Korean in, with no separate UI cut.
    [Theory]
    [InlineData("ko-KR")]
    [InlineData("ko")]
    public void KoreanDrawsInMalgunGothic(string language)
    {
        Assert.Equal("./Fonts/#Killer Sans UI, Segoe UI, Malgun Gothic, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.For(language));
        Assert.Equal("./Fonts/#Killer Sans, Segoe UI, Malgun Gothic, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.Title(language));
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("sv-SE")]
    [InlineData("")]
    [InlineData(null)]
    public void EveryOtherLanguageKeepsTheStack(string? language)
    {
        Assert.Equal(Today, UiFontStack.For(language));
    }

    [Fact]
    public void HeadingsAndFallbacksShareTheChoice()
    {
        Assert.Equal("./Fonts/#Lacquer, ./Fonts/#Killer Sans UI, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.Heading("zh-CN"));
        Assert.Equal("Segoe UI, Yu Gothic UI, Microsoft JhengHei UI, Nirmala UI", UiFontStack.Fallbacks("ja-JP"));
    }

    // #490: the file name in the title bar is drawn in Killer Sans, which has no CJK glyphs either, so it needs the same
    // language-chosen fallbacks as the interface or a Chinese file name falls to the system default face.
    [Fact]
    public void TheTitleFileNameStackFallsBackLikeTheInterface()
    {
        Assert.Equal("./Fonts/#Killer Sans, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.Title("zh-CN"));
        Assert.Equal("./Fonts/#Killer Sans, Segoe UI, Microsoft JhengHei UI, Nirmala UI", UiFontStack.Title("en-US"));
    }

    // #490: ideographs fill the em box, so Chinese and Japanese titles draw a size smaller. Hangul syllables are
    // square blocks that fill it the same way.
    [Theory]
    [InlineData("zh-CN", true)]
    [InlineData("zh-TW", true)]
    [InlineData("ja-JP", true)]
    [InlineData("ko-KR", true)]
    [InlineData("en-US", false)]
    [InlineData("sv-SE", false)]
    [InlineData(null, false)]
    public void ChineseJapaneseAndKoreanCountAsCjk(string? language, bool cjk) =>
        Assert.Equal(cjk, UiFontStack.IsCjk(language));

    // The status bar's page and zoom line is 16 in Killer Sans, which has no CJK glyphs, so Korean fell to Malgun
    // Gothic at 16 and read a size too big (Steve 2026-11-23). CJK languages take it at 14, like the title's step.
    [Theory]
    [InlineData("ko-KR", 14.0)]
    [InlineData("zh-CN", 14.0)]
    [InlineData("ja-JP", 14.0)]
    [InlineData("en-US", 16.0)]
    [InlineData("de-DE", 16.0)]
    [InlineData(null, 16.0)]
    public void TheStatusLineDropsASizeForCjk(string? language, double size) =>
        Assert.Equal(size, UiFontStack.StatusFontSize(language));

    // #490: the reporter, on Simplified Chinese at 150%, found the title and the toolbar captions still a size too big
    // in 1.9.6. Every CJK language takes them a size smaller.
    [Theory]
    [InlineData("zh-CN", 15.0, 15.0, 13.0)]
    [InlineData("zh-TW", 15.0, 15.0, 13.0)]
    [InlineData("ja-JP", 15.0, 15.0, 13.0)]
    [InlineData("ko-KR", 15.0, 15.0, 13.0)]
    [InlineData("en-US", 18.0, 16.0, 14.0)]
    [InlineData(null, 18.0, 16.0, 14.0)]
    public void TheTitleAndToolbarCaptionsDropASizeForCjk(string? language, double title, double beside, double under)
    {
        Assert.Equal(title, UiFontStack.TitleFontSize(language));
        Assert.Equal(beside, UiFontStack.ToolbarCaptionSize(language, under: false));
        Assert.Equal(under, UiFontStack.ToolbarCaptionSize(language, under: true));
    }

    [Fact]
    public void ToolbarCaptionsReadTheirSizeFromTheTheme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        string toolbar = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "ToolbarStyle.cs"));
        Assert.Contains("text.SetResourceReference(TextBlock.FontSizeProperty, sizeKey);", toolbar);
        string theme = File.ReadAllText(Path.Combine(dir.FullName, "Services", "ThemeManager.cs"));
        Assert.Contains("SetIfAbsent(\"FileNameFontSize\", flatTitle ? 11.0 : UiFontStack.TitleFontSize(UiLanguage));", theme);
        Assert.Contains("SetIfAbsent(\"ToolbarCaptionFontSize\", UiFontStack.ToolbarCaptionSize(UiLanguage, under: false));", theme);
        Assert.Contains("SetIfAbsent(\"ToolbarCaptionUnderFontSize\", UiFontStack.ToolbarCaptionSize(UiLanguage, under: true));", theme);
    }

    [Fact]
    public void TheStatusLineReadsItsSizeFromTheTheme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        string xaml = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "MainWindow.xaml"));
        Match status = Regex.Match(xaml, "<TextBlock x:Name=\"StatusText\"[^>]*>");
        Assert.True(status.Success);
        Assert.Contains("FontSize=\"{DynamicResource StatusFontSize}\"", status.Value);
    }

    [Fact]
    public void SwitchingTheLanguageRebuildsTheTitleStack()
    {
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        try
        {
            ThemeManager.SetUiLanguage("zh-CN");
            Assert.Equal(UiFontStack.Title("zh-CN"), ThemeManager.KillerSansTitleStack.Source);
            ThemeManager.SetUiLanguage("ja-JP");
            Assert.Equal(UiFontStack.Title("ja-JP"), ThemeManager.KillerSansTitleStack.Source);
        }
        finally { ThemeManager.SetUiLanguage("en-US"); }
    }

    // Source pin: the title file name and the theme's own MonoFont take the language stacks, never a bare face.
    [Fact]
    public void TheFileNameAndMonoFontsUseTheLanguageStacks()
    {
        string kit = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "ThemeManager.cs"));
        Assert.Contains("SetIfAbsent(\"FileNameFont\", flatTitle ? combined[\"ChromeFontFamily\"] : KillerSansTitleStack);", kit);
        Assert.Contains("SetIfAbsent(\"MonoFont\", KillerSansUiStack);", kit);
    }

    // Source pin: the 98SE caption glyph switch comes from the KillerUI kit's ThemeManager, as in KillerUI, and the app
    // keeps no second copy of it.
    [Fact]
    public void TheCaptionGlyphSwitchLivesOnlyInTheKit()
    {
        string root = FindRepositoryRoot();
        string kit = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        Assert.Contains("combined[\"CaptionDrawnGlyphVisibility\"] = flat ? Visibility.Visible   : Visibility.Collapsed;", kit);
        string app = File.ReadAllText(Path.Combine(root, "Services", "AppTheme.cs"));
        Assert.DoesNotContain("CaptionDrawnGlyphVisibility", app);
    }

    [Fact]
    public void AThemesSingleFaceKeepsTheLanguagesCjkFallback()
    {
        Assert.Equal("Microsoft Sans Serif, Segoe UI, Microsoft YaHei UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.WithFallbacks("Microsoft Sans Serif", "zh-CN"));
        Assert.Equal("Microsoft Sans Serif, Segoe UI, Yu Gothic UI, Microsoft JhengHei UI, Nirmala UI",
            UiFontStack.WithFallbacks("Microsoft Sans Serif", "ja-JP"));
        Assert.Equal("Tahoma, Segoe UI", UiFontStack.WithFallbacks("Tahoma, Segoe UI", "zh-CN"));
    }

    // Source pin: theme-stated faces go through the fallback before the kit fills its defaults.
    [Fact]
    public void ThemeStatedFacesGetTheLanguageFallbacks()
    {
        string kit = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "Services", "ThemeManager.cs"));
        Assert.Contains("UiFontStack.WithFallbacks(", kit);
        Assert.Contains("\"ChromeFontFamily\", \"UiFont\", \"MonoFont\", \"MenuFontFamily\"", kit);
    }

    [Fact]
    public void SwitchingTheLanguageRebuildsTheThemeStacks()
    {
        // Registers the pack:// scheme the stacks are built on, as a running app does.
        _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
        try
        {
            ThemeManager.SetUiLanguage("zh-CN");
            Assert.Equal(UiFontStack.For("zh-CN"), ThemeManager.KillerSansUiStack.Source);
            Assert.Equal(UiFontStack.Heading("zh-CN"), ThemeManager.LacquerHeadingStack.Source);
            ThemeManager.SetUiLanguage("ja-JP");
            Assert.Equal(UiFontStack.For("ja-JP"), ThemeManager.KillerSansUiStack.Source);
            ThemeManager.SetUiLanguage("de-DE");
            Assert.Equal(Today, ThemeManager.KillerSansUiStack.Source);
        }
        finally { ThemeManager.SetUiLanguage("en-US"); }
    }

    // Source pins: the language switch and the resource copy need a running app with its theme
    // files, which this project does not load.
    [Fact]
    public void ALanguageSwitchRepublishesTheThemeFonts()
    {
        string root = FindRepositoryRoot();
        string locale = File.ReadAllText(Path.Combine(root, "Services", "LocaleManager.cs"));
        Assert.Contains("ThemeManager.SetUiLanguage(CultureFor(locale).Name);", locale);
        string kit = File.ReadAllText(Path.Combine(root, "Services", "ThemeManager.cs"));
        Match setter = Regex.Match(kit, @"internal static void SetUiLanguage\(string language\)\s*\{(?<body>[^}]*)\}");
        Assert.True(setter.Success, "ThemeManager has no SetUiLanguage.");
        Assert.Contains("LoadDict(_current);", setter.Groups["body"].Value);
        Assert.Contains("SetIfAbsent(\"ChromeFontFamily\", KillerSansUiStack);", kit);
        Assert.Contains("SetIfAbsent(\"SearchTitleFont\", LacquerHeadingStack);", kit);
        string theme = File.ReadAllText(Path.Combine(root, "Services", "AppTheme.cs"));
        Assert.Contains("appResources[\"UiFont\"] = d[\"UiFont\"];", theme);
        Assert.Contains("appResources[\"MonoFont\"] = ThemeManager.KillerSansUiStack;", theme);
        string kitUi = File.ReadAllText(Path.Combine(root, "Controls", "UiKit.cs"));
        Assert.DoesNotContain("Microsoft JhengHei UI", kitUi);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj"))) return directory.FullName;
            directory = directory.Parent;
        }
        throw new InvalidOperationException("KillerPDF.csproj was not found above the test output.");
    }
}
