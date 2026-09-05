using KillerPdf.Engine.Syntax;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>Severity assigned to a structural inspection finding.</summary>
public enum PdfDiagnosticSeverity
{
    /// <summary>Informational context that does not indicate damage.</summary>
    Information,
    /// <summary>A suspicious condition that does not prevent structural use.</summary>
    Warning,
    /// <summary>The requested check is recognized but unavailable for this document or engine build.</summary>
    Unsupported,
    /// <summary>A structural failure requiring repair or rejection.</summary>
    Error
}

/// <summary>Stable machine-readable categories produced by structural inspection.</summary>
public enum PdfDiagnosticCode
{
    /// <summary>The PDF header is missing or malformed.</summary>
    InvalidHeader,
    /// <summary>The final startxref declaration is missing or malformed.</summary>
    InvalidStartXref,
    /// <summary>Cross-reference data is malformed or inconsistent.</summary>
    InvalidCrossReference,
    /// <summary>An indirect object cannot be parsed or resolved safely.</summary>
    InvalidIndirectObject,
    /// <summary>The effective trailer does not register a catalog root.</summary>
    MissingCatalogRoot,
    /// <summary>The registered catalog root is stale, malformed, or incorrectly typed.</summary>
    InvalidCatalogRoot,
    /// <summary>A bounded inspection limit prevented further traversal.</summary>
    InspectionLimitReached,
    /// <summary>Encrypted content could not be authenticated with the supplied credentials.</summary>
    AuthenticationFailed
}

/// <summary>A stable, machine-readable structural finding with a human-readable explanation.</summary>
public sealed record PdfDiagnostic(
    PdfDiagnosticCode Code,
    PdfDiagnosticSeverity Severity,
    string Message,
    int? Offset = null,
    int? ObjectNumber = null);

/// <summary>The non-throwing structural inspection result used to decide whether repair is needed.</summary>
public sealed partial class PdfInspectionReport
{
    private readonly PdfDiagnostic[] _diagnostics;

    internal PdfInspectionReport(
        PdfVersion? version,
        long? startXrefOffset,
        int? crossReferenceEntryCount,
        int inspectedObjectCount,
        IEnumerable<PdfDiagnostic> diagnostics)
    {
        Version = version;
        StartXrefOffset = startXrefOffset;
        CrossReferenceEntryCount = crossReferenceEntryCount;
        InspectedObjectCount = inspectedObjectCount;
        _diagnostics = [.. diagnostics];
    }

    /// <summary>Gets the parsed header version when available.</summary>
    public PdfVersion? Version { get; }
    /// <summary>Gets the final cross-reference offset when available.</summary>
    public long? StartXrefOffset { get; }
    /// <summary>Gets the number of merged cross-reference entries when available.</summary>
    public int? CrossReferenceEntryCount { get; }
    /// <summary>Gets the number of indirect objects inspected.</summary>
    public int InspectedObjectCount { get; }
    /// <summary>Gets the ordered structural findings.</summary>
    public IReadOnlyList<PdfDiagnostic> Diagnostics => _diagnostics;
    /// <summary>Gets whether encrypted content requires valid credentials.</summary>
    public bool RequiresAuthentication =>
        _diagnostics.Any(diagnostic => diagnostic.Code == PdfDiagnosticCode.AuthenticationFailed);
    /// <summary>Gets whether inspection found no structural errors other than missing authentication.</summary>
    public bool IsStructurallyValid =>
        !_diagnostics.Any(diagnostic => diagnostic.Severity == PdfDiagnosticSeverity.Error
            && diagnostic.Code != PdfDiagnosticCode.AuthenticationFailed);
    /// <summary>Gets whether structural repair is required before normal processing.</summary>
    public bool RequiresRepair => !IsStructurallyValid;

    /// <summary>Serializes the complete report using stable camel-case property and enum names.</summary>
    public string ToJson(bool indented = false)
    {
        var report = new PdfInspectionReportJson(Version?.ToString(), StartXrefOffset,
            CrossReferenceEntryCount, InspectedObjectCount, RequiresAuthentication,
            IsStructurallyValid, RequiresRepair, Diagnostics);
        return JsonSerializer.Serialize(report, indented
            ? PdfInspectionIndentedJsonContext.Default.PdfInspectionReportJson
            : PdfInspectionCompactJsonContext.Default.PdfInspectionReportJson);
    }

    private sealed record PdfInspectionReportJson(
        string? Version,
        long? StartXrefOffset,
        int? CrossReferenceEntryCount,
        int InspectedObjectCount,
        bool RequiresAuthentication,
        bool IsStructurallyValid,
        bool RequiresRepair,
        IReadOnlyList<PdfDiagnostic> Diagnostics);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UseStringEnumConverter = true)]
    [JsonSerializable(typeof(PdfInspectionReportJson))]
    private sealed partial class PdfInspectionCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        UseStringEnumConverter = true, WriteIndented = true)]
    [JsonSerializable(typeof(PdfInspectionReportJson))]
    private sealed partial class PdfInspectionIndentedJsonContext : JsonSerializerContext;
}
