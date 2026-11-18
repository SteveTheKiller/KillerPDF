using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>The overall PDF/UA-1 result for one document.</summary>
public enum PdfUaVerdict
{
    /// <summary>At least one machine-checkable failure condition failed.</summary>
    Failed,
    /// <summary>Nothing failed, but some machine-checkable conditions are not implemented yet.</summary>
    Incomplete,
    /// <summary>Every machine-checkable condition passed; the human-review conditions remain.</summary>
    PassedMachineChecks
}

/// <summary>The result of one Matterhorn Protocol checkpoint.</summary>
public enum PdfUaCheckpointStatus
{
    /// <summary>A machine-checkable failure condition in this checkpoint failed.</summary>
    Failed,
    /// <summary>Nothing failed, but some of its machine-checkable conditions were not checked.</summary>
    Incomplete,
    /// <summary>Its machine conditions passed or it has none; a person must judge the rest.</summary>
    NeedsHumanReview,
    /// <summary>It has only machine-checkable conditions, and every one passed.</summary>
    Passed
}

/// <summary>One failed Matterhorn failure condition at a document location.</summary>
/// <param name="Condition">The Matterhorn failure condition ID, such as 06-003.</param>
/// <param name="Clause">The ISO 14289-1 requirement, such as UA1:7.1-8.</param>
/// <param name="Message">A readable description of what was found.</param>
public sealed record PdfUaFailure(string Condition, string Clause, string Message)
{
    /// <summary>Gets the zero-based page index, when the failure belongs to a page.</summary>
    public int? PageIndex { get; init; }
    /// <summary>Gets the indirect object number, when applicable.</summary>
    public int? ObjectNumber { get; init; }
    /// <summary>Gets the indirect object generation, when applicable.</summary>
    public int? ObjectGeneration { get; init; }
}

/// <summary>The result of one Matterhorn Protocol checkpoint.</summary>
/// <param name="Number">The two-digit checkpoint number, 01 to 31.</param>
/// <param name="Name">The checkpoint name.</param>
/// <param name="Status">The checkpoint result.</param>
/// <param name="Failures">Failed conditions, in check order.</param>
/// <param name="HumanReviewConditions">Conditions the protocol leaves to a person.</param>
/// <param name="UncheckedConditions">Machine-checkable conditions this validator does not implement.</param>
public sealed record PdfUaCheckpointResult(
    string Number, string Name, PdfUaCheckpointStatus Status,
    IReadOnlyList<PdfUaFailure> Failures,
    IReadOnlyList<string> HumanReviewConditions,
    IReadOnlyList<string> UncheckedConditions);

/// <summary>A PDF/UA-1 validation report with one result per Matterhorn Protocol checkpoint.</summary>
public sealed partial class PdfUaReport
{
    private const int SchemaVersion = 1;
    private static readonly PdfUaCompactJsonContext CompactJson = new(JsonOptions(false));
    private static readonly PdfUaIndentedJsonContext IndentedJson = new(JsonOptions(true));

    internal PdfUaReport(int? declaredPart, IReadOnlyList<PdfUaCheckpointResult> checkpoints)
    {
        DeclaredPart = declaredPart;
        Checkpoints = checkpoints;
        Failures = Array.AsReadOnly(checkpoints.SelectMany(checkpoint => checkpoint.Failures).ToArray());
        Verdict = checkpoints.Any(checkpoint => checkpoint.Status == PdfUaCheckpointStatus.Failed)
            ? PdfUaVerdict.Failed
            : checkpoints.Any(checkpoint => checkpoint.Status == PdfUaCheckpointStatus.Incomplete)
                ? PdfUaVerdict.Incomplete
                : PdfUaVerdict.PassedMachineChecks;
    }

    /// <summary>Gets the PDF/UA part the document's XMP metadata declares, or null.</summary>
    public int? DeclaredPart { get; }
    /// <summary>Gets the summary verdict. It never claims full conformance, since people must judge some conditions.</summary>
    public PdfUaVerdict Verdict { get; }
    /// <summary>Gets all 31 checkpoints in protocol order.</summary>
    public IReadOnlyList<PdfUaCheckpointResult> Checkpoints { get; }
    /// <summary>Gets every failure across the checkpoints.</summary>
    public IReadOnlyList<PdfUaFailure> Failures { get; }

    /// <summary>Formats a readable report with one line per checkpoint and its failures beneath.</summary>
    public string ToText()
    {
        var output = new StringBuilder().Append("PDF/UA-1 result: ").AppendLine(Verdict switch
        {
            PdfUaVerdict.Failed => "Failed",
            PdfUaVerdict.Incomplete => "Incomplete (no failures, some machine checks not implemented)",
            _ => "Passed machine checks (human review still required)"
        });
        output.Append("Declared conformance: ")
            .AppendLine(DeclaredPart is int part ? $"PDF/UA-{part}" : "none");
        foreach (PdfUaCheckpointResult checkpoint in Checkpoints)
        {
            output.Append(checkpoint.Number).Append(' ').Append(checkpoint.Name).Append(": ")
                .AppendLine(checkpoint.Status switch
                {
                    PdfUaCheckpointStatus.NeedsHumanReview => "Needs human review",
                    _ => checkpoint.Status.ToString()
                });
            foreach (PdfUaFailure failure in checkpoint.Failures)
            {
                output.Append("  ").Append(failure.Condition).Append(" (").Append(failure.Clause).Append(')');
                if (failure.PageIndex is int pageIndex) output.Append(" | Page ").Append(pageIndex + 1);
                if (failure.ObjectNumber is int objectNumber) output.Append(" | Object ").Append(objectNumber);
                output.Append(": ").AppendLine(failure.Message);
            }
            if (checkpoint.HumanReviewConditions.Count > 0)
                output.Append("  Human review: ").AppendLine(string.Join(", ", checkpoint.HumanReviewConditions));
            if (checkpoint.UncheckedConditions.Count > 0)
                output.Append("  Not checked: ").AppendLine(string.Join(", ", checkpoint.UncheckedConditions));
        }
        return output.ToString();
    }

    /// <summary>Serializes the report with stable camel-case names and string enums.</summary>
    public string ToJson(bool indented = false)
    {
        var report = new PdfUaReportJson(SchemaVersion, "PDF/UA-1", "Matterhorn Protocol 1.1",
            DeclaredPart, Verdict, Checkpoints);
        return JsonSerializer.Serialize(report, indented
            ? IndentedJson.PdfUaReportJson
            : CompactJson.PdfUaReportJson);
    }

    private static JsonSerializerOptions JsonOptions(bool indented)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter<PdfUaVerdict>(JsonNamingPolicy.CamelCase));
        options.Converters.Add(new JsonStringEnumConverter<PdfUaCheckpointStatus>(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record PdfUaReportJson(
        int SchemaVersion, string Standard, string Protocol, int? DeclaredPart,
        PdfUaVerdict Verdict, IReadOnlyList<PdfUaCheckpointResult> Checkpoints);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(PdfUaReportJson))]
    private sealed partial class PdfUaCompactJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
    [JsonSerializable(typeof(PdfUaReportJson))]
    private sealed partial class PdfUaIndentedJsonContext : JsonSerializerContext;
}

// The Matterhorn Protocol 1.1 checkpoints and failure conditions: ID, whether a machine or a person
// decides it, and the ISO 14289-1 requirement. "M*" marks machine conditions this validator checks.
internal static class PdfUaMatterhorn
{
    private sealed record Condition(string Id, bool Machine, bool Implemented, string Clause);
    private sealed record Checkpoint(string Number, string Name, Condition[] Conditions);

    private static readonly Checkpoint[] Checkpoints = Build(
        ("01", "Real content tagged", "001 H 7.1-1; 002 H 7.1-1; 003 M* 7.1-1; 004 M* 7.1-1; 005 M* 7.1-2; 006 H 7.1-2; 007 M* 7.1-11"),
        ("02", "Role mapping", "001 M* 7.1-3; 002 H 7.1-3; 003 M* 7.1-3; 004 M* 7.1-4"),
        ("03", "Flickering", "001 H 7.1-5; 002 H 7.1-5; 003 H 7.1-5"),
        ("04", "Color and contrast", "001 H 7.1-6"),
        ("05", "Sound", "001 H 7.1-7; 002 H 7.1-7; 003 H 7.1-7"),
        ("06", "Metadata", "001 M* 7.1-8; 002 M* 5; 003 M* 7.1-8; 004 H 7.1-8"),
        ("07", "Dictionary", "001 M* 7.1-9; 002 M* 7.1-9"),
        ("08", "OCR validation", "001 H 7.1-10; 002 H 7.1-10"),
        ("09", "Appropriate tags", "001 H 7.2-1; 002 H 7.2-1; 003 H 7.2-1; 004 M 7.2-1; 005 M 7.2-1; 006 M 7.2-1; 007 M 7.2-1; 008 M 7.2-1"),
        ("10", "Character mappings", "001 M 7.2-2"),
        ("11", "Declared natural language", "001 M* 7.2-3; 002 M* 7.2-3; 003 M* 7.2-3; 004 M* 7.2-3; 005 M* 7.2-3; 006 M* 7.2-3; 007 H 7.2-3"),
        ("12", "Stretchable characters", "001 H 7.2-4"),
        ("13", "Graphics", "001 H 7.3-1; 002 H 7.3-1; 003 H 7.3-2; 004 M* 7.3-3; 005 H 7.3-4; 006 H 7.3-5; 007 H 7.3-6; 008 H 7.3-4"),
        ("14", "Headings", "001 H 7.4-1; 002 M* 7.4.2-1; 003 M* 7.4-1; 004 H 7.4.3-1; 005 H 7.4.3-1; 006 M* 7.4.4-1; 007 M* 7.4.4-3"),
        ("15", "Tables", "001 H 7.5-1; 002 H 7.5-1; 003 M* 7.5-2; 004 H 7.5-3; 005 H 7.5-2"),
        ("16", "Lists", "001 H 7.6-1; 002 H 7.6-1; 003 H 7.6-2"),
        ("17", "Mathematical expressions", "001 H 7.7-1; 002 M* 7.7-1; 003 M 7.7-2"),
        ("18", "Page headers and footers", "001 H 7.8-1; 002 H 7.8-1"),
        ("19", "Notes and references", "001 H 7.9-1; 002 H 7.9-1; 003 M* 7.9-2; 004 M* 7.9-2"),
        ("20", "Optional content", "001 M* 7.10-1; 002 M* 7.10-1; 003 M* 7.10-2"),
        ("21", "Embedded files", "001 M* 7.11-1"),
        ("22", "Article threads", "001 H 7.12-1"),
        ("23", "Digital signatures", "001 H 7.13-1"),
        ("24", "Non-interactive forms", "001 H 7.14-1"),
        ("25", "XFA", "001 M* 7.15-1"),
        ("26", "Security", "001 M* 7.16-1; 002 M* 7.16-1"),
        ("27", "Navigation", "001 H 7.17-1"),
        ("28", "Annotations", "001 H 7.18.1-2; 002 M* 7.18.1-2; 003 H 7.18.1-3; 004 M* 7.18.1-4; 005 M* 7.18.1-4; "
            + "006 M 7.18.2-1; 007 M* 7.18.2-2; 008 M* 7.18.3-1; 009 M* 7.18.3-1; 010 M* 7.18.4-1; 011 M* 7.18.5-1; "
            + "012 M* 7.18.5-2; 013 H 7.18.5-3; 014 M 7.18.6.2-1; 015 M 7.18.6.2-1; 016 M 7.18.7-1; "
            + "017 M 7.18.8-1; 018 M 7.18.8-2"),
        ("29", "Actions", "001 H 7.19-1"),
        ("30", "XObjects", "001 M* 7.20-1; 002 M* 7.20-2"),
        ("31", "Fonts", "001 M 7.21.3-1; 002 M 7.21.3.1-1; 003 M 7.21.3.1-1; 004 M 7.21.3.2-1; 005 M 7.21.3.2-1; "
            + "006 M 7.21.3.3-1; 007 M 7.21.3.3-1; 008 M 7.21.3.3-2; 009 M* 7.21.4.1-1; 010 H 7.21.4.1-2; "
            + "011 M 7.21.4.1-3; 012 M 7.21.4.2-1; 013 M 7.21.4.2-2; 014 M 7.21.4.2-3; 015 M 7.21.4.2-4; "
            + "016 M 7.21.5-1; 017 M 7.21.6-1; 018 M 7.21.6-2; 019 M 7.21.6-3; 020 M 7.21.6-4; 021 M 7.21.6-5; "
            + "022 M 7.21.6-6; 023 M 7.21.6-7; 024 M 7.21.6-8; 025 M 7.21.6-9; 026 M 7.21.6-10; "
            + "027 M 7.21.7-1; 028 M 7.21.7-2; 029 M 7.21.7-3; 030 M 7.21.8-1"));

    private static readonly Dictionary<string, string> Clauses = Checkpoints
        .SelectMany(checkpoint => checkpoint.Conditions)
        .ToDictionary(condition => condition.Id, condition => condition.Clause);

    internal static string Clause(string condition) =>
        Clauses.TryGetValue(condition, out string? clause)
            ? clause
            : throw new ArgumentException($"Unknown Matterhorn failure condition {condition}.", nameof(condition));

    internal static IReadOnlyList<PdfUaCheckpointResult> Evaluate(IReadOnlyList<PdfUaFailure> failures)
    {
        var results = new List<PdfUaCheckpointResult>(Checkpoints.Length);
        foreach (Checkpoint checkpoint in Checkpoints)
        {
            PdfUaFailure[] failed = [.. failures.Where(failure =>
                failure.Condition.StartsWith(checkpoint.Number + "-", StringComparison.Ordinal))];
            string[] human = [.. checkpoint.Conditions.Where(item => !item.Machine).Select(item => item.Id)];
            string[] unchecked_ = [.. checkpoint.Conditions.Where(item => item.Machine && !item.Implemented)
                .Select(item => item.Id)];
            PdfUaCheckpointStatus status = failed.Length > 0 ? PdfUaCheckpointStatus.Failed
                : unchecked_.Length > 0 ? PdfUaCheckpointStatus.Incomplete
                : human.Length > 0 ? PdfUaCheckpointStatus.NeedsHumanReview
                : PdfUaCheckpointStatus.Passed;
            results.Add(new PdfUaCheckpointResult(checkpoint.Number, checkpoint.Name, status,
                Array.AsReadOnly(failed), Array.AsReadOnly(human), Array.AsReadOnly(unchecked_)));
        }
        return results.AsReadOnly();
    }

    private static Checkpoint[] Build(params (string Number, string Name, string Conditions)[] rows) =>
        [.. rows.Select(row => new Checkpoint(row.Number, row.Name, [.. row.Conditions
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(' '))
            .Select(parts => new Condition(row.Number + "-" + parts[0], parts[1] != "H",
                parts[1] == "M*", "UA1:" + parts[2]))]))];
}
