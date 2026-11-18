using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

// No card tile cuts its caption off with an ellipsis (Steve 2026-11-17: the tab card showed "Open Containi..."). A tile
// is at most 84 px wide (QuickStrip's MaxWidth) less its 2 px padding and 1 px border on each side, and its caption
// (CardQuickButton) wraps to at most two lines of 11 px text before the template trims it. Every caption a tile shows
// is measured in the menu font; one that does not fit has a short caption in TileCaptions, and the tile keeps the
// full name as its tooltip and accessible name.
public sealed class TileCaptionTests
{
    private const double CaptionWidth = 84 - 2 * 2 - 2 * 1;
    private const double CaptionMaxHeight = 30;
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "KillerPDF.csproj"))) dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("KillerPDF.csproj not found.");
    }

    // Every card menu built of tiles: the page, gray-area, markup and picture cards, the tab card, the OCR, Save and
    // Open dropdowns, the sidebar cards and the Document tools flyouts.
    private static readonly string[][] TileSources =
    [
        ["Shell", "PageCard.cs"], ["Shell", "ContextMenu.cs"], ["Shell", "AnnotationCard.cs"], ["Shell", "PictureCard.cs"],
        ["Shell", "Ocr.cs"], ["Shell", "FileOperations.cs"], ["Shell", "SidebarPanels.cs"], ["Shell", "SidebarOutline.cs"],
        ["Shell", "DataExchange.cs"], ["Shell", "DocumentRepairs.cs"], ["Controls", "Viewer", "PdfViewer.TabStrip.cs"],
    ];

    // A tile is written as (glyph, Loc("Str_...")...), Tile(Short("Str_..."), ...), an Open menu command
    // new("Str_...", "\u...") or a Document tools card command ("Str_...", action).
    private static readonly Regex[] TilePatterns =
    [
        new(@"(?:""\\u[0-9A-Fa-f]{4}""|""[\uE000-\uF8FF]""|GlyphText\(0x[0-9A-Fa-f]+\)|\b\w*[Gg]lyph\b)\s*,\s*(?:Loc|Short)\(""(?<key>Str_\w+)""\)"),
        new(@"\bTile\((?:Loc|Short)\(""(?<key>Str_\w+)""\)"),
        new(@"\bnew\(""(?<key>Str_\w+)"",\s*""\\u"),
    ];

    private static readonly Regex ToolCardCall = new(@"OpenDocumentToolCard\(""[^""]+""(?<args>.*?)\);", RegexOptions.Singleline);

    internal static SortedSet<string> TileCaptionKeys()
    {
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string[] parts in TileSources)
        {
            string text = File.ReadAllText(Path.Combine([Root(), .. parts]));
            foreach (Regex pattern in TilePatterns)
                foreach (Match match in pattern.Matches(text)) keys.Add(match.Groups["key"].Value);
            foreach (Match call in ToolCardCall.Matches(text))
                foreach (Match key in Regex.Matches(call.Groups["args"].Value, @"^\s*\(""(?<key>Str_\w+)""", RegexOptions.Multiline))
                    keys.Add(key.Groups["key"].Value);
        }
        return keys;
    }

    private static Dictionary<string, string> Strings(string file) =>
        XDocument.Load(Path.Combine(Root(), "Strings", file)).Root!.Elements()
            .Where(element => element.Attribute(Xaml + "Key") is not null)
            .ToDictionary(element => element.Attribute(Xaml + "Key")!.Value, element => element.Value, StringComparer.Ordinal);

    // What QuickStrip shows: the short caption when there is one, and never a trailing "...".
    private static string Shown(Dictionary<string, string> strings, string key) =>
        (TileCaptions.ShortKeys.TryGetValue(key, out string? shortKey) ? strings[shortKey] : strings[key]).TrimEnd('.', '\u2026').TrimEnd();

    private static void OnSta(Action body)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { body(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.Message);
    }

    private static List<string> Overflowing(string file)
    {
        var strings = Strings(file);
        var failures = new List<string>();
        OnSta(() =>
        {
            var family = new FontFamily(new Uri(Root().TrimEnd('\\') + "\\"), "./Fonts/#Killer Sans UI, Segoe UI");
            var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            foreach (string key in TileCaptionKeys())
            {
                if (!strings.ContainsKey(key)) continue;
                string caption = Shown(strings, key);
                var text = new FormattedText(caption, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, 11, Brushes.Black, 1.0)
                {
                    MaxTextWidth = CaptionWidth
                };
                if (text.Height > CaptionMaxHeight || text.MinWidth > CaptionWidth)
                    failures.Add($"{key} \"{caption}\" ({text.MinWidth:0} px widest word, {text.Height:0} px tall)");
            }
        });
        return failures;
    }

    [Fact]
    public void TheCardMenusAreFoundAndMeasured()
    {
        var keys = TileCaptionKeys();
        foreach (string key in new[] { "Str_Ctx_OpenContainingFolder", "Str_Ctx_OcrPage", "Str_Menu_ReduceSize", "Str_Menu_Scan",
                     "Str_Exchange_ExportBookmarks", "Str_Repair_PageBox", "Str_FormTools_Flatten", "Str_Ctx_DuplicatePage" })
            Assert.Contains(key, keys);
    }

    [Fact]
    public void NoEnglishTileCaptionIsCutOff()
    {
        var failures = Overflowing("en-US.xaml");
        Assert.True(failures.Count == 0, "Tile captions that do not fit two lines of a tile:\n" + string.Join("\n", failures));
    }

    [Fact]
    public void EveryShortCaptionIsShorterAndInEveryLanguage()
    {
        var english = Strings("en-US.xaml");
        foreach (var (full, shortKey) in TileCaptions.ShortKeys)
        {
            Assert.True(english[shortKey].Length < english[full].TrimEnd('.', '\u2026').Length, shortKey + " is not shorter than " + full);
            foreach (string file in Directory.GetFiles(Path.Combine(Root(), "Strings"), "*.xaml"))
            {
                var strings = Strings(Path.GetFileName(file));
                Assert.True(strings.ContainsKey(shortKey) && strings[shortKey].Trim().Length > 0, Path.GetFileName(file) + " has no " + shortKey);
            }
        }
    }

    [Fact]
    public void ATileShowsTheShortCaptionAndKeepsTheFullNameForItsTooltipAndReader()
    {
        string strip = ShellSource.Between(ShellSource.Read("Shell", "PageCard.cs"),
            "private FrameworkElement QuickStrip(ContextMenu menu, int columns, params CardTile[] tiles)", "private static void ShowTileState(");
        Assert.Contains("Tag = shown", strip);
        Assert.Contains("ToolTip = gesture is null ? caption : WithGesture(caption, gesture)", strip);
        Assert.Contains("AutomationProperties.SetName(button, caption)", strip);
        Assert.Contains("TileCaptions.ShortKeys", ShellSource.Read("Shell", "PageCard.cs"));
    }
}
