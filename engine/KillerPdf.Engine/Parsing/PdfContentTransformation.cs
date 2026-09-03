using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>A finite affine matrix for direct page-content transformations.</summary>
public readonly record struct PdfContentTransformMatrix
{
    /// <summary>Creates an affine matrix in PDF operand order.</summary>
    public PdfContentTransformMatrix(double a, double b, double c, double d, double e, double f)
    {
        if (!double.IsFinite(a) || !double.IsFinite(b) || !double.IsFinite(c)
            || !double.IsFinite(d) || !double.IsFinite(e) || !double.IsFinite(f))
            throw new ArgumentOutOfRangeException(nameof(a), "Content transformation values must be finite.");
        A = a; B = b; C = c; D = d; E = e; F = f;
    }

    /// <summary>Gets the horizontal scale or rotation value.</summary>
    public double A { get; }
    /// <summary>Gets the vertical skew or rotation value.</summary>
    public double B { get; }
    /// <summary>Gets the horizontal skew or rotation value.</summary>
    public double C { get; }
    /// <summary>Gets the vertical scale or rotation value.</summary>
    public double D { get; }
    /// <summary>Gets the horizontal translation.</summary>
    public double E { get; }
    /// <summary>Gets the vertical translation.</summary>
    public double F { get; }

    /// <summary>Creates a translation.</summary>
    public static PdfContentTransformMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    /// <summary>Creates independent horizontal and vertical scaling.</summary>
    public static PdfContentTransformMatrix Scale(double x, double y)
    {
        if (x == 0 || y == 0) throw new ArgumentOutOfRangeException(nameof(x), "Content scale cannot be zero.");
        return new(x, 0, 0, y, 0, 0);
    }
    /// <summary>Creates counterclockwise rotation around the origin.</summary>
    public static PdfContentTransformMatrix Rotation(double degrees)
    {
        if (!double.IsFinite(degrees)) throw new ArgumentOutOfRangeException(nameof(degrees));
        double radians = degrees * Math.PI / 180;
        double cosine = Math.Cos(radians), sine = Math.Sin(radians);
        return new(cosine, sine, -sine, cosine, 0, 0);
    }
}

/// <summary>Rewrites selected instructions or transforms a complete decoded content stream.</summary>
public static class PdfContentTransformation
{
    /// <summary>Removes or replaces zero-based instructions while preserving every untouched instruction.</summary>
    public static IReadOnlyList<PdfContentInstruction> Rewrite(
        IEnumerable<PdfContentInstruction> instructions,
        IReadOnlyDictionary<int, PdfContentInstruction?> changes)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(changes);
        PdfContentInstruction[] source = instructions.ToArray();
        if (changes.Keys.Any(index => index < 0 || index >= source.Length))
            throw new ArgumentOutOfRangeException(nameof(changes), "A changed instruction index is outside the stream.");
        var result = new List<PdfContentInstruction>(source.Length);
        for (int index = 0; index < source.Length; index++)
        {
            if (!changes.TryGetValue(index, out PdfContentInstruction? replacement))
                result.Add(source[index]);
            else if (replacement is not null)
                result.Add(replacement);
        }
        return Array.AsReadOnly(result.ToArray());
    }

    /// <summary>Wraps all existing content in an isolated affine transformation.</summary>
    public static IReadOnlyList<PdfContentInstruction> TransformAll(
        IEnumerable<PdfContentInstruction> instructions, PdfContentTransformMatrix matrix)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        PdfContentInstruction[] source = instructions.ToArray();
        var result = new List<PdfContentInstruction>(source.Length + 3)
        {
            new("q", 0, []),
            new("cm", 0, [new PdfReal(matrix.A), new PdfReal(matrix.B), new PdfReal(matrix.C),
                new PdfReal(matrix.D), new PdfReal(matrix.E), new PdfReal(matrix.F)])
        };
        result.AddRange(source);
        result.Add(new PdfContentInstruction("Q", 0, []));
        return Array.AsReadOnly(result.ToArray());
    }
}
