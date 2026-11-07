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
        Placement = image.Placement;
        BoundsUnsupportedReason = GetBoundsUnsupportedReason();
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

    /// <summary>Gets whether the occurrence supports moving and resizing; editing also checks document restrictions.</summary>
    public bool CanSetBounds => BoundsUnsupportedReason is null;
    /// <summary>Gets a stable bounds-editing refusal reason, or null for a supported occurrence.</summary>
    public string? BoundsUnsupportedReason { get; }

    private string? GetBoundsUnsupportedReason()
    {
        if (UnsupportedReason is not null) return UnsupportedReason;
        if (Placement is not { } matrix) return "MissingPlacement";
        if (!double.IsFinite(matrix.A) || !double.IsFinite(matrix.B)
            || !double.IsFinite(matrix.C) || !double.IsFinite(matrix.D)
            || !double.IsFinite(matrix.E) || !double.IsFinite(matrix.F)
            || matrix.A == 0 || matrix.D == 0) return "InvalidPlacement";
        if (matrix.B != 0 || matrix.C != 0) return "NonAxisAlignedPlacement";
        double right = matrix.E + matrix.A, top = matrix.F + matrix.D;
        if (!double.IsFinite(right) || !double.IsFinite(top)) return "InvalidPlacement";
        var fullBounds = new PdfContentBounds(Math.Min(matrix.E, right), Math.Min(matrix.F, top),
            Math.Max(matrix.E, right), Math.Max(matrix.F, top));
        if (fullBounds != BoundingBox) return "ClippedImage";
        if (!double.IsFinite(fullBounds.Width) || !double.IsFinite(fullBounds.Height)
            || fullBounds.Width <= 0 || fullBounds.Height <= 0) return "InvalidPlacement";
        return null;
    }

    internal PdfTextContentReader.Matrix? Placement { get; }
    internal PdfDocument Owner { get; }
    internal IReadOnlyList<PdfContentInstruction> Instructions { get; }
    internal int Ordinal { get; }
    internal PdfStream? Resource { get; }
}
