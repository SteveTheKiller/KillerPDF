using KillerPDF.Services;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Parsing;
using Xunit;

namespace KillerPDF.Tests;

public sealed class PdfDataMergeWorkflowTests
{
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

        Assert.Throws<InvalidOperationException>(() =>
            PdfDataMergeWorkflow.CreatePlan(template, records, "output.pdf"));
    }
}
