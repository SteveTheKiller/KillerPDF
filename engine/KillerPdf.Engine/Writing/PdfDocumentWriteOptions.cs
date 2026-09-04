using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Writing;

/// <summary>Controls which descriptive metadata a full rewrite preserves.</summary>
public enum PdfMetadataPolicy
{
    /// <summary>Preserves document information and XMP metadata.</summary>
    Preserve,
    /// <summary>Removes the trailer document-information dictionary.</summary>
    RemoveDocumentInformation,
    /// <summary>Removes both document information and catalog XMP metadata.</summary>
    RemoveDocumentInformationAndXmp
}

/// <summary>The cross-reference representation emitted by a full rewrite.</summary>
public enum PdfCrossReferenceFormat
{
    /// <summary>A classic cross-reference table.</summary>
    Table,
    /// <summary>A PDF 1.5 or later cross-reference stream.</summary>
    Stream
}

/// <summary>Explicit policy choices for a deterministic full rewrite.</summary>
public sealed class PdfDocumentWriteOptions
{
    /// <summary>Removes password encryption from an authenticated source during the full rewrite.</summary>
    public bool RemoveEncryption { get; init; }

    /// <summary>
    /// Permits a full rewrite of a document containing signed signature fields. Full rewrites
    /// necessarily invalidate their byte ranges, so this must be selected explicitly.
    /// </summary>
    public bool AllowSignatureInvalidation { get; init; }

    /// <summary>Null preserves the source header version. Rewrites may upgrade but not downgrade.</summary>
    public PdfVersion? TargetVersion { get; init; }

    /// <summary>Gets the policy for descriptive document information and XMP metadata.</summary>
    public PdfMetadataPolicy MetadataPolicy { get; init; } = PdfMetadataPolicy.Preserve;

    /// <summary>Preserves the trailer /ID pair independently from descriptive document information.</summary>
    public bool PreserveDocumentIdentifiers { get; init; } = true;

    /// <summary>Controls whether a full rewrite ends with a classic table or a PDF 1.5+ cross-reference stream.</summary>
    public PdfCrossReferenceFormat CrossReferenceFormat { get; init; } = PdfCrossReferenceFormat.Table;

    /// <summary>Packs eligible generation-zero non-stream objects into one deterministic object stream.</summary>
    public bool UseObjectStreams { get; init; }

    /// <summary>Applies deterministic Flate compression to emitted cross-reference and object streams.</summary>
    public bool CompressStructuralStreams { get; init; }

    /// <summary>Omits active objects that are unreachable from the output trailer.</summary>
    public bool PruneUnreachableObjects { get; init; }

    /// <summary>Flate-compresses unfiltered embedded streams when doing so reduces their size.</summary>
    public bool CompressUnfilteredStreams { get; init; }
}
