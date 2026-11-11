using System.Runtime.CompilerServices;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Classifies a printer profile's colorimetric round-trip difference.</summary>
public enum PdfGamutClassification
{
    /// <summary>The difference is below the warning boundary band.</summary>
    Inside,
    /// <summary>The difference lies in the numerical warning boundary band.</summary>
    Boundary,
    /// <summary>The difference exceeds the warning boundary band.</summary>
    Outside,
    /// <summary>No color was evaluated, such as a fully transparent frame pixel.</summary>
    NotEvaluated
}

/// <summary>An independent printer-gamut observation without modified color pixels.</summary>
/// <param name="DeltaE76">The relative D50 CIE1976 Lab round-trip difference.</param>
/// <param name="Classification">The classification under the application's warning policy.</param>
public readonly record struct PdfGamutCheckResult(double DeltaE76, PdfGamutClassification Classification);

/// <summary>Checks intended D50 colors against a printer's relative-colorimetric round trip.</summary>
public sealed class PdfPrinterGamutCheck
{
    private readonly PdfColorProfile _printer;
    private readonly Rendering.PdfProofPixelChain _roundTrip;

    /// <summary>Gets the warning threshold in CIE1976 Lab units where the printer's tables round-trip precisely.</summary>
    /// <remarks>About one just-noticeable difference: a color the printer moves further than this is warned.</remarks>
    public const double WarningThreshold = 2;
    /// <summary>Gets the warning threshold in CIE1976 Lab units, and the error ratio, for imprecise printer tables.</summary>
    /// <remarks>
    /// Table-based CMYK profiles commonly move even printable colors several units on a round trip.
    /// Where a second round trip shows that table error, the first difference must exceed this value,
    /// and also this multiple of the table error, before the color is warned.
    /// </remarks>
    public const double ImpreciseTableThreshold = 5;
    /// <summary>Gets the numerical boundary band's half-width in CIE1976 Lab units.</summary>
    public const double BoundaryTolerance = .05;

    /// <summary>Creates a printer-gamut check independent of display and proof simulation.</summary>
    public PdfPrinterGamutCheck(PdfColorProfile printer) : this(printer, false) { }

    /// <summary>Creates a printer-gamut check that optionally applies black-point compensation first.</summary>
    /// <remarks>
    /// With compensation the intended color is first mapped from reference black onto the printer's
    /// relative-colorimetric black, as a compensated proof does, so ordinary black is not warned.
    /// A printer whose black point cannot be estimated is checked without compensation.
    /// </remarks>
    public PdfPrinterGamutCheck(PdfColorProfile printer, bool blackPointCompensation)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var forward = printer.GetTransformSelection(Authoring.PdfRenderingIntent.RelativeColorimetric);
        var reverse = printer.GetTransformSelection(Authoring.PdfRenderingIntent.RelativeColorimetric,
            PdfColorTransformDirection.ConnectionToDevice);
        if (printer.ProfileClassSignature != "prtr" || !forward.IsAvailable || forward.UsesIntentFallback
            || !reverse.IsAvailable || reverse.UsesIntentFallback)
            throw new NotSupportedException("Printer gamut checking requires actual relative-colorimetric output transforms in both directions.");
        _printer = printer;
        _roundTrip = new Rendering.PdfProofPixelChain(printer,
            new PdfProofOptions { BlackPointCompensation = false, SimulateBlack = true });
        _grid = new Lazy<float[]>(BuildGrid, LazyThreadSafetyMode.ExecutionAndPublication);
        if (blackPointCompensation)
            try { _compensation = printer.ReferenceBlackToDeviceCompensation(Authoring.PdfRenderingIntent.RelativeColorimetric); }
            catch (NotSupportedException) { }
    }

    private readonly Rendering.PdfBlackPointCompensation _compensation = new(1);

    /// <summary>Evaluates intended relative D50 XYZ before display conversion or clipping.</summary>
    /// <remarks>
    /// Uses a relative-colorimetric printer round trip and CIE1976 Lab difference, after black-point
    /// compensation when the check was created with it; the difference is measured from the compensated color.
    /// A second round trip of the returned color measures the printer tables' own error, which a
    /// color already on the printer's gamut shows alone. Where that error is below
    /// <see cref="WarningThreshold"/> the difference is compared with that threshold; otherwise it must
    /// exceed <see cref="ImpreciseTableThreshold"/> and that multiple of the table error.
    /// The thresholds are an application warning policy, not a physical measurement or visibility guarantee.
    /// The check does not change intended colors, proof pixels or display pixels.
    /// </remarks>
    public PdfGamutCheckResult EvaluateXyz(ReadOnlySpan<double> xyz)
    {
        if (xyz.Length != 3) throw new ArgumentException("D50 XYZ requires three channels.", nameof(xyz));
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(xyz[channel]))
                throw new ArgumentException("D50 XYZ channels must be finite.", nameof(xyz));
        Span<double> intended = stackalloc double[3];
        xyz.CopyTo(intended);
        _compensation.Apply(intended);
        var (difference, threshold) = Measure(intended);
        return new(difference, Classify(difference, threshold));
    }

    // Lab lookup grid: 33 nodes over L 0 to 100 and a, b -128 to 128 (3.125 and 8 units apart).
    private const int GridNodes = 33;
    private const double GridLightnessStep = 100d / (GridNodes - 1), GridChromaStep = 256d / (GridNodes - 1);
    private static readonly ConditionalWeakTable<PdfColorProfile, PdfPrinterGamutCheck?[]> Shared = new();
    private readonly Lazy<float[]> _grid;

    /// <summary>Gets the check shared by every render with this printer and compensation, with its lookup grid.</summary>
    internal static PdfPrinterGamutCheck For(PdfColorProfile printer, bool blackPointCompensation)
    {
        var checks = Shared.GetValue(printer, _ => new PdfPrinterGamutCheck?[2]);
        int slot = blackPointCompensation ? 1 : 0;
        lock (checks)
            return checks[slot] ??= new PdfPrinterGamutCheck(printer, blackPointCompensation);
    }

    /// <summary>Classifies intended relative D50 XYZ through the lookup grid, built once per check.</summary>
    /// <remarks>
    /// Each grid node holds its exact warning margin (round-trip difference less the threshold).
    /// Colors inside the grid interpolate the margins of their eight surrounding nodes and keep the
    /// boundary band; colors outside it, or next to a node the printer cannot evaluate, are evaluated exactly.
    /// </remarks>
    internal PdfGamutClassification ClassifyXyz(ReadOnlySpan<double> xyz)
    {
        Span<double> intended = stackalloc double[3];
        xyz.CopyTo(intended);
        _compensation.Apply(intended);
        var (lightness, a, b) = Lab(intended);
        double gl = lightness / GridLightnessStep, ga = (a + 128) / GridChromaStep, gb = (b + 128) / GridChromaStep;
        if (!(gl >= 0 && gl <= GridNodes - 1 && ga >= 0 && ga <= GridNodes - 1 && gb >= 0 && gb <= GridNodes - 1))
            return Exact(intended);
        int il = Math.Min((int)gl, GridNodes - 2), ia = Math.Min((int)ga, GridNodes - 2), ib = Math.Min((int)gb, GridNodes - 2);
        double fl = gl - il, fa = ga - ia, fb = gb - ib;
        float[] grid = _grid.Value;
        double margin = 0;
        for (int corner = 0; corner < 8; corner++)
        {
            int dl = corner >> 2, da = (corner >> 1) & 1, db = corner & 1;
            float node = grid[((il + dl) * GridNodes + ia + da) * GridNodes + ib + db];
            if (float.IsNaN(node)) return Exact(intended);
            margin += node * (dl == 1 ? fl : 1 - fl) * (da == 1 ? fa : 1 - fa) * (db == 1 ? fb : 1 - fb);
        }
        return Classify(margin);

        PdfGamutClassification Exact(ReadOnlySpan<double> color)
        {
            var (difference, threshold) = Measure(color);
            return Classify(difference, threshold);
        }
    }

    private float[] BuildGrid()
    {
        var grid = new float[GridNodes * GridNodes * GridNodes];
        Span<double> xyz = stackalloc double[3];
        for (int l = 0; l < GridNodes; l++)
            for (int a = 0; a < GridNodes; a++)
                for (int b = 0; b < GridNodes; b++)
                {
                    FromLab(l * GridLightnessStep, a * GridChromaStep - 128, b * GridChromaStep - 128, xyz);
                    float margin;
                    try
                    {
                        var (difference, threshold) = Measure(xyz);
                        margin = (float)(difference - threshold);
                    }
                    catch (NotSupportedException) { margin = float.NaN; }
                    grid[(l * GridNodes + a) * GridNodes + b] = margin;
                }
        return grid;
    }

    // The first round-trip difference and the warning threshold the table error sets for it.
    private (double Difference, double Threshold) Measure(ReadOnlySpan<double> intended)
    {
        Span<double> device = stackalloc double[_printer.ComponentCount];
        Span<double> returned = stackalloc double[3];
        Span<double> again = stackalloc double[3];
        RoundTrip(intended, device, returned);
        double difference = Difference(intended, returned);
        RoundTrip(returned, device, again);
        double tableError = Difference(returned, again);
        double threshold = tableError < WarningThreshold ? WarningThreshold
            : tableError < ImpreciseTableThreshold ? ImpreciseTableThreshold
            : ImpreciseTableThreshold * tableError;
        return (difference, threshold);
    }

    private static PdfGamutClassification Classify(double difference, double threshold) =>
        difference < threshold - BoundaryTolerance ? PdfGamutClassification.Inside
        : difference > threshold + BoundaryTolerance ? PdfGamutClassification.Outside
        : PdfGamutClassification.Boundary;

    private static PdfGamutClassification Classify(double margin) =>
        margin < -BoundaryTolerance ? PdfGamutClassification.Inside
        : margin > BoundaryTolerance ? PdfGamutClassification.Outside
        : PdfGamutClassification.Boundary;

    private static void FromLab(double lightness, double a, double b, Span<double> xyz)
    {
        double y = (lightness + 16) / 116, x = y + a / 500, z = y - b / 200;
        xyz[0] = .9642 * Inverse(x);
        xyz[1] = Inverse(y);
        xyz[2] = .8249 * Inverse(z);

        static double Inverse(double t) => t > 6d / 29 ? t * t * t : 108d / 841 * (t - 4d / 29);
    }

    private void RoundTrip(ReadOnlySpan<double> xyz, Span<double> device, Span<double> returned)
    {
        // Relative proofing corrects small printer-table white errors. Classifying the same corrected
        // round trip keeps an unprinted area inside the gamut the proof actually shows.
        xyz.CopyTo(returned);
        var rgb = Rendering.PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
        _roundTrip.Apply(returned, Math.Min(rgb.Red, Math.Min(rgb.Green, rgb.Blue)), device);
    }

    private static double Difference(ReadOnlySpan<double> first, ReadOnlySpan<double> second)
    {
        var firstLab = Lab(first);
        var secondLab = Lab(second);
        double lightness = firstLab.Lightness - secondLab.Lightness;
        double a = firstLab.A - secondLab.A, b = firstLab.B - secondLab.B;
        double maximum = Math.Max(Math.Abs(lightness), Math.Max(Math.Abs(a), Math.Abs(b)));
        double difference = maximum == 0 ? 0 : maximum * Math.Sqrt(
            Math.Pow(lightness / maximum, 2) + Math.Pow(a / maximum, 2) + Math.Pow(b / maximum, 2));
        if (!double.IsFinite(difference))
            throw new NotSupportedException("The printer gamut difference exceeds finite numerical range.");
        return difference;
    }

    private static (double Lightness, double A, double B) Lab(ReadOnlySpan<double> xyz)
    {
        double x = Response(xyz[0] / .9642), y = Response(xyz[1]), z = Response(xyz[2] / .8249);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));

        static double Response(double ratio) => ratio > 216d / 24389
            ? Math.Cbrt(ratio) : (841d / 108) * ratio + 4d / 29;
    }
}
