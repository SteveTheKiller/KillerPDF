namespace KillerPdf.Engine.Documents;

/// <summary>The attachment surface where a comparison change was found.</summary>
public enum PdfAttachmentChangeScope
{
    /// <summary>A document-level embedded-file registration.</summary>
    Document,
    /// <summary>A file-attachment annotation placed on a page.</summary>
    PageAnnotation
}

/// <summary>A category of attachment change.</summary>
public enum PdfAttachmentChangeKind
{
    /// <summary>An attachment exists only in the changed document.</summary>
    Added,
    /// <summary>An attachment exists only in the original document.</summary>
    Removed,
    /// <summary>The decoded embedded-file payload changed.</summary>
    Payload,
    /// <summary>Attachment metadata or portfolio values changed.</summary>
    Metadata,
    /// <summary>A page annotation's target, geometry, icon, or description changed.</summary>
    Placement
}

/// <summary>One attachment difference between two documents.</summary>
public sealed record PdfAttachmentChange(
    PdfAttachmentChangeScope Scope,
    PdfAttachmentChangeKind Kind,
    string FileName,
    int? PageIndex = null,
    int? AnnotationIndex = null);

/// <summary>A deterministic comparison of embedded files and their page placements.</summary>
public sealed class PdfAttachmentComparison
{
    private PdfAttachmentComparison(IEnumerable<PdfAttachmentChange> changes) =>
        Changes = Array.AsReadOnly(changes.ToArray());

    /// <summary>Gets changes in document, page, annotation, and category order.</summary>
    public IReadOnlyList<PdfAttachmentChange> Changes { get; }

    /// <summary>Gets whether any attachment change was found.</summary>
    public bool HasChanges => Changes.Count > 0;

    /// <summary>Compares document-level embedded files and page attachment annotations.</summary>
    public static PdfAttachmentComparison Compare(
        PdfDocument original, PdfDocument changed)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(changed);
        var changes = new List<PdfAttachmentChange>();
        CompareDocumentAttachments(original, changed, changes);
        ComparePageAnnotations(original, changed, changes);
        return new PdfAttachmentComparison(changes);
    }

    private static void CompareDocumentAttachments(PdfDocument original,
        PdfDocument changed, ICollection<PdfAttachmentChange> changes)
    {
        Dictionary<string, PdfAttachmentInfo> before = PdfAttachmentReader.Read(original)
            .ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        Dictionary<string, PdfAttachmentInfo> after = PdfAttachmentReader.Read(changed)
            .ToDictionary(item => item.FileName, StringComparer.OrdinalIgnoreCase);
        foreach (string name in before.Keys.Union(after.Keys, StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(name, out PdfAttachmentInfo? left))
            {
                changes.Add(new(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Added, after[name].FileName));
                continue;
            }
            if (!after.TryGetValue(name, out PdfAttachmentInfo? right))
            {
                changes.Add(new(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Removed, left.FileName));
                continue;
            }
            if (!left.Data.Span.SequenceEqual(right.Data.Span))
                changes.Add(new(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Payload, right.FileName));
            if (!MetadataEquals(left, right))
                changes.Add(new(PdfAttachmentChangeScope.Document,
                    PdfAttachmentChangeKind.Metadata, right.FileName));
        }
    }

    private static void ComparePageAnnotations(PdfDocument original,
        PdfDocument changed, ICollection<PdfAttachmentChange> changes)
    {
        int pageCount = Math.Max(PdfPageTree.Read(original).Pages.Count,
            PdfPageTree.Read(changed).Pages.Count);
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            IReadOnlyList<PdfAttachmentAnnotationInfo> before = pageIndex <
                PdfPageTree.Read(original).Pages.Count
                ? PdfAttachmentReader.ReadPageAnnotations(original, pageIndex) : [];
            IReadOnlyList<PdfAttachmentAnnotationInfo> after = pageIndex <
                PdfPageTree.Read(changed).Pages.Count
                ? PdfAttachmentReader.ReadPageAnnotations(changed, pageIndex) : [];
            int count = Math.Max(before.Count, after.Count);
            for (int index = 0; index < count; index++)
            {
                if (index >= before.Count)
                {
                    changes.Add(new(PdfAttachmentChangeScope.PageAnnotation,
                        PdfAttachmentChangeKind.Added, after[index].Attachment.FileName,
                        pageIndex, after[index].AnnotationIndex));
                    continue;
                }
                if (index >= after.Count)
                {
                    changes.Add(new(PdfAttachmentChangeScope.PageAnnotation,
                        PdfAttachmentChangeKind.Removed, before[index].Attachment.FileName,
                        pageIndex, before[index].AnnotationIndex));
                    continue;
                }
                PdfAttachmentAnnotationInfo left = before[index];
                PdfAttachmentAnnotationInfo right = after[index];
                if (!string.Equals(left.Attachment.FileName, right.Attachment.FileName,
                        StringComparison.OrdinalIgnoreCase)
                    || left.Left != right.Left || left.Bottom != right.Bottom
                    || left.Right != right.Right || left.Top != right.Top
                    || left.Icon != right.Icon || left.Contents != right.Contents)
                    changes.Add(new(PdfAttachmentChangeScope.PageAnnotation,
                        PdfAttachmentChangeKind.Placement, right.Attachment.FileName,
                        pageIndex, right.AnnotationIndex));
            }
        }
    }

    private static bool MetadataEquals(PdfAttachmentInfo left, PdfAttachmentInfo right) =>
        left.Description == right.Description
        && left.MimeType == right.MimeType
        && left.Relationship == right.Relationship
        && left.DeclaredSize == right.DeclaredSize
        && left.CreationDate == right.CreationDate
        && left.ModificationDate == right.ModificationDate
        && NullableBytesEqual(left.DeclaredChecksum, right.DeclaredChecksum)
        && left.CollectionValues.SequenceEqual(right.CollectionValues);

    private static bool NullableBytesEqual(
        ReadOnlyMemory<byte>? left, ReadOnlyMemory<byte>? right) =>
        left.HasValue == right.HasValue
        && (!left.HasValue || left.Value.Span.SequenceEqual(right!.Value.Span));
}
