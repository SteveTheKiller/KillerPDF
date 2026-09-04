using System.Text.Json;

namespace KillerPdf.Engine.Documents;

/// <summary>Combines print-production geometry, separations, and color-management facts.</summary>
public sealed record PdfPrintProductionReport(
    IReadOnlyList<PdfPageBoxInformation> Pages,
    IReadOnlyList<PdfSeparationColorant> Colorants,
    IReadOnlyList<PdfOutputIntentInformation> OutputIntents)
{
    /// <summary>Inspects the document without rendering or changing it.</summary>
    public static PdfPrintProductionReport Inspect(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        return new PdfPrintProductionReport(
            PdfPageBoxInformation.Read(document),
            PdfSeparationInspection.Inspect(document).Colorants,
            PdfOutputIntentInspection.Inspect(document));
    }

    /// <summary>Exports stable JSON without embedding ICC profile bytes.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(new
    {
        Version = 1,
        Pages,
        Colorants,
        OutputIntents = OutputIntents.Select(intent => new
        {
            intent.Subtype,
            intent.OutputConditionIdentifier,
            intent.OutputCondition,
            intent.RegistryName,
            intent.Information,
            Profile = new
            {
                intent.Profile.ColorSpace,
                intent.Profile.ComponentCount,
                ByteCount = intent.Profile.Data.Length
            }
        })
    }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = indented
    });
}
