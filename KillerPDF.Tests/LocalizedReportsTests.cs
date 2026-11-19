using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Diagnostics;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Signing;
using KillerPDF.Services;
using Xunit;

namespace KillerPDF.Tests;

/// <summary>
/// KillerPDF #506: the report windows build their text from the engine's results through the app's strings, so a
/// German window shows no English engine text. A pseudo-locale turns every letter of every string (or of the key,
/// before a key is translated) into x, keeping placeholders; any English word left in the output was written
/// by code instead of coming from a string.
/// </summary>
public sealed class LocalizedReportsTests
{
    private static readonly string[] EngineEnglish =
    [
        "Print production", "pages", "Page ", "colorants", "output intents", "effective", "explicit", "inherited",
        "Separation preview", "plates", "no selected plates", "Layers:", "Configurations", "visible", "hidden",
        "unlocked", "Default", "base state", "PDF portfolio", "Initial document", "Fields:", "Sort rules", "Folders",
        "Signatures:", "Structural comparison", "changes found", "no changes", "Changes:", "PageAdded", "PageRemoved",
        "Navigation findings", "Suggested repair", "Preflight profile", "Result:", "No findings", "Passed", "Failed",
        "original", "changed", "object", "source", "none", "unnamed",
        "Accessibility result", "Data merge records", "succeeded", "Succeeded", "skipped", "failed", "Canceled",
        "Macro run", "not started", "Input ", "Record ", "Unnamed output", "General PDF", "Attachment safety",
        "Print production", "Error:", " at ", "document", "has no",
    ];

    private static readonly Lazy<System.Collections.Generic.Dictionary<string, string>> English = new(() =>
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Strings", "en-US.xaml"))) dir = dir.Parent;
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var strings = XDocument.Load(Path.Combine(dir!.FullName, "Strings", "en-US.xaml")).Root!.Elements()
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(element => (string)element.Attribute(x + "Key")!, element => element.Value);
        return strings;
    });

    private static string Pseudo(string key)
    {
        string template = English.Value.TryGetValue(key, out string? value) ? value : key;
        // Letters outside {n} placeholders become x.
        return Regex.Replace(template, @"\{\d+\}|[A-Za-z]", match => match.Value.Length > 1 ? match.Value : "x");
    }

    private static void AssertNoEngineEnglish(string report)
    {
        foreach (string word in EngineEnglish)
            Assert.False(report.Contains(word, StringComparison.Ordinal), $"\"{word}\" is English engine text in:\n{report}");
    }

    private static string WithFile(byte[] pdf, Func<string, string> run)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-506-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, pdf);
            return run(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static byte[] A4(int pages = 1)
    {
        var builder = new PdfDocumentBuilder();
        for (int index = 0; index < pages; index++) builder.AddPage(595.276, 841.89, ReadOnlyMemory<byte>.Empty);
        return builder.Build();
    }

    [Fact]
    public void PrintProduction_EveryLineComesFromStrings()
    {
        string report = WithFile(A4(), path => LocalizedReports.PrintProduction(
            PdfEngineIntegration.InspectPrintProduction(path, null), Pseudo, CultureInfo.InvariantCulture));
        Assert.Contains("MediaBox", report);
        Assert.Contains("TrimBox", report);
        AssertNoEngineEnglish(report);
    }

    [Theory]
    [InlineData("en-US", "595.28", "841.89")]
    [InlineData("de-DE", "595,28", "841,89")]
    public void PrintProduction_CoordinatesHaveTwoDecimalsInTheAppCulture(string culture, string width, string height)
    {
        static string Loc(string key) => key == "Str_Rep_PP_Box" ? "{0}: {1}; {2} | {3}; {4} ({5} x {6}, {7})" : Pseudo(key);
        string report = WithFile(A4(), path => LocalizedReports.PrintProduction(
            PdfEngineIntegration.InspectPrintProduction(path, null), Loc, new CultureInfo(culture)));
        Assert.Contains($"MediaBox: 0; 0 | {width}; {height} ({width} x {height}", report);
        Assert.DoesNotContain("595.27599", report);
        Assert.DoesNotContain("-0", report);
    }

    [Fact]
    public void SeparationPreview_EveryLineComesFromStrings()
    {
        var preview = new PdfSeparationPreview(
            [new PdfSeparationPreviewPlate("Black", true, [0]), new PdfSeparationPreviewPlate("Killer Orange", false, [1])],
            [new PdfSeparationPreviewPage(0, ["Black"]), new PdfSeparationPreviewPage(1, [])]);
        string report = LocalizedReports.SeparationPreview(preview, Pseudo, CultureInfo.InvariantCulture);
        Assert.Contains("Killer Orange", report);
        AssertNoEngineEnglish(report);
    }

    [Fact]
    public void LayersPortfolioSignaturesAndPreflight_EveryLineComesFromStrings()
    {
        WithFile(A4(), path =>
        {
            AssertNoEngineEnglish(LocalizedReports.Layers(PdfEngineIntegration.InspectLayers(path, null), Pseudo));
            AssertNoEngineEnglish(LocalizedReports.Portfolio(PdfEngineIntegration.ReadPortfolio(path, null), Pseudo));
            AssertNoEngineEnglish(LocalizedReports.Signatures(PdfEngineIntegration.InspectSignatures(path, null), Pseudo));
            var preflight = PdfEngineIntegration.InspectPreflight(path);
            string report = LocalizedReports.Preflight(preflight, Pseudo);
            Assert.NotEmpty(preflight.Findings);
            AssertNoEngineEnglish(report);
            foreach (PdfPreflightFinding finding in preflight.Findings)
            {
                Assert.DoesNotContain(finding.Code, report);
                Assert.DoesNotContain(finding.Message, report);
            }
            return "";
        });
    }

    [Fact]
    public void Layers_ConfigurationAndGroupLinesComeFromStrings()
    {
        var layers = new PdfOptionalContentInfo
        {
            Groups = [new PdfOptionalContentGroupInfo { ObjectNumber = 7, Name = "Dimensions", IsInitiallyVisible = true }],
            Configurations = [new PdfOptionalContentConfigurationInfo { IsDefault = true, Creator = "CAD" }],
        };
        string report = LocalizedReports.Layers(layers, Pseudo, CultureInfo.InvariantCulture);
        Assert.Contains("Dimensions", report);
        AssertNoEngineEnglish(report);
    }

    [Fact]
    public void Portfolio_FieldsSortAndFoldersComeFromStrings()
    {
        var portfolio = new PdfCollectionInfo
        {
            View = PdfCollectionView.Tile,
            Fields = [new PdfCollectionFieldInfo { Key = "Size", DisplayName = "Größe", Order = 1, IsVisible = true }],
            Sort = [new PdfCollectionSortInfo("Size", false)],
            Folders = [new PdfCollectionFolderInfo(1, 9, "Verträge", "Alle", null, null, null, 0)],
        };
        string report = LocalizedReports.Portfolio(portfolio, Pseudo, CultureInfo.InvariantCulture);
        Assert.Contains("Verträge", report);
        AssertNoEngineEnglish(report);
        AssertNoEngineEnglish(LocalizedReports.Portfolio(null, Pseudo));
    }

    [Fact]
    public void StructuralComparison_ChangeKindsComeFromStrings()
    {
        string report = WithFile(A4(1), first => WithFile(A4(2), second => LocalizedReports.StructuralComparison(
            PdfEngineIntegration.CompareStructure(first, null, second, null), Pseudo, CultureInfo.InvariantCulture)));
        Assert.NotEqual("", report);
        AssertNoEngineEnglish(report);
    }

    [Fact]
    public void NavigationAudit_EveryCodeHasItsOwnMessage()
    {
        var findings = Enum.GetValues<PdfNavigationFindingCode>().Select(code => new PdfNavigationFinding(code,
            code.ToString().StartsWith("Link", StringComparison.Ordinal) ? "Link" : "Action", 0, "Kapitel 3",
            "The engine message.", PdfNavigationRepairKind.Remove, 12)).ToArray();
        string report = LocalizedReports.NavigationAudit(findings, Pseudo, CultureInfo.InvariantCulture);
        Assert.DoesNotContain("engine message", report);
        Assert.Contains("Kapitel 3", report);
        AssertNoEngineEnglish(report);
        foreach (PdfNavigationFindingCode code in Enum.GetValues<PdfNavigationFindingCode>())
            Assert.DoesNotContain(code.ToString(), report);
    }

    private static void AssertKey(string key) =>
        Assert.True(English.Value.ContainsKey(key), $"{key} is missing from the strings.");

    private static string EngineSource(string fileName)
    {
        DirectoryInfo? dir = new(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "engine", "KillerPdf.Engine"))) dir = dir.Parent;
        return File.ReadAllText(Path.Combine(dir!.FullName, "engine", "KillerPdf.Engine", "Diagnostics", fileName));
    }

    [Fact]
    public void Preflight_EveryEngineCodeHasItsOwnKey()
    {
        // The fixed codes are string literals in the engine, so read them from its source instead of a list here.
        var codes = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
        {
            "DocumentChecksUnavailable", "PageBoxes.BleedOutsideMediaBox", "PageBoxes.TrimOutsideMediaBox",
            "PageBoxes.ArtOutsideMediaBox",
        };
        foreach (string file in (string[])["PdfPreflightDocumentChecks.cs", "PdfConformanceInspection.cs"])
            foreach (Match match in Regex.Matches(EngineSource(file),
                         @"(?:Error|Warning|Information|Unsupported|AddMissing)\([^""]*?""([A-Za-z]+\.[A-Za-z]+)"""))
                codes.Add(match.Groups[1].Value);
        Assert.True(codes.Count > 45, string.Join(", ", codes));
        Assert.Equal(codes.Order(), LocalizedReports.KnownPreflightCodes.Order());
        foreach (PdfDiagnosticCode code in Enum.GetValues<PdfDiagnosticCode>()) codes.Add("Structural." + code);
        foreach (PdfAccessibilityFindingCode code in Enum.GetValues<PdfAccessibilityFindingCode>())
        {
            codes.Add("Accessibility." + code);
            codes.Add("Conformance.PdfUa." + code);
        }
        codes.Add("Conformance.PdfUa1.06-003");
        foreach (string code in codes)
        {
            string? key = LocalizedReports.PreflightKey(code);
            Assert.True(key is not null, $"{code} has no key.");
            AssertKey(key!);
            int placeholders = Regex.Matches(English.Value[key!], @"\{\d+\}").Select(match => match.Value).Distinct().Count();
            Assert.True(LocalizedReports.PreflightArgumentCount(code) == placeholders,
                $"{code} takes {LocalizedReports.PreflightArgumentCount(code)} arguments but {key} has {placeholders} placeholders.");
        }
        foreach (string key in (string[])["Str_Rep_Pre_Profile_General", "Str_Rep_Pre_Profile_Attachments",
                     "Str_Rep_Pre_Profile_PrintProduction"]) AssertKey(key);
    }

    [Fact]
    public void Preflight_MessagesComeFromStringsAndUnknownCodesKeepTheEngineText()
    {
        var findings = LocalizedReports.KnownPreflightCodes
            .Concat(["Conformance.PdfA.OutputIntent.Missing", "Conformance.PdfUa1.06-003", "Structural.InvalidHeader",
                "Accessibility.MissingTableHeader"])
            .Select(code => new PdfPreflightFinding(code, PdfDiagnosticSeverity.Error, "The engine message.", 2, 9)
            {
                Arguments = LocalizedReports.PreflightDetailCodes.Contains(code) ? ["The engine message."]
                    : [.. Enumerable.Range(0, LocalizedReports.PreflightArgumentCount(code)).Select(index => code switch
                    {
                        "OptionalContent.LayerState" => index switch { 0 => "Bemaßung", 1 or 2 => "true", _ => "off" },
                        "ImageResolution.BelowMinimum" or "ColorUsage.InkCoverageAboveMaximum" => "72.123456",
                        _ => "Wert" + index
                    })]
            })
            .Append(new PdfPreflightFinding("Future.Check", PdfDiagnosticSeverity.Warning, "A future message."))
            .ToArray();
        var report = (PdfPreflightReport)Activator.CreateInstance(typeof(PdfPreflightReport),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [PdfPreflightProfile.Attachments.Name, findings], CultureInfo.InvariantCulture)!;
        string text = LocalizedReports.Preflight(report, Pseudo, CultureInfo.InvariantCulture);
        string[] lines = text.Split('\n');
        AssertNoEngineEnglish(string.Join("\n", lines.Where(line => !line.Contains("Future.Check"))));
        Assert.Contains("PDF/A-4", text);
        Assert.Contains("Bemaßung", text);
        Assert.Contains("72.12", text);
        Assert.DoesNotContain("72.123", text);
        Assert.Contains("Wert0", text);
        Assert.Contains("Future.Check: A future message.", text);
        Assert.Equal(LocalizedReports.PreflightDetailCodes.Length,
            lines.Count(line => line.Contains("The engine message.")));
    }

    [Fact]
    public void Accessibility_EveryCodeHasItsOwnMessage()
    {
        var findings = Enum.GetValues<PdfAccessibilityFindingCode>().Select(code => new PdfAccessibilityFinding(
            code, PdfDiagnosticSeverity.Error, "The engine message.", 0, 4)
        { FieldName = code == PdfAccessibilityFindingCode.MissingFormFieldDescription ? "Kundennummer" : null }).ToArray();
        var report = (PdfAccessibilityReport)Activator.CreateInstance(typeof(PdfAccessibilityReport),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null,
            [findings], CultureInfo.InvariantCulture)!;
        string text = LocalizedReports.Accessibility(report, Pseudo, CultureInfo.InvariantCulture);
        foreach (PdfAccessibilityFindingCode code in Enum.GetValues<PdfAccessibilityFindingCode>())
        {
            AssertKey("Str_Rep_A11y_" + code);
            Assert.DoesNotContain(code.ToString(), text);
        }
        AssertKey("Str_Rep_A11y_Result");
        Assert.Contains("Kundennummer", text);
        Assert.Single(text.Split('\n'), line => line.Contains("The engine message."));
        AssertNoEngineEnglish(text.Replace("The engine message.", ""));
        WithFile(A4(), path =>
        {
            AssertNoEngineEnglish(LocalizedReports.Accessibility(
                PdfAccessibilityInspector.Inspect(KillerPdf.Engine.Documents.PdfDocument.Open(File.ReadAllBytes(path))),
                Pseudo));
            return "";
        });
    }

    [Fact]
    public void DataMerge_EveryLineComesFromStrings()
    {
        var report = PdfDataMergeBatchReport.Create([
            new PdfDataMergeDocumentResult(0, "Rechnung 1.pdf", new byte[1], null),
            new PdfDataMergeDocumentResult(1, null, null, null) { Skipped = true },
            new PdfDataMergeDocumentResult(2, "Rechnung 3.pdf", null, "Feld fehlt"),
        ]);
        string text = LocalizedReports.DataMerge(report, Pseudo, CultureInfo.InvariantCulture);
        Assert.Contains("Rechnung 1.pdf", text);
        Assert.Contains("Feld fehlt", text);
        AssertNoEngineEnglish(text);
        foreach (string key in (string[])["Str_Rep_Merge_Summary", "Str_Rep_Record", "Str_Rep_RecordOutput",
                     "Str_Rep_Error", "Str_Rep_Outcome_Succeeded", "Str_Rep_Outcome_Skipped", "Str_Rep_Outcome_Failed"])
            AssertKey(key);
    }

    [Fact]
    public void MacroRun_EveryLineComesFromStrings()
    {
        var report = new PdfMacroRunReport(4, [
            new PdfMacroFileResult(0, new byte[1], null, false) { OutputFileName = "Ausgabe.pdf" },
            new PdfMacroFileResult(1, null, "Schritt fehlgeschlagen", false) { FailedOperation = PdfMacroOperation.Rename },
            new PdfMacroFileResult(2, null, null, true),
            new PdfMacroFileResult(2, null, null, false)
            {
                Outputs = [new PdfMacroOutput(new byte[1], "Brief 1.pdf", RecordIndex: 0),
                    new PdfMacroOutput(null, null, "Datensatz kaputt", 1) { FailedOperation = PdfMacroOperation.Rename }]
            },
        ]);
        string text = LocalizedReports.MacroRun(report, operation => "Umbenennen", Pseudo, CultureInfo.InvariantCulture);
        Assert.Contains("Ausgabe.pdf", text);
        Assert.Contains("Umbenennen", text);
        Assert.Contains("Datensatz kaputt", text);
        Assert.DoesNotContain("Rename", text);
        AssertNoEngineEnglish(text);
        foreach (string key in (string[])["Str_Rep_Macro_Summary", "Str_Rep_Macro_Input", "Str_Rep_Macro_SucceededOutput",
                     "Str_Rep_Macro_FailedAt", "Str_Rep_Outcome_Canceled", "Str_Rep_Unnamed"])
            AssertKey(key);
    }
}
