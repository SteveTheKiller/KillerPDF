using System.Text;
using KillerPdf.Engine.Documents;
using Xunit;

namespace KillerPdf.Engine.Tests.Documents;

public sealed class PdfDataMergeTests
{
    [Fact]
    public void ExpandSupportsMissingValuePolicies()
    {
        var record = new Dictionary<string, string?> { ["Name"] = "Ada" };

        Assert.Equal("Hello Ada.", PdfDataMerge.Expand("Hello {{ Name }}.", record));
        Assert.Equal("Ada/", PdfDataMerge.Expand("{{Name}}/{{Code}}", record,
            PdfMissingMergeValueBehavior.Empty));
        Assert.Equal("Ada/{{Code}}", PdfDataMerge.Expand("{{Name}}/{{Code}}", record,
            PdfMissingMergeValueBehavior.KeepPlaceholder));
        Assert.Throws<KeyNotFoundException>(() => PdfDataMerge.Expand("{{Code}}", record));
        Assert.Throws<FormatException>(() => PdfDataMerge.Expand("{{Name", record));
    }

    [Fact]
    public void RunBatchIsolatesBadRecords()
    {
        IReadOnlyDictionary<string, string?>[] records =
        [
            new Dictionary<string, string?> { ["Value"] = "first" },
            new Dictionary<string, string?>(),
            new Dictionary<string, string?> { ["Value"] = "third" }
        ];

        IReadOnlyList<PdfDataMergeResult> results = PdfDataMerge.RunBatch(records, record =>
            Encoding.UTF8.GetBytes(PdfDataMerge.Expand("{{Value}}", record)));

        Assert.True(results[0].Succeeded);
        Assert.False(results[1].Succeeded);
        Assert.Contains("Value", results[1].Error, StringComparison.Ordinal);
        Assert.True(results[2].Succeeded);
        Assert.Equal("third", Encoding.UTF8.GetString(results[2].Data!.Value.Span));
    }
}
