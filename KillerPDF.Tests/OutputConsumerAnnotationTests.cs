using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Windows;
using KillerPDF.Services;
using KillerPDF.Services.Signing;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPDF.Tests;

// A save keeps text added with the Text tool as an editable FreeText annotation, and most operations that start from
// the saved copy carry that annotation through. The ones that rebuild pages from content (production layouts) or read
// only page content (text exports) lose it, so their copy is flattened (drawn into the page) instead, as 1.9.2 did.
// A privacy redaction is the reverse: text under a marked area must be gone from the output wherever it was kept.
public sealed class OutputConsumerAnnotationTests
{
    private const string Marker = "MARKERTEXT";

    // The copy WriteSavedCopyToTemp() writes: an editable annotation, or page content when flattened for output.
    private static byte[] SavedCopyWithAddedText(bool flattenEditing = false, byte[]? basePdf = null)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-consumer-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, basePdf ?? new PdfDocumentBuilder().AddBlankPage(612, 792).AddBlankPage(612, 792).Build());
            var annotations = new Dictionary<int, List<PageAnnotation>>
            {
                [0] = [new TextAnnotation { PageIndex = 0, Content = Marker, Position = new Point(60, 400), Width = 220, Height = 30 }]
            };
            PdfEngineBurn.Burn(path, annotations, new Dictionary<int, (int w, int h)> { [0] = (612, 792) },
                measurementsAsAnnotations: true, flattenEditing: flattenEditing);
            byte[] saved = File.ReadAllBytes(path);
            // The probe means something only when the copy holds the text in the form it is meant to hold it.
            PdfDocument document = PdfDocument.Open(saved);
            bool annotation = PdfCommentReader.Read(document, true).Any(comment => comment.Subtype == "FreeText"
                && comment.Contents.Contains(Marker, StringComparison.Ordinal));
            Assert.Equal(!flattenEditing, annotation);
            return saved;
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    private static bool Keeps(byte[] output)
    {
        PdfDocument document = PdfDocument.Open(output);
        var reader = new PdfPageContentReader(document);
        for (int page = 0; page < PdfPageBoxInformation.Read(document).Count; page++)
            if (reader.Read(page).Text.Contains(Marker, StringComparison.Ordinal)) return true;
        return PdfCommentReader.Read(document, true).Any(comment => comment.Subtype == "FreeText"
            && comment.Contents.Contains(Marker, StringComparison.Ordinal));
    }

    private static PdfMacroDispatcher Dispatcher() => new([
        new(PdfMacroOperation.Watermark, PdfEngineBurn.ExecuteWatermarkMacro),
        new(PdfMacroOperation.Resize, PdfResizeMacro.Execute),
        new(PdfMacroOperation.ConvertColor, WorkingCmykProfiles.ConvertColor)]);

    private static PdfMacroStep StepNamed(string name)
    {
        var landscape = new PdfImpositionPreset("Two-up", 2, 1, 792, 612, margin: 18, gutter: 12);
        return name switch
        {
            "Optimize" => new(PdfMacroOperation.Optimize),
            "NumberPages" => PdfPageFurnitureMacro.NumberPagesStep(new PdfPageNumberMacroOptions
                { Date = new DateOnly(2026, 9, 7), FontSize = 12, VerticalMargin = 10 }),
            "Flatten" => new(PdfMacroOperation.Flatten),
            "Resize" => PdfResizeMacro.ScaleStep(50),
            "ConvertColor" => PdfColorConversionMacro.ConvertStep(PdfMacroColorMode.Grayscale),
            "Watermark" => PdfEngineBurn.WatermarkStep(new("REVIEW")),
            "RemoveUnsafeLinks" => PdfNavigationMacro.RemoveUnsafeLinksStep(),
            "RemoveUnresolvedLinks" => PdfNavigationMacro.RemoveUnresolvedLinksStep(),
            "GenerateBookmarks" => PdfNavigationMacro.HeadingBookmarksStep(),
            "GenerateTableOfContents" => PdfNavigationMacro.TableOfContentsStep(),
            "SetPageLabels" => PdfNavigationMacro.PageLabelsStep([new PdfPageLabelMacroRange(0, PdfPageLabelStyle.Decimal)]),
            "RemoveAttachments" => PdfAttachmentMacro.RemoveStep(),
            "ImposeNUp" => PdfImpositionMacro.NUpStep(landscape),
            "ImposeBooklet" => PdfImpositionMacro.BookletStep(new PdfImpositionPreset("Booklet", 2, 1, 792, 612,
                margin: 18, gutter: 12, duplex: true), 0),
            "ImposeStepAndRepeat" => PdfImpositionMacro.StepAndRepeatStep(landscape, 0, 2),
            "ImposeCutStack" => PdfImpositionMacro.CutStackStep(landscape),
            "ImposeManualSequence" => PdfImpositionMacro.ManualSequenceStep(landscape, [0, 1]),
            "ImposePoster" => PdfImpositionMacro.PosterStep(new PdfImpositionPreset("Poster", 1, 1, 320, 420, margin: 10), 0, 20),
            _ => throw new ArgumentOutOfRangeException(nameof(name))
        };
    }

    // Steps measured to carry the annotation through from the unflattened saved copy.
    public static TheoryData<string> KeepingSteps => new()
    {
        "Optimize", "NumberPages", "Flatten", "Resize", "ConvertColor", "Watermark", "RemoveUnsafeLinks",
        "RemoveUnresolvedLinks", "GenerateBookmarks", "GenerateTableOfContents", "SetPageLabels", "RemoveAttachments"
    };

    // Steps measured to drop it: they rebuild every page from its content.
    public static TheoryData<string> RebuildingSteps => new()
    {
        "ImposeNUp", "ImposeBooklet", "ImposeStepAndRepeat", "ImposeCutStack", "ImposeManualSequence", "ImposePoster"
    };

    [Theory]
    [MemberData(nameof(KeepingSteps))]
    public void AMacroStepKeepsTheAddedTextFromTheEditableSavedCopy(string step) =>
        Assert.True(Keeps(Dispatcher().Execute(StepNamed(step), SavedCopyWithAddedText()).Data.ToArray()),
            $"{step} dropped the text added with the Text tool.");

    [Theory]
    [MemberData(nameof(RebuildingSteps))]
    public void AProductionLayoutStepKeepsTheAddedTextFromTheFlattenedCopy(string step) =>
        Assert.True(Keeps(Dispatcher().Execute(StepNamed(step), SavedCopyWithAddedText(flattenEditing: true)).Data.ToArray()),
            $"{step} dropped the text added with the Text tool.");

    [Theory]
    [MemberData(nameof(RebuildingSteps))]
    [InlineData("Export")]
    [InlineData("Save")]
    [InlineData("Redact")]
    [InlineData("Ocr")]
    [InlineData("DataMerge")]
    [InlineData("Rename")]
    public void OnlyMacrosThatRebuildPagesOrExportTextAskForTheFlattenedCopy(string step)
    {
        PdfMacro macro = new("Probe", [new(PdfMacroOperation.Optimize),
            step switch
            {
                "Export" => PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText),
                "Save" or "Redact" or "Ocr" or "DataMerge" or "Rename" => new(Enum.Parse<PdfMacroOperation>(step)),
                _ => StepNamed(step)
            }]);
        bool rebuilds = step.StartsWith("Impose", StringComparison.Ordinal) || step == "Export";
        Assert.Equal(rebuilds, MacroSourceCopy.NeedsAddedTextInPageContent(macro));
    }

    [Theory]
    [MemberData(nameof(KeepingSteps))]
    public void AMacroOfKeepingStepsStaysEditable(string step) =>
        Assert.False(MacroSourceCopy.NeedsAddedTextInPageContent(new("Probe", [StepNamed(step)])));

    [Fact]
    public void TheMacroExportStepKeepsTheAddedTextFromTheFlattenedCopy()
    {
        PdfMacroExport export = Assert.Single(new PdfMacroDispatcher().Execute(
            PdfStructuredExportMacro.Step(PdfStructuredExportFormat.PlainText),
            SavedCopyWithAddedText(flattenEditing: true)).Exports);
        Assert.Contains(Marker, Encoding.UTF8.GetString(export.Data.Span), StringComparison.Ordinal);
    }

    [Fact]
    public void TheMacroDataMergeStepKeepsTheAddedTextFromTheEditableSavedCopy()
    {
        byte[] template = new PdfDocumentBuilder().AddPage(612, 792,
            new PdfContentStreamBuilder().SetFillRgb(1, 0, 0).Rectangle(0, 700, 612, 92).Fill())
            .AddTextField(0, "name", 20, 20, 140, 24).Build();
        var profile = new PdfDataMergeProfile("Customers", [new("Name", "name")], "{{Name}}.pdf");
        IReadOnlyDictionary<string, string?>[] records = [new Dictionary<string, string?> { ["Name"] = "Ada" }];
        var dispatcher = new PdfMacroDispatcher([new(PdfMacroOperation.DataMerge,
            (step, data, _, token) => PdfDataMergeMacro.Execute(step, data, records, token))]);

        PdfMacroOperationResult result = dispatcher.Execute(profile.ToMacroStep(), SavedCopyWithAddedText(basePdf: template));

        Assert.True(Keeps(Assert.Single(result.Outputs!).Data!.Value.ToArray()), "Data Merge dropped the text added with the Text tool.");
    }

    [Theory]
    [InlineData(PdfStructuredExportFormat.PlainText)]
    [InlineData(PdfStructuredExportFormat.Html)]
    [InlineData(PdfStructuredExportFormat.Markdown)]
    [InlineData(PdfStructuredExportFormat.Json)]
    public void ExportKeepsTheAddedTextFromTheFlattenedCopy(PdfStructuredExportFormat format)
    {
        byte[] output = PdfStructuredExport.Export(PdfDocument.Open(SavedCopyWithAddedText(flattenEditing: true)), format);
        Assert.Contains(Marker, Encoding.UTF8.GetString(output), StringComparison.Ordinal);
    }

    [Fact]
    public void ExtractPagesKeepsTheAddedTextFromTheEditableSavedCopy()
    {
        string source = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-src-{Guid.NewGuid():N}.pdf");
        string destination = Path.Combine(Path.GetTempPath(), $"killerpdf-extract-dst-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(source, SavedCopyWithAddedText());
            PdfEngineIntegration.ExtractPages(source, destination, [0], new Dictionary<int, int>());
            Assert.True(Keeps(File.ReadAllBytes(destination)));
        }
        finally { foreach (string path in new[] { source, destination }) if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void SigningKeepsTheAddedTextFromTheEditableSavedCopy()
    {
        string source = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-src-{Guid.NewGuid():N}.pdf");
        string output = Path.Combine(Path.GetTempPath(), $"killerpdf-sign-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(source, SavedCopyWithAddedText());
            using RSA key = RSA.Create(2048);
            using X509Certificate2 certificate = new CertificateRequest("CN=KillerPDF Probe", key,
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
                .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));
            new PdfSigner().Sign(source, output, certificate, new PdfSigner.SignInfo("Approved", "", ""));
            Assert.True(Keeps(File.ReadAllBytes(output)));
        }
        finally { foreach (string path in new[] { source, output }) if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void ReduceSizeKeepsTheAddedTextFromTheEditableSavedCopy()
    {
        string source = Path.Combine(Path.GetTempPath(), $"killerpdf-reduce-src-{Guid.NewGuid():N}.pdf");
        string output = Path.Combine(Path.GetTempPath(), $"killerpdf-reduce-out-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(source, SavedCopyWithAddedText());
            PdfEngineIntegration.ReduceFileSize(source, output, null, CancellationToken.None);
            Assert.True(Keeps(File.ReadAllBytes(output)));
        }
        finally { foreach (string path in new[] { source, output }) if (File.Exists(path)) File.Delete(path); }
    }

    // The text sits 60..280 across and 362..392 up from the page bottom.
    private static byte[] Redact(PdfContentBounds area)
    {
        string path = Path.Combine(Path.GetTempPath(), $"killerpdf-redact-{Guid.NewGuid():N}.pdf");
        try
        {
            File.WriteAllBytes(path, SavedCopyWithAddedText());
            PdfEngineIntegration.ApplyRedaction(path, [new PdfRedactionRegion(0, area)], []);
            return File.ReadAllBytes(path);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void RedactingAnAreaElsewhereKeepsTheAddedText() =>
        Assert.True(Keeps(Redact(new PdfContentBounds(40, 40, 100, 100))));

    [Fact]
    public void RedactingTheAreaUnderTheAddedTextRemovesItFromTheOutput() =>
        Assert.False(Keeps(Redact(new PdfContentBounds(40, 340, 300, 420))),
            "The text under the redacted area is still in the output.");

    // The shell asks for the flattened copy only where the probes above show the editable one loses the text.
    private static string Shell(string file)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "KillerPDF.csproj")))
            directory = directory.Parent;
        return File.ReadAllText(Path.Combine(directory!.FullName, "Shell", file));
    }

    [Fact]
    public void StructuredExportAsksForTheFlattenedCopy() =>
        Assert.Contains("forRasterization: officeMode == PdfOfficeExportMode.PageImages, throwaway: true, flattenEditing: true",
            Shell("FileOperations.cs"));

    [Theory]
    [InlineData("Macros.cs", "SavedCopy macroCopy = WriteSavedCopyToTemp(flattenEditing: MacroSourceCopy.NeedsAddedTextInPageContent(macro));")]
    [InlineData("MacroDataMerge.cs", "SavedCopy mergeCopy = WriteSavedCopyToTemp(flattenEditing: MacroSourceCopy.NeedsAddedTextInPageContent(macro));")]
    public void MacroRunsAskForTheFlattenedCopyOnlyWhenTheirStepsNeedIt(string file, string call) =>
        Assert.Contains(call, Shell(file));
}
