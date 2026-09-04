using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Editing;
using System.Text.RegularExpressions;

namespace KillerPdf.Engine.Documents;

/// <summary>Detects heading-like page text and authors reviewed bookmark proposals.</summary>
public static class PdfBookmarkGeneration
{
    /// <summary>Creates reviewable bookmark proposals from prominent horizontal text lines.</summary>
    public static IReadOnlyList<PdfBookmarkProposal> DetectHeadings(
        PdfDocument document, PdfBookmarkDetectionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfBookmarkDetectionOptions();
        Validate(options);
        Regex? titlePattern = options.TitlePattern is null ? null : new Regex(
            options.TitlePattern, RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
        var reader = new PdfPageContentReader(document);
        if (options.PageRegions is not null
            && options.PageRegions.Keys.Any(page => page < 0 || page >= reader.PageCount))
            throw new ArgumentOutOfRangeException(nameof(options.PageRegions),
                "A heading region page is outside the document.");
        var candidates = new List<(int Page, PdfExtractedLine Line, double Size)>();
        for (int pageIndex = 0; pageIndex < reader.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (PdfExtractedLine line in reader.Read(pageIndex, cancellationToken).Lines)
            {
                string title = line.Text.Trim();
                double size = line.Runs.Max(run => run.PointSize);
                if (title.Length is 0 || title.Length > options.MaximumTitleLength
                    || size < options.MinimumPointSize
                    || titlePattern is not null && !titlePattern.IsMatch(title)
                    || options.PageRegions is not null
                        && (!options.PageRegions.TryGetValue(pageIndex, out PdfContentBounds region)
                            || !Intersects(region, line.BoundingBox))
                    || line.WritingDirection is not (PdfWritingDirection.LeftToRight
                        or PdfWritingDirection.RightToLeft))
                    continue;
                candidates.Add((pageIndex, line, size));
            }
        }
        double[] sizes = [.. candidates.Select(item => item.Size).Distinct()
            .OrderByDescending(size => size).Take(options.MaximumDepth)];
        var proposals = new List<PdfBookmarkProposal>(candidates.Count);
        int previousLevel = 0;
        foreach ((int page, PdfExtractedLine line, double size) in candidates)
        {
            int ranked = Array.IndexOf(sizes, size);
            if (ranked < 0) continue;
            int level = proposals.Count == 0 ? 0 : Math.Min(ranked, previousLevel + 1);
            proposals.Add(new PdfBookmarkProposal
            {
                Title = line.Text.Trim(),
                PageIndex = page,
                Bounds = line.BoundingBox,
                PointSize = size,
                Level = level
            });
            previousLevel = level;
        }
        return Array.AsReadOnly(proposals.ToArray());
    }

    private static bool Intersects(PdfContentBounds first, PdfContentBounds second) =>
        first.Left < second.Right && first.Right > second.Left
        && first.Bottom < second.Top && first.Top > second.Bottom;

    /// <summary>Authors accepted proposals after every proposal has been reviewed.</summary>
    public static byte[] Apply(PdfDocument document,
        IEnumerable<PdfBookmarkProposal> proposals)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(proposals);
        PdfBookmarkProposal[] reviewed = proposals.ToArray();
        if (reviewed.Any(item => item.Decision == PdfBookmarkProposalDecision.Pending))
            throw new InvalidOperationException(
                "Every bookmark proposal must be accepted or rejected before authoring.");
        PdfBookmarkProposal[] accepted = [.. reviewed.Where(item =>
            item.Decision == PdfBookmarkProposalDecision.Accepted)];
        if (accepted.Length == 0)
            throw new InvalidOperationException("No bookmark proposals were accepted.");
        var editor = new PdfIncrementalPageEditor(document);
        int previousLevel = 0;
        for (int index = 0; index < accepted.Length; index++)
        {
            PdfBookmarkProposal item = accepted[index];
            if (string.IsNullOrWhiteSpace(item.Title))
                throw new ArgumentException("An accepted bookmark title cannot be empty.",
                    nameof(proposals));
            int level = index == 0 ? 0 : Math.Min(item.Level, previousLevel + 1);
            editor.AddBookmark(item.Title, item.PageIndex, level,
                new PdfBookmarkOptions
                {
                    Destination = PdfDestination.FitWidth(item.Bounds.Top)
                });
            previousLevel = level;
        }
        return editor.Build();
    }

    private static void Validate(PdfBookmarkDetectionOptions options)
    {
        if (!double.IsFinite(options.MinimumPointSize)
            || options.MinimumPointSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(options.MinimumPointSize));
        if (options.MaximumTitleLength < 1)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumTitleLength));
        if (options.MaximumDepth is < 1 or > 256)
            throw new ArgumentOutOfRangeException(nameof(options.MaximumDepth));
        if (options.PageRegions?.Values.Any(region => region.Width <= 0 || region.Height <= 0) == true)
            throw new ArgumentException(
                "Heading regions must have positive dimensions.", nameof(options.PageRegions));
        if (options.TitlePattern is not null)
            try
            {
                _ = new Regex(options.TitlePattern, RegexOptions.CultureInvariant,
                    TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException error)
            {
                throw new ArgumentException(
                    "The heading title pattern is invalid.", nameof(options.TitlePattern), error);
            }
    }
}

/// <summary>Controls heading detection for bookmark proposals.</summary>
public sealed record PdfBookmarkDetectionOptions
{
    /// <summary>Gets the smallest effective text size considered a heading.</summary>
    public double MinimumPointSize { get; init; } = 14;
    /// <summary>Gets the longest heading text accepted.</summary>
    public int MaximumTitleLength { get; init; } = 160;
    /// <summary>Gets the maximum number of font-size hierarchy levels.</summary>
    public int MaximumDepth { get; init; } = 6;
    /// <summary>Gets an optional regular expression that heading titles must match.</summary>
    public string? TitlePattern { get; init; }
    /// <summary>Gets optional page regions that bound heading detection.</summary>
    public IReadOnlyDictionary<int, PdfContentBounds>? PageRegions { get; init; }
}

/// <summary>A reviewable heading-derived bookmark.</summary>
public sealed record PdfBookmarkProposal
{
    /// <summary>Gets the proposed bookmark title.</summary>
    public required string Title { get; init; }
    /// <summary>Gets the zero-based target page.</summary>
    public int PageIndex { get; init; }
    /// <summary>Gets the detected heading bounds.</summary>
    public PdfContentBounds Bounds { get; init; }
    /// <summary>Gets the detected effective text size.</summary>
    public double PointSize { get; init; }
    /// <summary>Gets the proposed zero-based hierarchy level.</summary>
    public int Level { get; init; }
    /// <summary>Gets the explicit review decision.</summary>
    public PdfBookmarkProposalDecision Decision { get; init; }
}

/// <summary>The review state of a generated bookmark proposal.</summary>
public enum PdfBookmarkProposalDecision
{
    /// <summary>The proposal has not been reviewed.</summary>
    Pending,
    /// <summary>The proposal should be authored.</summary>
    Accepted,
    /// <summary>The proposal should not be authored.</summary>
    Rejected
}
