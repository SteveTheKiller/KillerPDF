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

    // #490: CJK text read a size too big against Latin text, and dropping the whole line a size crushed the CJK
    // glyphs and shrank ".pdf" with them. The stacks scale only CJK characters, to 90%, so Latin text keeps its exact
    // width and the line keeps its height, in every language and in an English interface showing a Chinese name.
    // The composite families live in Fonts/*.CompositeFont, since a family built in code cannot reach the app's
    // embedded fonts. Measured from the repo's Fonts folder, as the app reads them from its resources.
    [Theory]
    [InlineData("zh-CN", "试试新版")]
    [InlineData("zh-TW", "開啟檔案")]
    [InlineData("ja-JP", "ファイル")]
    [InlineData("ko-KR", "파일열기")]
    [InlineData("en-US", "试试新版")]
    public void CjkCharactersDrawAtNinetyPercentWhileLatinKeepsItsSize(string language, string cjk)
    {
        RunSta(() =>
        {
            var fonts = new Uri(RepoRoot() + Path.DirectorySeparatorChar);
            foreach (var (plainStack, scaledStack) in new[]
            {
                (UiFontStack.For(language), UiFontStack.ScaledFor(language)),
                (UiFontStack.Title(language), UiFontStack.ScaledTitle(language)),
                (UiFontStack.Heading(language), UiFontStack.ScaledHeading(language)),
            })
            {
                var plain = new System.Windows.Media.FontFamily(fonts, plainStack);
                var scaled = new System.Windows.Media.FontFamily(fonts, scaledStack);
                Assert.Equal(Text(plain, "KillerPDF.pdf 12").WidthIncludingTrailingWhitespace,
                    Text(scaled, "KillerPDF.pdf 12").WidthIncludingTrailingWhitespace);
                Assert.Equal(Text(plain, "Ag").Height, Text(scaled, "Ag").Height);
                Assert.Equal(Text(plain, "Ag").Baseline, Text(scaled, "Ag").Baseline);
                // Yu Gothic's kana land a few thousandths of a DIP off an exact 90%.
                Assert.Equal(Text(plain, cjk).WidthIncludingTrailingWhitespace * UiFontStack.CjkScale,
                    Text(scaled, cjk).WidthIncludingTrailingWhitespace, 0.01);
            }
        });
    }

    // Each language names the composite for its CJK face, and the plain stack follows it.
    [Theory]
    [InlineData("zh-CN", "SC")]
    [InlineData("zh-TW", "CJK")]
    [InlineData("ja-JP", "JP")]
    [InlineData("ko-KR", "KR")]
    [InlineData("en-US", "CJK")]
    [InlineData(null, "CJK")]
    public void EachLanguageNamesItsCompositeFamily(string? language, string tag)
    {
        Assert.Equal("./Fonts/#Killer Sans UI " + tag + ", " + UiFontStack.For(language), UiFontStack.ScaledFor(language));
        Assert.Equal("./Fonts/#Killer Sans " + tag + ", " + UiFontStack.Title(language), UiFontStack.ScaledTitle(language));
        Assert.Equal("./Fonts/#Lacquer, " + UiFontStack.ScaledFor(language), UiFontStack.ScaledHeading(language));
        string file = Path.Combine(RepoRoot(), "Fonts", "KillerSansUI-" + tag + ".CompositeFont");
        Assert.Contains(">Killer Sans UI " + tag + "<", File.ReadAllText(file));
    }

    // Menus, dialogs and buttons draw at 10 to 13, where 90% clogs CJK strokes, so the UI stack and a theme's own
    // face keep CJK at full size. Only the title and heading stacks scale it.
    [Fact]
    public void SmallInterfaceTextKeepsCjkAtFullSize()
    {
        string theme = File.ReadAllText(Path.Combine(RepoRoot(), "Services", "ThemeManager.cs"));
        Assert.Contains("new(new Uri($\"pack://application:,,,/{AssemblyName};component/\"), UiFontStack.For(language));", theme);
        Assert.Contains("combined[fontKey] = new FontFamily(UiFontStack.WithFallbacks(stated.Source, UiLanguage));", theme);
        Assert.Contains("flat ? new FontFamily(UiFontStack.WithFallbacks(\"Microsoft Sans Serif\", UiLanguage)) : KillerSansUiStack", theme);
    }

    // Every composite file is embedded, or the app would quietly draw the plain stack.
    [Fact]
    public void TheCompositeFontFilesAreEmbedded()
    {
        string project = File.ReadAllText(Path.Combine(RepoRoot(), "KillerPDF.csproj"));
        Assert.Contains("<Resource Include=\"Fonts\\*.CompositeFont\" />", project);
        Assert.Equal(8, Directory.GetFiles(Path.Combine(RepoRoot(), "Fonts"), "*.CompositeFont").Length);
    }

    private static System.Windows.Media.FormattedText Text(System.Windows.Media.FontFamily family, string text) =>
        new(text, System.Globalization.CultureInfo.InvariantCulture, System.Windows.FlowDirection.LeftToRight,
            new System.Windows.Media.Typeface(family, System.Windows.FontStyles.Normal, System.Windows.FontWeights.Normal,
                System.Windows.FontStretches.Normal), 18, System.Windows.Media.Brushes.Black, 1.0);

    // #490: Killer Sans sets the caption's line height and CJK glyphs rise above it, so with the Latin gap a Chinese
    // caption sat about a pixel under its icon. Drawn at 150% like the reporter's monitor, every CJK caption has to
    // clear the icon by as much as an English one, within a device pixel.
    [Theory]
    [InlineData("zh-CN", "添加")]
    [InlineData("zh-TW", "開啟")]
    [InlineData("ja-JP", "開く")]
    [InlineData("ko-KR", "열기")]
    public void ACjkCaptionSitsAsFarUnderItsIconAsALatinOne(string language, string caption)
    {
        double latin = 0, cjk = 0;
        RunSta(() =>
        {
            latin = InkGapUnderIcon("en-US", "Add");
            cjk = InkGapUnderIcon(language, caption);
        });
        // One device pixel at 150% is 2/3 DIP; the small slack keeps an exact pixel inside the range.
        Assert.InRange(cjk, latin - 2 / 3.0 - 1e-9, latin + 2 / 3.0 + 1e-9);
    }

    // The gap in DIPs between a 14-DIP icon and the first inked row of its caption, laid out as ToolbarStyle lays a
    // caption under its icon.
    private static double InkGapUnderIcon(string language, string caption)
    {
        const double scale = 1.5, icon = 14;
        var text = new System.Windows.Controls.TextBlock
        {
            Text = caption,
            FontFamily = new System.Windows.Media.FontFamily(new Uri(RepoRoot() + Path.DirectorySeparatorChar), UiFontStack.ScaledTitle(language)),
            FontSize = 14,
            Margin = new System.Windows.Thickness(0, UiFontStack.ToolbarCaptionUnderGap(language), 0, 0),
            Foreground = System.Windows.Media.Brushes.Black,
        };
        var column = new System.Windows.Controls.StackPanel();
        column.Children.Add(new System.Windows.Controls.Border { Height = icon });
        column.Children.Add(text);
        column.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        column.Arrange(new System.Windows.Rect(column.DesiredSize));
        int width = (int)Math.Ceiling(column.ActualWidth * scale), height = (int)Math.Ceiling(column.ActualHeight * scale);
        var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale,
            System.Windows.Media.PixelFormats.Pbgra32);
        bitmap.Render(column);
        byte[] pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                if (pixels[(y * width + x) * 4 + 3] > 96) return y / scale - icon;
        throw new InvalidOperationException("The caption drew nothing.");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        return dir!.FullName;
    }

    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    [Fact]
    public void ToolbarCaptionsReadTheirSizeFromTheTheme()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.sln"))) dir = dir.Parent;
        string toolbar = File.ReadAllText(Path.Combine(dir!.FullName, "Shell", "ToolbarStyle.cs"));
        Assert.Contains("text.SetResourceReference(TextBlock.FontSizeProperty, sizeKey);", toolbar);
        string theme = File.ReadAllText(Path.Combine(dir.FullName, "Services", "ThemeManager.cs"));
        Assert.Contains("SetIfAbsent(\"FileNameFontSize\", flatTitle ? 11.0 : 18.0);", theme);
        Assert.Contains("SetIfAbsent(\"ToolbarCaptionFontSize\", 16.0);", theme);
        Assert.Contains("SetIfAbsent(\"ToolbarCaptionUnderFontSize\", 14.0);", theme);
        Assert.Contains("SetIfAbsent(\"StatusFontSize\", 16.0);", theme);
        Assert.Contains("under.SetResourceReference(FrameworkElement.MarginProperty, CaptionUnderMargin);", toolbar);
        Assert.Contains("SetIfAbsent(\"ToolbarCaptionUnderMargin\", new Thickness(0, UiFontStack.ToolbarCaptionUnderGap(UiLanguage), 0, 0));", theme);
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
            Assert.Equal(UiFontStack.ScaledTitle("zh-CN"), ThemeManager.KillerSansTitleStack.Source);
            ThemeManager.SetUiLanguage("ja-JP");
            Assert.Equal(UiFontStack.ScaledTitle("ja-JP"), ThemeManager.KillerSansTitleStack.Source);
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
            Assert.Equal(UiFontStack.ScaledHeading("zh-CN"), ThemeManager.LacquerHeadingStack.Source);
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
