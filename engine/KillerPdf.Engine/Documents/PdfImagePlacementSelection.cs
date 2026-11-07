using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>An immutable image occurrence bound to the document that read it.</summary>
public sealed class PdfImagePlacementSelection
{
    internal PdfImagePlacementSelection(PdfDocument owner, int pageIndex, PdfExtractedImage image,
        IReadOnlyList<PdfContentInstruction> instructions, int ordinal, PdfStream? resource, string? reason)
    {
        Owner = owner;
        PageIndex = pageIndex;
        BoundingBox = image.BoundingBox;
        PixelWidth = image.PixelWidth;
        PixelHeight = image.PixelHeight;
        ResourceName = image.ResourceName;
        Instructions = instructions;
        Ordinal = ordinal;
        Resource = resource;
        UnsupportedReason = reason;
    }

    /// <summary>Gets the zero-based page index.</summary>
    public int PageIndex { get; }
    /// <summary>Gets the visible bounds in page extraction coordinates.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets the source pixel width.</summary>
    public int PixelWidth { get; }
    /// <summary>Gets the source pixel height.</summary>
    public int PixelHeight { get; }
    /// <summary>Gets the image resource name, or null for an inline image.</summary>
    public string? ResourceName { get; }
    /// <summary>Gets whether the page content supports removal; editing also checks document restrictions.</summary>
    public bool CanRemove => UnsupportedReason is null;
    /// <summary>Gets a stable content refusal reason, or null when the content supports removal.</summary>
    public string? UnsupportedReason { get; }

    internal PdfDocument Owner { get; }
    internal IReadOnlyList<PdfContentInstruction> Instructions { get; }
    internal int Ordinal { get; }
    internal PdfStream? Resource { get; }
}
