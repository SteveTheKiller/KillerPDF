using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Documents;

/// <summary>Combines print-production geometry, separations, and color-management facts.</summary>
public sealed partial record PdfPrintProductionReport(
    IReadOnlyList<PdfPageBoxInformation> Pages,
    IReadOnlyList<PdfSeparationColorant> Colorants,
    IReadOnlyList<PdfOutputIntentInformation> OutputIntents)
{
    private static readonly PdfPrintProductionJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfPrintProductionJsonContext IndentedJson = new(JsonOptions(true));

    /// <summary>Inspects the document without rendering or changing it.</summary>
    public static PdfPrintProductionReport Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PdfPrintProductionReport(
            PdfPageBoxInformation.Read(document),
            PdfSeparationInspection.Inspect(document).Colorants,
            PdfOutputIntentInspection.Inspect(document));
    }

    /// <summary>Exports a readable summary without embedding ICC profile bytes.</summary>
    public string ToText()
    {
        var output = new StringBuilder();
        output.Append("Print production: pages ").Append(Pages.Count)
            .Append(", colorants ").Append(Colorants.Count)
            .Append(", output intents ")
            .AppendLine(OutputIntents.Count.ToString(CultureInfo.InvariantCulture));
        foreach (PdfPageBoxInformation page in Pages)
        {
            output.Append("  Page ")
                .Append((page.PageIndex + 1).ToString(CultureInfo.InvariantCulture))
                .AppendLine(":");
            AppendBox("MediaBox", page.MediaBox, "effective");
            AppendBox("CropBox", page.CropBox,
                page.HasExplicitCropBox ? "explicit" : "inherited");
            AppendBox("BleedBox", page.BleedBox,
                page.HasExplicitBleedBox ? "explicit" : "inherited");
            AppendBox("TrimBox", page.TrimBox,
                page.HasExplicitTrimBox ? "explicit" : "inherited");
            AppendBox("ArtBox", page.ArtBox,
                page.HasExplicitArtBox ? "explicit" : "inherited");
        }
        foreach (PdfSeparationColorant colorant in Colorants)
        {
            output.Append("  ").Append(colorant.IsProcess ? "Process" : "Spot")
                .Append(" colorant ").Append(colorant.Name).Append(": pages ")
                .AppendLine(PageList(colorant.PageIndexes));
        }
        foreach (PdfOutputIntentInformation intent in OutputIntents)
        {
            output.Append("  Output intent ").Append(Safe(intent.OutputConditionIdentifier))
                .Append(": ").Append(intent.Subtype).Append(", ")
                .Append(intent.Profile.ColorSpace).Append(", ")
                .Append(intent.Profile.ComponentCount.ToString(CultureInfo.InvariantCulture))
                .Append(" components, ")
                .Append(intent.Profile.Data.Length.ToString(CultureInfo.InvariantCulture))
                .AppendLine(" profile bytes");
        }
        return output.ToString().TrimEnd();

        void AppendBox(string name, PdfPageBoxBounds box, string source) => output
            .Append("    ").Append(name).Append(": ")
            .Append(Number(box.Left)).Append(", ").Append(Number(box.Bottom))
            .Append(" to ").Append(Number(box.Right)).Append(", ").Append(Number(box.Top))
            .Append(" pt (").Append(Number(box.Width)).Append(" x ")
            .Append(Number(box.Height)).Append(", ").Append(source)
            .AppendLine(")");
    }

    /// <summary>Exports stable JSON without embedding ICC profile bytes.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new ReportFile(1, Pages.ToArray(), Colorants.ToArray(),
            [.. OutputIntents.Select(intent => new OutputIntentFile(
                intent.Subtype, intent.OutputConditionIdentifier,
                intent.OutputCondition, intent.RegistryName, intent.Information,
                new ProfileFile(intent.Profile.ColorSpace,
                    intent.Profile.ComponentCount, intent.Profile.Data.Length)))]),
        indented ? IndentedJson.ReportFile : CompactJson.ReportFile);

    private sealed record ReportFile(int Version, PdfPageBoxInformation[] Pages,
        PdfSeparationColorant[] Colorants, OutputIntentFile[] OutputIntents);
    private sealed record OutputIntentFile(string Subtype,
        string OutputConditionIdentifier, string? OutputCondition,
        string? RegistryName, string? Information, ProfileFile Profile);
    private sealed record ProfileFile(string ColorSpace, int ComponentCount, int ByteCount);

    private static JsonSerializerOptions JsonOptions(bool indented) => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    };

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(ReportFile))]
    private sealed partial class PdfPrintProductionJsonContext : JsonSerializerContext;

    private static string Number(double value) =>
        value.ToString("G17", CultureInfo.InvariantCulture);

    private static string PageList(IEnumerable<int> pageIndexes) => string.Join(", ",
        pageIndexes.Select(index => (index + 1).ToString(CultureInfo.InvariantCulture)));

    private static string Safe(string value) => value
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
