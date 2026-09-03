using System.Text.Json;
using System.Text.Json.Serialization;
using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>A repeatable check available to a preflight profile.</summary>
public enum PdfPreflightCheck
{
    /// <summary>Checks the PDF header, cross references, objects, and catalog.</summary>
    StructuralIntegrity,
    /// <summary>Checks for a declared primary document language.</summary>
    DocumentLanguage,
    /// <summary>Checks for a structure tree and marked-content declaration.</summary>
    TaggedStructure,
    /// <summary>Checks effective media and crop boxes on every page.</summary>
    PageBoxes,
    /// <summary>Checks for a usable document output intent and ICC profile.</summary>
    OutputIntent,
    /// <summary>Checks the effective resolution of every placed image.</summary>
    ImageResolution,
    /// <summary>Checks that page and form fonts include an embedded font program.</summary>
    FontEmbedding,
    /// <summary>Checks for transparency, soft masks, and non-normal blend modes.</summary>
    Transparency
}

/// <summary>A named, shareable selection of preflight checks.</summary>
public sealed record PdfPreflightProfile
{
    /// <summary>Creates a validated profile.</summary>
    public PdfPreflightProfile(string name, IEnumerable<PdfPreflightCheck> checks,
        double minimumImageDpi = 300)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A preflight profile name is required.", nameof(name));
        ArgumentNullException.ThrowIfNull(checks);
        PdfPreflightCheck[] selected = checks.Distinct().ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("A preflight profile requires at least one check.", nameof(checks));
        if (selected.Any(check => !Enum.IsDefined(check)))
            throw new ArgumentOutOfRangeException(nameof(checks), "A preflight check is not defined.");
        if (!double.IsFinite(minimumImageDpi) || minimumImageDpi <= 0)
            throw new ArgumentOutOfRangeException(nameof(minimumImageDpi));
        Name = name;
        Checks = Array.AsReadOnly(selected);
        MinimumImageDpi = minimumImageDpi;
    }

    /// <summary>Gets a structural validation profile suitable for ordinary PDFs.</summary>
    public static PdfPreflightProfile General { get; } =
        new("General PDF", [PdfPreflightCheck.StructuralIntegrity]);

    /// <summary>Gets the current print-production profile.</summary>
    public static PdfPreflightProfile PrintProduction { get; } = new("Print production", [
        PdfPreflightCheck.StructuralIntegrity,
        PdfPreflightCheck.PageBoxes,
        PdfPreflightCheck.OutputIntent,
        PdfPreflightCheck.ImageResolution,
        PdfPreflightCheck.FontEmbedding,
        PdfPreflightCheck.Transparency
    ]);

    /// <summary>Gets the profile name.</summary>
    public string Name { get; }
    /// <summary>Gets the selected checks in profile order.</summary>
    public IReadOnlyList<PdfPreflightCheck> Checks { get; }
    /// <summary>Gets the minimum effective image resolution in dots per inch.</summary>
    public double MinimumImageDpi { get; }

    /// <summary>Serializes the profile with stable camel-case names.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new PdfPreflightProfileFile(1, Name, Checks.ToArray(), MinimumImageDpi), JsonOptions(indented));

    /// <summary>Reads and validates a serialized profile.</summary>
    public static PdfPreflightProfile FromJson(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        PdfPreflightProfileFile file = JsonSerializer.Deserialize<PdfPreflightProfileFile>(
            json, JsonOptions(false)) ?? throw new JsonException("The preflight profile is empty.");
        if (file.Version != 1)
            throw new NotSupportedException(
                $"Preflight profile version {file.Version} is not supported.");
        return new PdfPreflightProfile(file.Name, file.Checks, file.MinimumImageDpi ?? 300);
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PdfPreflightProfileFile(
        int Version, string Name, PdfPreflightCheck[] Checks, double? MinimumImageDpi);
}

/// <summary>One finding produced by a preflight profile.</summary>
public sealed record PdfPreflightFinding(
    string Code, PdfDiagnosticSeverity Severity, string Message,
    int? PageIndex = null, int? ObjectNumber = null);

/// <summary>The repeatable result of running one preflight profile.</summary>
public sealed class PdfPreflightReport
{
    internal PdfPreflightReport(string profileName, IEnumerable<PdfPreflightFinding> findings)
    {
        ProfileName = profileName;
        Findings = Array.AsReadOnly(findings.ToArray());
    }

    /// <summary>Gets the profile that produced the report.</summary>
    public string ProfileName { get; }
    /// <summary>Gets findings in deterministic check order.</summary>
    public IReadOnlyList<PdfPreflightFinding> Findings { get; }
    /// <summary>Gets whether every selected implemented check passed.</summary>
    public bool Passed => !Findings.Any(finding => finding.Severity == PdfDiagnosticSeverity.Error);

    /// <summary>Serializes the report with stable camel-case names.</summary>
    public string ToJson(bool indented = false)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return JsonSerializer.Serialize(new { ProfileName, Passed, Findings }, options);
    }
}

/// <summary>Runs selected structural and document-level accessibility checks.</summary>
public static class PdfPreflightRunner
{
    /// <summary>Runs a profile without changing the source document.</summary>
    public static PdfPreflightReport Run(ReadOnlyMemory<byte> source,
        PdfPreflightProfile profile, string? password = null)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PdfInspectionReport inspection = password is null
            ? PdfDocumentInspector.Inspect(source)
            : PdfDocumentInspector.InspectAuthenticated(source, password);
        var findings = new List<PdfPreflightFinding>();
        if (profile.Checks.Contains(PdfPreflightCheck.StructuralIntegrity))
            findings.AddRange(inspection.Diagnostics.Select(diagnostic => new PdfPreflightFinding(
                $"Structural.{diagnostic.Code}", diagnostic.Severity, diagnostic.Message,
                ObjectNumber: diagnostic.ObjectNumber)));
        if (!inspection.IsStructurallyValid || inspection.RequiresAuthentication)
        {
            if (profile.Checks.Any(check => check != PdfPreflightCheck.StructuralIntegrity))
                findings.Add(new PdfPreflightFinding("DocumentChecksUnavailable",
                    PdfDiagnosticSeverity.Error,
                    "Document checks require a structurally valid, authenticated PDF."));
            return new PdfPreflightReport(profile.Name, findings);
        }

        PdfDocument checkedDocument = password is null
            ? PdfDocument.Open(source) : PdfDocument.Open(source, password);
        if (profile.Checks.Contains(PdfPreflightCheck.DocumentLanguage)
            || profile.Checks.Contains(PdfPreflightCheck.TaggedStructure))
        {
            foreach (PdfAccessibilityFinding finding in
                PdfAccessibilityInspector.Inspect(checkedDocument).Findings)
            {
                bool selected = finding.Code == PdfAccessibilityFindingCode.MissingDocumentLanguage
                    ? profile.Checks.Contains(PdfPreflightCheck.DocumentLanguage)
                    : profile.Checks.Contains(PdfPreflightCheck.TaggedStructure);
                if (selected)
                    findings.Add(new PdfPreflightFinding(
                        $"Accessibility.{finding.Code}", finding.Severity, finding.Message));
            }
        }
        if (profile.Checks.Contains(PdfPreflightCheck.PageBoxes))
            findings.AddRange(PdfPreflightDocumentChecks.CheckPageBoxes(checkedDocument));
        if (profile.Checks.Contains(PdfPreflightCheck.OutputIntent))
            findings.AddRange(PdfPreflightDocumentChecks.CheckOutputIntent(checkedDocument));
        if (profile.Checks.Contains(PdfPreflightCheck.ImageResolution))
            findings.AddRange(PdfPreflightDocumentChecks.CheckImageResolution(
                checkedDocument, profile.MinimumImageDpi));
        if (profile.Checks.Contains(PdfPreflightCheck.FontEmbedding))
            findings.AddRange(PdfPreflightDocumentChecks.CheckFontEmbedding(checkedDocument));
        if (profile.Checks.Contains(PdfPreflightCheck.Transparency))
            findings.AddRange(PdfPreflightDocumentChecks.CheckTransparency(checkedDocument));
        return new PdfPreflightReport(profile.Name, findings);
    }
}
