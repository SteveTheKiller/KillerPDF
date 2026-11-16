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

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")]
    [InlineData("ko-KR")]
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
