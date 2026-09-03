using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>One material change proposed by a document optimization plan.</summary>
public enum PdfOptimizationChangeKind
{
    /// <summary>Replace incremental history with one complete revision.</summary>
    ConsolidateRevisions,
    /// <summary>Remove document information and XMP metadata.</summary>
    RemoveMetadata,
    /// <summary>Write eligible objects into compressed object streams.</summary>
    PackObjects,
    /// <summary>Compress structural streams.</summary>
    CompressStructure
}

/// <summary>Explicit lossless optimization and sanitization choices.</summary>
public sealed record PdfOptimizationOptions
{
    /// <summary>Gets whether descriptive document information and XMP are removed.</summary>
    public bool RemoveMetadata { get; init; }
    /// <summary>Gets whether eligible objects are packed into object streams.</summary>
    public bool PackObjects { get; init; } = true;
    /// <summary>Gets whether structural streams are compressed.</summary>
    public bool CompressStructure { get; init; } = true;
    /// <summary>Gets whether signatures may be invalidated by the required full rewrite.</summary>
    public bool AllowSignatureInvalidation { get; init; }
}

/// <summary>A completed optimization and its measured size change.</summary>
public sealed record PdfOptimizationResult(ReadOnlyMemory<byte> Data, int OriginalSize,
    int OutputSize, IReadOnlyList<PdfOptimizationChangeKind> Changes)
{
    /// <summary>Gets the signed output-size difference in bytes.</summary>
    public int SizeDifference => OutputSize - OriginalSize;
}

/// <summary>An immutable preview of a deterministic full-document optimization.</summary>
public sealed class PdfOptimizationPlan
{
    private readonly PdfDocument _document;
    private readonly PdfOptimizationOptions _options;

    internal PdfOptimizationPlan(PdfDocument document, PdfOptimizationOptions options,
        IEnumerable<PdfOptimizationChangeKind> changes)
    {
        _document = document;
        _options = options;
        Changes = Array.AsReadOnly(changes.ToArray());
    }

    /// <summary>Gets the original byte count.</summary>
    public int OriginalSize => _document.Source.Length;
    /// <summary>Gets every material change in application order.</summary>
    public IReadOnlyList<PdfOptimizationChangeKind> Changes { get; }

    /// <summary>Applies the previewed plan and verifies that the result reopens with the same page count.</summary>
    public PdfOptimizationResult Apply()
    {
        int pageCount = PdfPageTree.Read(_document).Pages.Count;
        byte[] output = PdfDocumentWriter.Write(_document, new PdfDocumentWriteOptions
        {
            MetadataPolicy = _options.RemoveMetadata
                ? PdfMetadataPolicy.RemoveDocumentInformationAndXmp : PdfMetadataPolicy.Preserve,
            CrossReferenceFormat = _options.PackObjects || _options.CompressStructure
                ? PdfCrossReferenceFormat.Stream : PdfCrossReferenceFormat.Table,
            UseObjectStreams = _options.PackObjects,
            CompressStructuralStreams = _options.CompressStructure,
            AllowSignatureInvalidation = _options.AllowSignatureInvalidation
        });
        PdfDocument reopened = PdfDocument.Open(output);
        if (PdfPageTree.Read(reopened).Pages.Count != pageCount)
            throw new InvalidOperationException("The optimized document did not preserve its page count.");
        if (reopened.CrossReferences.Sections.Count != 1)
            throw new InvalidOperationException("The optimized document still contains revision history.");
        return new PdfOptimizationResult(output, OriginalSize, output.Length, Changes);
    }
}

/// <summary>Previews deterministic lossless structural optimization and metadata sanitization.</summary>
public static class PdfOptimizer
{
    private static readonly PdfName InformationName = Name("Info");
    private static readonly PdfName MetadataName = Name("Metadata");

    /// <summary>Creates an explainable plan without changing the document.</summary>
    public static PdfOptimizationPlan CreatePlan(PdfDocument document,
        PdfOptimizationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        options ??= new PdfOptimizationOptions();
        var changes = new List<PdfOptimizationChangeKind> { PdfOptimizationChangeKind.ConsolidateRevisions };
        PdfPageTree tree = PdfPageTree.Read(document);
        if (options.RemoveMetadata && (document.Trailer.ContainsKey(InformationName)
            || tree.Catalog.ContainsKey(MetadataName)))
            changes.Add(PdfOptimizationChangeKind.RemoveMetadata);
        if (options.PackObjects) changes.Add(PdfOptimizationChangeKind.PackObjects);
        if (options.CompressStructure) changes.Add(PdfOptimizationChangeKind.CompressStructure);
        return new PdfOptimizationPlan(document, options, changes);
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));
}
