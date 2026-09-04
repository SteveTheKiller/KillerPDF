namespace KillerPdf.Engine.Documents;

/// <summary>A category of structural page-content change.</summary>
public enum PdfStructuralChangeKind
{
    /// <summary>A page exists only in the changed document.</summary>
    PageAdded,
    /// <summary>A page exists only in the original document.</summary>
    PageRemoved,
    /// <summary>The page dimensions changed.</summary>
    PageSize,
    /// <summary>Extracted text or its geometry and font metadata changed.</summary>
    Text,
    /// <summary>Image placements changed.</summary>
    Images,
    /// <summary>Vector paths changed.</summary>
    Paths,
    /// <summary>Shading placements changed.</summary>
    Shadings
}

/// <summary>One page-level structural difference between two documents.</summary>
public sealed record PdfStructuralChange(
    int PageIndex,
    PdfStructuralChangeKind Kind,
    int OriginalCount,
    int ChangedCount);

/// <summary>A structural comparison of interpreted page content.</summary>
public sealed class PdfStructuralComparison
{
    private PdfStructuralComparison(IEnumerable<PdfStructuralChange> changes)
    {
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets structural changes in page and category order.</summary>
    public IReadOnlyList<PdfStructuralChange> Changes { get; }
    /// <summary>Gets whether any structural page-content change was found.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Compares interpreted text, images, paths, shadings, and page geometry.</summary>
    public static PdfStructuralComparison Compare(PdfDocument original, PdfDocument changed,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(changed);
        var before = new PdfPageContentReader(original);
        var after = new PdfPageContentReader(changed);
        var changes = new List<PdfStructuralChange>();
        int sharedPages = Math.Min(before.PageCount, after.PageCount);
        for (int pageIndex = 0; pageIndex < sharedPages; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfPageContent left = before.Read(pageIndex, cancellationToken);
            PdfPageContent right = after.Read(pageIndex, cancellationToken);
            if (left.Width != right.Width || left.Height != right.Height)
                changes.Add(new(pageIndex, PdfStructuralChangeKind.PageSize, 1, 1));
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Text,
                left.Letters, right.Letters);
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Images,
                left.Images, right.Images);
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Paths,
                left.Paths.Select(PathSignature), right.Paths.Select(PathSignature));
            AddDifference(changes, pageIndex, PdfStructuralChangeKind.Shadings,
                left.Shadings, right.Shadings);
        }
        for (int pageIndex = sharedPages; pageIndex < before.PageCount; pageIndex++)
            changes.Add(new(pageIndex, PdfStructuralChangeKind.PageRemoved, 1, 0));
        for (int pageIndex = sharedPages; pageIndex < after.PageCount; pageIndex++)
            changes.Add(new(pageIndex, PdfStructuralChangeKind.PageAdded, 0, 1));
        return new PdfStructuralComparison(changes);
    }

    private static void AddDifference<T>(List<PdfStructuralChange> changes, int pageIndex,
        PdfStructuralChangeKind kind, IEnumerable<T> original, IEnumerable<T> changed)
    {
        T[] left = [.. original];
        T[] right = [.. changed];
        if (!left.SequenceEqual(right))
            changes.Add(new(pageIndex, kind, left.Length, right.Length));
    }

    private static string PathSignature(PdfExtractedPath path) => string.Join('|',
        path.PaintOperator, path.IsClippingPath, path.BoundingBox,
        string.Join(';', path.Segments.Select(segment =>
            $"{segment.Operator}:{string.Join(',', segment.Points)}")));
}
