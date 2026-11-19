using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfDataMergeWorkflowTests
{
    [Fact]
    public void MacroPlanUsesGeneratedNamesAndOnlySubsequentRenameSteps()
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "macro-plan");
        var profile = new PdfDataMergeProfile("Customers", [new("Name", "name")], "{{Name}}.pdf");
        var macro = new PdfMacro("Customers", [PdfRenameMacro.RenameStep("ignored"),
            profile.ToMacroStep(), PdfRenameMacro.RenameStep("{name}-ready-{index}")]);
        IReadOnlyDictionary<string, string?>[] records = [
            new Dictionary<string, string?> { ["Name"] = "Ada" },
            new Dictionary<string, string?> { ["Name"] = "Grace" }];

        var plan = PdfDataMergeWorkflow.PlanMacro(macro, records,
            System.IO.Path.Combine(folder, "template.pdf"), System.IO.Path.Combine(folder, "output.pdf"), 3);

        Assert.Equal(new[] { System.IO.Path.Combine(folder, "Ada-ready-3.pdf"),
            System.IO.Path.Combine(folder, "Grace-ready-3.pdf") }, plan.OutputPaths);
        Assert.Throws<ArgumentException>(() => PdfDataMergeWorkflow.PlanMacro(macro, records,
            System.IO.Path.Combine(folder, "Ada-ready-3.pdf"), System.IO.Path.Combine(folder, "output.pdf"), 3));
    }

    [Theory]
    [InlineData(PdfDataMergeOutputMode.Editable)]
    [InlineData(PdfDataMergeOutputMode.Flattened)]
    public void SavedProfileGeneratesConditionalImagesAndSelectedOutputMode(PdfDataMergeOutputMode mode)
    {
        string folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "killerpdf-merge-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(folder);
        try
        {
            string imagePath = System.IO.Path.Combine(folder, "logo.png");
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(
                System.Windows.Media.Imaging.BitmapSource.Create(1, 1, 96, 96,
                    System.Windows.Media.PixelFormats.Rgb24, null, new byte[] { 255, 0, 0 }, 3)));
            using (var stream = System.IO.File.Create(imagePath)) encoder.Save(stream);
            byte[] templateBytes = new PdfDocumentBuilder().AddBlankPage(300, 400)
                .AddTextField(0, "Name", 20, 20, 120, 24).Build();
            var profile = PdfDataMergeProfile.FromJson(new PdfDataMergeProfile("Letters",
                [new("Customer", "Name")], "{{Customer}}.pdf", imageMappings:
                [new("Image", 0, 20, 100, 40, 40)], includeWhenField: "Active", includeWhenValue: "yes").ToJson());
            IReadOnlyList<IReadOnlyDictionary<string, string?>> records = [
                new Dictionary<string, string?> { ["Customer"] = "Ada", ["Image"] = imagePath, ["Active"] = "yes" },
                new Dictionary<string, string?> { ["Customer"] = "Grace", ["Active"] = "no" }];
            var plan = PdfDataMergeWorkflow.CreatePlan(PdfDocument.Open(templateBytes), records, "letter.pdf", profile);
            var report = PdfDataMergeWorkflow.RunToDirectory(templateBytes, plan, folder, mode, CancellationToken.None,
                PdfDataMergeWorkflow.LoadImage, new HashSet<string>());
            Assert.Equal(1, report.SucceededRecords);
            Assert.Equal(0, report.FailedRecords);
            Assert.False(System.IO.File.Exists(System.IO.Path.Combine(folder, "Grace.pdf")));
            var result = PdfDocument.Open(System.IO.File.ReadAllBytes(System.IO.Path.Combine(folder, "Ada.pdf")));
            Assert.Single(new PdfPageContentReader(result).Read(0).Images);
            if (mode == PdfDataMergeOutputMode.Editable) Assert.Equal("Ada", Assert.Single(PdfFormDataExporter.Export(result).Fields).Values[0]);
            else Assert.Empty(PdfFormWidgetReader.ReadPage(result, 0));
        }
        finally { System.IO.Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void ConfiguredProfileMapsDifferentNamesFiltersRecordsAndRejectsDuplicateOutputs()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder().AddBlankPage()
            .AddTextField(0, "Name", 20, 20, 120, 24).Build());
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records = [
            new Dictionary<string, string?> { ["Customer"] = "Ada", ["Active"] = "yes" },
            new Dictionary<string, string?> { ["Customer"] = "Grace", ["Active"] = "no" }];
        var profile = new PdfDataMergeProfile("Letters", [new("Customer", "Name")],
            "{{Customer}}.pdf", includeWhenField: "Active", includeWhenValue: "yes");
        var plan = PdfDataMergeWorkflow.CreatePlan(template, records, "letter.pdf", profile);
        Assert.Equal(new[] { "Ada.pdf" }, plan.OutputFileNames);
        Assert.Equal("Ada", plan.Profile.Map(plan.Records[0]).FormData.Fields[0].Values[0]);
        var duplicates = new PdfDataMergeProfile("Letters", [new("Customer", "Name")], "same.pdf");
        Assert.Throws<ArgumentException>(() => PdfDataMergeWorkflow.CreatePlan(template, records, "letter.pdf", duplicates));
    }

    [Fact]
    public void CreatePlan_MatchesFieldAndMappingNamesAndBuildsUniqueOutputs()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextField(0, "CustomerName", 20, 20, 120, 24,
                fieldMetadata: new PdfFormFieldMetadata { MappingName = "Customer" })
            .AddTextField(0, "Invoice", 20, 60, 120, 24)
            .Build());
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
        [
            new Dictionary<string, string?> { ["Customer"] = "Ada", ["Invoice"] = "100" },
            new Dictionary<string, string?> { ["Customer"] = "Grace", ["Invoice"] = "101" }
        ];

        PdfDataMergePlan plan = PdfDataMergeWorkflow.CreatePlan(template, records, "letters.pdf");

        Assert.Equal(2, plan.MatchedFieldCount);
        Assert.Equal(["letters.pdf", "letters-2.pdf"], plan.OutputFileNames);
        Assert.Equal("letters.pdf", plan.Profile.Map(plan.Records[0]).OutputFileName);
        Assert.Equal("letters-2.pdf", plan.Profile.Map(plan.Records[1]).OutputFileName);
        Assert.Equal(["CustomerName", "Invoice"],
            plan.Profile.Mappings.Select(mapping => mapping.TargetField));
    }

    [Fact]
    public void CreatePlan_RejectsDataWithoutMatchingTemplateFields()
    {
        PdfDocument template = PdfDocument.Open(new PdfDocumentBuilder()
            .AddBlankPage()
            .AddTextField(0, "Name", 20, 20, 120, 24)
            .Build());
        IReadOnlyList<IReadOnlyDictionary<string, string?>> records =
            [new Dictionary<string, string?> { ["Unrelated"] = "value" }];

        // A typed exception so the dialog can show its own localized reason (#506).
        Assert.Throws<DataMergeNoColumnsException>(() =>
            PdfDataMergeWorkflow.CreatePlan(template, records, "output.pdf"));
    }
}
