using KillerPdf.Engine.Documents;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Captures requested proof preferences for one view of a logical document.</summary>
public sealed class PdfDocumentProofView(PdfLogicalDocument document)
{
    private readonly Lock _proofLock = new();
    private PdfDocumentProofSettings? _comparisonOverride;
    private long _overrideGeneration;

    /// <summary>Gets the logical owner shared by normal views.</summary>
    public PdfLogicalDocument Document { get; } = document ?? throw new ArgumentNullException(nameof(document));
    /// <summary>Gets this view's identity.</summary>
    public Guid Id { get; } = Guid.NewGuid();
    /// <summary>Gets or sets optional proof preferences local to this comparison view.</summary>
    public PdfDocumentProofSettings? ComparisonOverride
    {
        get { lock (_proofLock) return _comparisonOverride; }
        set
        {
            lock (_proofLock)
            {
                if (_comparisonOverride is null && value is null) return;
                if (_comparisonOverride is { } current && value is not null
                    && current.Enabled == value.Enabled && current.GamutWarnings == value.GamutWarnings
                    && current.Options == value.Options
                    && current.PrinterProfile?.ContentSha256 == value.PrinterProfile?.ContentSha256) return;
                long generation = checked(_overrideGeneration + 1);
                _comparisonOverride = value;
                _overrideGeneration = generation;
            }
        }
    }
    /// <summary>Captures coherent requested preferences and their generation stamp.</summary>
    public PdfDocumentProofViewSnapshot CaptureProof()
    {
        lock (_proofLock)
        {
            if (_comparisonOverride is { } comparison)
                return new(comparison, new(Id, Document.Id, 0, _overrideGeneration, true));
            var captured = Document.CaptureProof();
            return new(captured.Settings, new(Id, Document.Id, captured.Generation, _overrideGeneration, false));
        }
    }
}

/// <summary>Immutable requested proof preferences and their view identity.</summary>
public sealed record PdfDocumentProofViewSnapshot(PdfDocumentProofSettings Settings, PdfDocumentProofViewStamp Stamp);

/// <summary>Identifies the requested proof state captured for a view.</summary>
public readonly record struct PdfDocumentProofViewStamp(Guid ViewId, Guid DocumentId,
    long DocumentGeneration, long OverrideGeneration, bool IsComparison);
