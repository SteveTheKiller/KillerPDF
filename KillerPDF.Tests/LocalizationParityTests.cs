using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace KillerPDF.Tests;

public sealed class LocalizationParityTests
{
    private static readonly string StringsDirectory = FindStringsDirectory();
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void EveryLocaleHasTheEnglishKeysAndPlaceholders()
    {
        var english = ReadStrings(Path.Combine(StringsDirectory, "en-US.xaml"));
        foreach (var file in Directory.GetFiles(StringsDirectory, "*.xaml"))
        {
            var localized = ReadStrings(file);
            Assert.True(english.Keys.OrderBy(x => x).SequenceEqual(localized.Keys.OrderBy(x => x)),
                $"{Path.GetFileName(file)} does not contain exactly the English resource-key set.");

            foreach (var key in english.Keys)
            {
                Assert.False(string.IsNullOrWhiteSpace(localized[key]),
                    $"{Path.GetFileName(file)} has an empty value for {key}.");
                Assert.True(Placeholders(english[key]).SequenceEqual(Placeholders(localized[key])),
                    $"{Path.GetFileName(file)} has different placeholders for {key}.");
            }
        }
    }

    [Fact]
    public void Issue227SharedSurfacesHaveResourceKeys()
    {
        var english = ReadStrings(Path.Combine(StringsDirectory, "en-US.xaml"));
        string[] required =
        [
            "Str_Btn_OK", "Str_Btn_Yes", "Str_Btn_No", "Str_Btn_Cancel",
            "Str_RecentMissing",
            "Str_St_CopiedAnnotationOne", "Str_St_CopiedAnnotationMany",
            "Str_St_PastedAnnotationOne", "Str_St_PastedAnnotationMany",
            "Str_St_DeletedAnnotationOne", "Str_St_DeletedAnnotationMany",
            "Str_St_PlaceSignature", "Str_St_PlaceInitials",
            "Str_St_SelectionCleared", "Str_St_AnnotationsSelected",
            "Str_Search_NoMatches", "Str_Search_Error", "Str_Search_Summary",
            "Str_Search_PreviousTT", "Str_Search_NextTT", "Str_Search_CloseTT",
            "Str_Busy_FlattenPage", "Str_Busy_ExportPage", "Str_Busy_CancelHint",
            "Str_Busy_DownloadingMany", "Str_Print_NoTwoSidedSupport",
            "Str_Form_ClickInitials", "Str_Form_ClickSign", "Str_Form_Initial",
            "Str_Compare_PreviousRegion", "Str_Compare_NextRegion",
            "Str_Portable_InstallTip", "Str_Print_PreviewFailed",
            "Str_Print_PreparingToPrint", "Str_Link_GoToPage",
            "Str_Measurement_Unavailable", "Str_Repair_Unrecoverable"
        ];

        foreach (string key in required)
            Assert.True(english.ContainsKey(key), $"Missing #227 resource key: {key}");
    }

    [Fact]
    public void UiPropertiesDoNotIntroduceHardcodedEnglish()
    {
        string root = Directory.GetParent(StringsDirectory)!.FullName;
        var offenders = new List<string>();
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "KillerPDF", "Killer", "PDF", "T", "English", "Čeština", "Deutsch",
            "Español", "Français", "Magyar", "Italiano", "Polski", "Türkçe",
            "en-US", "bn", "cs-CZ", "de-DE", "es", "fr-FR", "hu-HU", "it-IT",
            "ja-JP", "kk-KZ", "pl-PL", "ru-RU", "tr-TR", "zh-CN", "zh-TW",
            " (F5)", " (F6)", " (F7)", " (F8)", "v1.6.3", "Steve the Killer",
            "https://killerpdf.net/help.html", "KillerPDF - ", "thekiller.net", "pt",
            "PNG", "JPEG"
        };

        foreach (string file in Directory.GetFiles(root, "*.xaml", SearchOption.AllDirectories)
                     .Where(path => !ExcludedSourcePath(root, path)))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                         @"\b(?:Text|Content|Header|ToolTip|Title)=""([^""{&]*[A-Za-z][^""]*)"""))
                if (!IsAllowedUiLiteral(match.Groups[1].Value, allowed))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {match.Groups[1].Value}");
        }

        foreach (string file in Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                     .Where(path => !ExcludedSourcePath(root, path)))
        {
            string source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source,
                         @"\b(?:Text|Content|Header|ToolTip|Title)\s*=\s*(?:[^;\r\n]*\?[^:\r\n]*:\s*)?\$?""([^""\r\n]*[A-Za-z][^""\r\n]*)"""))
                if (!IsAllowedUiLiteral(match.Groups[1].Value, allowed))
                    offenders.Add($"{Path.GetRelativePath(root, file)}: {match.Groups[1].Value}");
        }

        Assert.True(offenders.Count == 0,
            "Hardcoded UI text bypasses Strings resources:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void Issue227ReportedEnglishIsNotHardcodedInItsUiPaths()
    {
        string root = Directory.GetParent(StringsDirectory)!.FullName;
        var checks = new Dictionary<string, string[]>
        {
            ["Controls/KillerDialog.cs"] = ["MakeBtn(\"Yes\"", "MakeBtn(\"No\"", "MakeBtn(\"Cancel\""],
            ["Shell/FileOperations.cs"] = [": \"missing\"", "\"Exporting\"", "\"Flattening\""],
            ["Shell/ContextMenu.cs"] = ["Copied 1 annotation", "Pasted 1 annotation"],
            ["Controls/Viewer/PdfViewer.Annotations.cs"] = ["Deleted selected annotation", "Selection cleared", "annotations selected - press Delete to remove"],
            ["Shell/Signing.cs"] = ["Click on the page to place your signature", "Click on the page to place your initials"],
            ["Shell/Search.cs"] = ["Previous Match (Shift+Enter)", "Next Match (Enter)", "Close (Esc)"],
            ["Features/Search/SearchController.cs"] = ["SetResultText(\"No matches\")", "SetResultText(\"Search error\")"],
            ["Services/OcrLanguages.cs"] = ["(Esc to cancel)"]
        };

        foreach (var check in checks)
        {
            string source = File.ReadAllText(Path.Combine(root, check.Key.Replace('/', Path.DirectorySeparatorChar)));
            foreach (string text in check.Value)
                Assert.DoesNotContain(text, source);
        }
    }

    [Fact]
    public void BrowserHandoffMessagesAreNotHardcoded()
    {
        string root = Directory.GetParent(StringsDirectory)!.FullName;
        string source = File.ReadAllText(Path.Combine(root, "Shell", "ExternalOpen.cs"));
        string[] messages =
        [
            "KillerPDF could not open the browser PDF.",
            "The PDF is larger than the 256 MB browser handoff limit.",
            "The downloaded file is not a PDF.",
            "KillerPDF could not download the PDF.",
            "The PDF download timed out.",
            "KillerPDF could not save or read the downloaded PDF."
        ];

        foreach (string message in messages)
            Assert.DoesNotContain(message, source);
    }

    private static Dictionary<string, string> ReadStrings(string path) =>
        XDocument.Load(path).Root!.Elements()
            .Where(e => e.Attribute(Xaml + "Key") is not null)
            .ToDictionary(e => e.Attribute(Xaml + "Key")!.Value, e => e.Value);

    private static IEnumerable<string> Placeholders(string value) =>
        Regex.Matches(value, @"\{\d+(?::[^}]*)?\}").Cast<Match>().Select(m => m.Value).OrderBy(x => x);

    private static bool ExcludedSourcePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.StartsWith("Strings/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("Packaging/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("KillerPDF.Tests/", StringComparison.OrdinalIgnoreCase)
            || relative.StartsWith("engine/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/bin/", StringComparison.OrdinalIgnoreCase)
            || relative.Contains("/obj/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedUiLiteral(string value, HashSet<string> allowed) =>
        allowed.Contains(value)
        || value.Contains("Str_", StringComparison.Ordinal)
        || value.Contains('{')
        || value.Contains('}')
        || value.StartsWith("\\u", StringComparison.Ordinal)
        || value.StartsWith('#')
        || value.StartsWith('/')
        || value.All(character => !char.IsLetter(character));

    private static string FindStringsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "Strings")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "Strings");
    }
}
