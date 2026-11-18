namespace KillerPdf.Engine.Diagnostics;

// The PDF/UA-2 (ISO 14289-2:2024) machine rules, named by the rule IDs of the veraPDF PDF/UA-2
// validation profile (clause and test number, such as 8.2.5.12-1), grouped by the clause they
// belong to. "M*" marks rules this validator checks; "M" marks rules it does not implement, which
// the report lists as not checked. The profile publishes machine rules only, so the requirements a
// person must judge are named by one review entry instead of per-rule IDs.
internal static class PdfUa2Rules
{
    internal const string Standard = "PDF/UA-2";
    internal const string Protocol = "veraPDF PDF/UA-2 profile rule IDs (ISO 14289-2:2024 clauses)";

    /// <summary>The containment rules of ISO/TS 32005 Table 5, which this validator does not check.</summary>
    internal const string Iso32005Containment = "ISO 32005 Table 5 containment";

    /// <summary>The part of 8.4.4-2 not checked: Lang in marked-content property lists.</summary>
    internal const string LanguageInPropertyLists = "8.4.4-2 (marked-content property lists)";

    /// <summary>The review entry for requirements that need a person.</summary>
    internal const string HumanReview = "Requirements a person must judge, such as alternate text quality, reading order and correct tag semantics";

    private sealed record Rule(string Id, bool Implemented, string Clause);
    private sealed record Section(string Number, string Name, Rule[] Rules, string[] Unlisted, string[] Human);

    private static readonly Section[] Sections =
    [
        Build("5", "PDF/UA identification", "5-1 M*; 5-2 M*; 5-3 M*; 5-4 M*; 5-5 M*"),
        Build("6", "File format", "6.1-1 M*; 6.2-1 M*"),
        Build("8.2", "Logical structure",
            "8.2.1-1 M*; 8.2.1-2 M*; 8.2.2-1 M*; 8.2.4-1 M*; 8.2.4-2 M*; 8.2.4-3 M*; 8.2.4-4 M*; "
            + "8.2.5.2-1 M*; 8.2.5.2-2 M*; 8.2.5.8-1 M*; 8.2.5.12-1 M*; 8.2.5.14-1 M*; 8.2.5.14-2 M; "
            + "8.2.5.14-3 M; 8.2.5.14-4 M*; 8.2.5.20-1 M*; 8.2.5.20-2 M; 8.2.5.23-1 M*; 8.2.5.24-1 M*; "
            + "8.2.5.25-1 M*; 8.2.5.25-2 M*; 8.2.5.26-1 M; 8.2.5.26-2 M; 8.2.5.26-3 M; 8.2.5.26-4 M; "
            + "8.2.5.26-5 M; 8.2.5.26-6 M; 8.2.5.27-1 M*; 8.2.5.28.2-1 M*; 8.2.5.29-1 M*",
            unlisted: [Iso32005Containment]),
        Build("8.4", "Text and fonts",
            "8.4.3-1 M; 8.4.3-2 M; 8.4.3-3 M; 8.4.4-1 M*; 8.4.4-2 M*; 8.4.5.3.1-1 M; 8.4.5.3.2-1 M; "
            + "8.4.5.4-1 M; 8.4.5.4-2 M; 8.4.5.4-3 M; 8.4.5.5.1-1 M*; 8.4.5.5.1-2 M; 8.4.5.6-1 M; "
            + "8.4.5.7-1 M; 8.4.5.7-2 M; 8.4.5.7-3 M; 8.4.5.7-4 M; 8.4.5.8-1 M; 8.4.5.8-2 M; 8.4.5.9-1 M",
            unlisted: [LanguageInPropertyLists]),
        Build("8.6", "Text strings", "8.6-1 M"),
        Build("8.7", "Optional content", "8.7-1 M*; 8.7-2 M*"),
        Build("8.8", "Destinations", "8.8-1 M; 8.8-2 M"),
        Build("8.9", "Annotations",
            "8.9.2.2-1 M*; 8.9.2.2-2 M*; 8.9.2.3-1 M*; 8.9.2.3-2 M; 8.9.2.4.7-1 M*; 8.9.2.4.8-1 M*; "
            + "8.9.2.4.9-1 M*; 8.9.2.4.10-1 M*; 8.9.2.4.11-1 M*; 8.9.2.4.11-2 M*; 8.9.2.4.12-1 M*; "
            + "8.9.2.4.13-1 M*; 8.9.2.4.14-1 M*; 8.9.2.4.15-1 M*; 8.9.2.4.16-1 M*; 8.9.2.4.19-1 M*; "
            + "8.9.2.4.19-2 M*; 8.9.3.3-1 M*; 8.9.4.2-1 M*"),
        Build("8.10", "Forms",
            "8.10.1-1 M*; 8.10.1-2 M*; 8.10.1-3 M*; 8.10.2.3-1 M; 8.10.2.3-2 M*; 8.10.3.3-1 M; 8.10.3.5-1 M"),
        Build("8.11", "Metadata", "8.11.1-1 M*; 8.11.1-2 M*; 8.11.2-1 M*"),
        Build("8.14", "Embedded files", "8.14.1-1 M*"),
        new Section("R", "Human review", [], [], [HumanReview])
    ];

    private static readonly Dictionary<string, string> Clauses = Sections
        .SelectMany(section => section.Rules)
        .ToDictionary(rule => rule.Id, rule => rule.Clause);

    internal static string Clause(string rule) =>
        Clauses.TryGetValue(rule, out string? clause)
            ? clause
            : throw new ArgumentException($"Unknown PDF/UA-2 rule {rule}.", nameof(rule));

    internal static IReadOnlyList<PdfUaCheckpointResult> Evaluate(IReadOnlyList<PdfUaFailure> failures)
    {
        var results = new List<PdfUaCheckpointResult>(Sections.Length);
        foreach (Section section in Sections)
        {
            PdfUaFailure[] failed = [.. failures.Where(failure =>
                section.Rules.Any(rule => rule.Id == failure.Condition))];
            string[] notChecked = [.. section.Rules.Where(rule => !rule.Implemented).Select(rule => rule.Id),
                .. section.Unlisted];
            PdfUaCheckpointStatus status = failed.Length > 0 ? PdfUaCheckpointStatus.Failed
                : notChecked.Length > 0 ? PdfUaCheckpointStatus.Incomplete
                : section.Human.Length > 0 ? PdfUaCheckpointStatus.NeedsHumanReview
                : PdfUaCheckpointStatus.Passed;
            results.Add(new PdfUaCheckpointResult(section.Number, section.Name, status,
                Array.AsReadOnly(failed), Array.AsReadOnly(section.Human), Array.AsReadOnly(notChecked)));
        }
        return results.AsReadOnly();
    }

    private static Section Build(string number, string name, string rules, string[]? unlisted = null) =>
        new(number, name, [.. rules
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(entry => entry.Split(' '))
            .Select(parts => new Rule(parts[0], parts[1] == "M*", "UA2:" + parts[0][..parts[0].LastIndexOf('-')]))],
            unlisted ?? [], []);
}
