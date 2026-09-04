using System.Globalization;

namespace KillerPdf.Engine.Documents;

/// <summary>Creates and executes typed navigation macro steps.</summary>
public static class PdfNavigationMacro
{
    /// <summary>Creates a navigation-audit macro step.</summary>
    public static PdfMacroStep AuditStep() => new(PdfMacroOperation.AuditNavigation);

    /// <summary>Creates a macro step that removes unsafe URI links.</summary>
    public static PdfMacroStep RemoveUnsafeLinksStep() =>
        new(PdfMacroOperation.RemoveUnsafeLinks);

    /// <summary>Creates an explicitly configured heading-bookmark generation step.</summary>
    public static PdfMacroStep HeadingBookmarksStep(
        PdfBookmarkDetectionOptions? options = null)
    {
        options ??= new PdfBookmarkDetectionOptions();
        return new PdfMacroStep(PdfMacroOperation.GenerateBookmarks,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["minimumPointSize"] = options.MinimumPointSize.ToString("R", CultureInfo.InvariantCulture),
                ["maximumTitleLength"] = options.MaximumTitleLength.ToString(CultureInfo.InvariantCulture),
                ["maximumDepth"] = options.MaximumDepth.ToString(CultureInfo.InvariantCulture),
                ["acceptDetectedHeadings"] = "true"
            });
    }

    /// <summary>Creates a clickable table-of-contents generation step.</summary>
    public static PdfMacroStep TableOfContentsStep(int maximumDepth = 6) =>
        new(PdfMacroOperation.GenerateTableOfContents,
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["maximumDepth"] = maximumDepth.ToString(CultureInfo.InvariantCulture)
            });

    /// <summary>Executes one typed navigation macro step without external actions.</summary>
    public static ReadOnlyMemory<byte> Execute(PdfMacroStep step,
        ReadOnlyMemory<byte> source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (source.IsEmpty) throw new ArgumentException("The PDF source is empty.", nameof(source));
        PdfDocument document = PdfDocument.Open(source);
        return step.Operation switch
        {
            PdfMacroOperation.AuditNavigation => Audit(document, source),
            PdfMacroOperation.RemoveUnsafeLinks =>
                PdfNavigationAudit.RemoveUnsafeLinks(document),
            PdfMacroOperation.GenerateBookmarks => GenerateBookmarks(
                document, HeadingOptions(step), cancellationToken),
            PdfMacroOperation.GenerateTableOfContents => PdfTableOfContentsWriter.Write(
                document, new PdfTableOfContentsWriteOptions
                {
                    MaximumDepth = PositiveInteger(step, "maximumDepth", 256)
                }).Document,
            _ => throw new ArgumentException(
                "The macro step is not a navigation operation.", nameof(step))
        };
    }

    /// <summary>Returns findings for an audit macro step without changing the document.</summary>
    public static IReadOnlyList<PdfNavigationFinding> Inspect(
        PdfMacroStep step, PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(document);
        if (step.Operation != PdfMacroOperation.AuditNavigation)
            throw new ArgumentException("The macro step is not a navigation audit.", nameof(step));
        return PdfNavigationAudit.Inspect(document);
    }

    private static ReadOnlyMemory<byte> Audit(
        PdfDocument document, ReadOnlyMemory<byte> source)
    {
        _ = PdfNavigationAudit.Inspect(document);
        return source.ToArray();
    }

    private static byte[] GenerateBookmarks(PdfDocument document,
        PdfBookmarkDetectionOptions options, CancellationToken cancellationToken)
    {
        PdfBookmarkProposal[] proposals = [.. PdfBookmarkGeneration
            .DetectHeadings(document, options, cancellationToken)
            .Select(item => item with
            {
                Decision = PdfBookmarkProposalDecision.Accepted
            })];
        return proposals.Length == 0 ? document.Source.ToArray()
            : PdfBookmarkGeneration.Apply(document, proposals);
    }

    private static PdfBookmarkDetectionOptions HeadingOptions(PdfMacroStep step)
    {
        if (step.Settings is null
            || !step.Settings.TryGetValue("acceptDetectedHeadings", out string? accepted)
            || accepted != "true")
            throw new ArgumentException(
                "Heading generation requires explicit acceptance in the macro step.", nameof(step));
        return new PdfBookmarkDetectionOptions
        {
            MinimumPointSize = PositiveDouble(step, "minimumPointSize"),
            MaximumTitleLength = PositiveInteger(step, "maximumTitleLength", int.MaxValue),
            MaximumDepth = PositiveInteger(step, "maximumDepth", 256)
        };
    }

    private static int PositiveInteger(PdfMacroStep step, string key, int maximum)
    {
        if (step.Settings is null || !step.Settings.TryGetValue(key, out string? value)
            || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture,
                out int parsed) || parsed < 1 || parsed > maximum)
            throw new ArgumentException(
                $"Navigation macro setting '{key}' is invalid.", nameof(step));
        return parsed;
    }

    private static double PositiveDouble(PdfMacroStep step, string key)
    {
        if (step.Settings is null || !step.Settings.TryGetValue(key, out string? value)
            || !double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double parsed) || !double.IsFinite(parsed) || parsed <= 0)
            throw new ArgumentException(
                $"Navigation macro setting '{key}' is invalid.", nameof(step));
        return parsed;
    }
}
