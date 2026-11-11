using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Authoring;

/// <summary>Reusable coloured artwork or an uncoloured stencil repeated over a filled area.</summary>
public sealed class PdfTilingPattern
{
    /// <summary>Creates a validated colored pattern or uncolored stencil from reusable content.</summary>
    public PdfTilingPattern(
        double width, double height, PdfContentStreamBuilder content,
        double? horizontalStep = null, double? verticalStep = null,
        PdfTilingPatternType tilingType = PdfTilingPatternType.ConstantSpacing,
        PdfTilingPatternPaintType paintType = PdfTilingPatternPaintType.Colored,
        PdfPatternMatrix? matrix = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        Width = Positive(width, nameof(width));
        Height = Positive(height, nameof(height));
        HorizontalStep = NonZero(horizontalStep ?? width, nameof(horizontalStep));
        VerticalStep = NonZero(verticalStep ?? height, nameof(verticalStep));
        if (!Enum.IsDefined(tilingType)) throw new ArgumentOutOfRangeException(nameof(tilingType));
        if (!Enum.IsDefined(paintType)) throw new ArgumentOutOfRangeException(nameof(paintType));
        if (paintType == PdfTilingPatternPaintType.Uncolored
            && (content.HasColorOperators || NestedFormsSelectColors(content)))
            throw new ArgumentException(
                "An uncolored tiling pattern cannot contain color-selection or shading operators.",
                nameof(content));
        if (paintType == PdfTilingPatternPaintType.Uncolored && ContainsImage(content))
            throw new ArgumentException(
                "An uncolored tiling pattern cannot contain colored image assets.", nameof(content));
        if (content.MarkedContentIds.Count > 0)
            throw new ArgumentException(
                "Tagged marked content belongs to a page structure tree and cannot be captured inside a reusable pattern.",
                nameof(content));

        PdfPatternMatrix effectiveMatrix = matrix ?? PdfPatternMatrix.Identity;
        if (!double.IsFinite(effectiveMatrix.A)
            || !double.IsFinite(effectiveMatrix.B)
            || !double.IsFinite(effectiveMatrix.C)
            || !double.IsFinite(effectiveMatrix.D)
            || !double.IsFinite(effectiveMatrix.E)
            || !double.IsFinite(effectiveMatrix.F)
            || effectiveMatrix.A * effectiveMatrix.D
                - effectiveMatrix.B * effectiveMatrix.C == 0)
            throw new ArgumentOutOfRangeException(nameof(matrix),
                "A pattern matrix must be finite and invertible.");
        TilingType = tilingType;
        PaintType = paintType;
        Matrix = effectiveMatrix;
        Content = content.Build();
        Fonts = content.FontResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        EmbeddedFonts = [.. content.EmbeddedFontResources];
        Images = content.ImageResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Assets = content.AssetResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        OptionalContentGroups = content.OptionalContentResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        GraphicsStates = content.GraphicsStateResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Shadings = content.ShadingResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Forms = content.FormResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        Patterns = content.PatternResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        IccColorSpaces = content.IccColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        SpotColors = content.SpotColorResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        LabColorSpaces = content.LabColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        IndexedColorSpaces = content.IndexedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
        CalibratedColorSpaces = content.CalibratedColorSpaceResources.ToDictionary(entry => entry.Key, entry => entry.Value);
    }

    /// <summary>Gets the pattern-cell width.</summary>
    public double Width { get; }
    /// <summary>Gets the pattern-cell height.</summary>
    public double Height { get; }
    /// <summary>Gets the horizontal spacing between pattern cells.</summary>
    public double HorizontalStep { get; }
    /// <summary>Gets the vertical spacing between pattern cells.</summary>
    public double VerticalStep { get; }
    /// <summary>Gets the spacing-versus-distortion strategy.</summary>
    public PdfTilingPatternType TilingType { get; }
    /// <summary>Gets whether the pattern supplies its own colors or acts as a stencil.</summary>
    public PdfTilingPatternPaintType PaintType { get; }
    /// <summary>Gets the transformation from pattern space to default user space.</summary>
    public PdfPatternMatrix Matrix { get; }

    internal byte[] Content { get; }
    internal IReadOnlyDictionary<PdfStandardFont, PdfName> Fonts { get; }
    internal IReadOnlyCollection<EmbeddedFontUsage> EmbeddedFonts { get; }
    internal IReadOnlyDictionary<PdfImage, PdfName> Images { get; }
    internal IReadOnlyDictionary<PdfImageAsset, PdfName> Assets { get; }
    internal IReadOnlyDictionary<PdfOptionalContentGroup, PdfName> OptionalContentGroups { get; }
    internal IReadOnlyDictionary<PdfGraphicsState, PdfName> GraphicsStates { get; }
    internal IReadOnlyDictionary<PdfShading, PdfName> Shadings { get; }
    internal IReadOnlyDictionary<PdfFormXObject, PdfName> Forms { get; }
    internal IReadOnlyDictionary<PdfTilingPattern, PdfName> Patterns { get; }
    internal IReadOnlyDictionary<PdfIccProfile, PdfName> IccColorSpaces { get; }
    internal IReadOnlyDictionary<PdfSpotColor, PdfName> SpotColors { get; }
    internal IReadOnlyDictionary<PdfLabColorSpace, PdfName> LabColorSpaces { get; }
    internal IReadOnlyDictionary<PdfIndexedColorSpace, PdfName> IndexedColorSpaces { get; }
    internal IReadOnlyDictionary<PdfCalibratedColorSpace, PdfName> CalibratedColorSpaces { get; }

    // Images inside an uncolored cell carry their own colors, nested forms and patterns included.
    private static bool ContainsImage(PdfContentStreamBuilder content)
    {
        if (content.AssetResources.Count > 0 || content.ImageResources.Count > 0) return true;
        foreach (object nested in NestedContent(content))
            if (nested is PdfFormXObject { Assets.Count: > 0 } or PdfFormXObject { Images.Count: > 0 }
                or PdfTilingPattern { Assets.Count: > 0 } or PdfTilingPattern { Images.Count: > 0 })
                return true;
        return false;
    }

    // The builder tracks color operators for the cell itself, but a nested form keeps only its encoded content.
    private static bool NestedFormsSelectColors(PdfContentStreamBuilder content)
    {
        foreach (object nested in NestedContent(content))
        {
            if (nested is PdfFormXObject { Shadings.Count: > 0 } or PdfTilingPattern { Shadings.Count: > 0 })
                return true;
            byte[]? encoded = nested switch
            {
                PdfFormXObject form => form.Content,
                PdfTilingPattern pattern => pattern.Content,
                _ => null
            };
            if (encoded is null) continue;
            foreach (PdfContentInstruction instruction in PdfContentStreamReader.Read(encoded))
                if (instruction.Operator is "g" or "G" or "rg" or "RG" or "k" or "K" or "cs" or "CS"
                    or "sc" or "SC" or "scn" or "SCN" or "sh")
                    return true;
        }
        return false;
    }

    // Every form and pattern reachable from the cell, each visited once.
    private static IEnumerable<object> NestedContent(PdfContentStreamBuilder content)
    {
        var pending = new Stack<object>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (PdfFormXObject form in content.FormResources.Keys) pending.Push(form);
        foreach (PdfTilingPattern pattern in content.PatternResources.Keys) pending.Push(pattern);
        while (pending.TryPop(out object? item))
        {
            if (!visited.Add(item)) continue;
            yield return item;
            if (item is PdfFormXObject form)
            {
                foreach (PdfFormXObject nested in form.Forms.Keys) pending.Push(nested);
                foreach (PdfTilingPattern nested in form.Patterns.Keys) pending.Push(nested);
            }
            else if (item is PdfTilingPattern pattern)
            {
                foreach (PdfFormXObject nested in pattern.Forms.Keys) pending.Push(nested);
                foreach (PdfTilingPattern nested in pattern.Patterns.Keys) pending.Push(nested);
            }
        }
    }
    private static double Positive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0) throw new ArgumentOutOfRangeException(name);
        return value;
    }

    private static double NonZero(double value, string name)
    {
        if (!double.IsFinite(value) || value == 0) throw new ArgumentOutOfRangeException(name);
        return value;
    }
}

/// <summary>Controls the spacing-versus-distortion tradeoff used by a tiling pattern.</summary>
public enum PdfTilingPatternType
{
    /// <summary>Adjusts spacing to keep pattern cells undistorted.</summary>
    ConstantSpacing = 1,
    /// <summary>Adjusts pattern cells to keep spacing exact.</summary>
    NoDistortion = 2,
    /// <summary>Permits small spacing adjustments for faster tiling.</summary>
    FasterTiling = 3
}

/// <summary>Whether a tiling pattern supplies color or acts as an uncolored stencil.</summary>
public enum PdfTilingPatternPaintType
{
    /// <summary>The pattern content supplies its own colors.</summary>
    Colored = 1,
    /// <summary>The caller supplies color when painting the stencil.</summary>
    Uncolored = 2
}

/// <summary>Maps pattern space into the default user space.</summary>
public readonly record struct PdfPatternMatrix
{
    /// <summary>Creates a finite, invertible affine pattern matrix.</summary>
    public PdfPatternMatrix(double a, double b, double c, double d, double e, double f)
    {
        if (!double.IsFinite(a)) throw new ArgumentOutOfRangeException(nameof(a));
        if (!double.IsFinite(b)) throw new ArgumentOutOfRangeException(nameof(b));
        if (!double.IsFinite(c)) throw new ArgumentOutOfRangeException(nameof(c));
        if (!double.IsFinite(d)) throw new ArgumentOutOfRangeException(nameof(d));
        if (!double.IsFinite(e)) throw new ArgumentOutOfRangeException(nameof(e));
        if (!double.IsFinite(f)) throw new ArgumentOutOfRangeException(nameof(f));
        if (a * d - b * c == 0)
            throw new ArgumentException("A pattern matrix must be invertible.");
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    /// <summary>Gets the identity pattern matrix.</summary>
    public static PdfPatternMatrix Identity { get; } = new(1, 0, 0, 1, 0, 0);
    /// <summary>Gets the horizontal scale component.</summary>
    public double A { get; }
    /// <summary>Gets the vertical shear component.</summary>
    public double B { get; }
    /// <summary>Gets the horizontal shear component.</summary>
    public double C { get; }
    /// <summary>Gets the vertical scale component.</summary>
    public double D { get; }
    /// <summary>Gets the horizontal translation component.</summary>
    public double E { get; }
    /// <summary>Gets the vertical translation component.</summary>
    public double F { get; }
}
