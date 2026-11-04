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

    /// <summary>Gets the application warning threshold in CIE1976 Lab units.</summary>
    public const double WarningThreshold = 2;
    /// <summary>Gets the numerical boundary band's half-width in CIE1976 Lab units.</summary>
    public const double BoundaryTolerance = .05;

    /// <summary>Creates a printer-gamut check independent of display and proof simulation.</summary>
    public PdfPrinterGamutCheck(PdfColorProfile printer)
    {
        ArgumentNullException.ThrowIfNull(printer);
        var forward = printer.GetTransformSelection(Authoring.PdfRenderingIntent.RelativeColorimetric);
        var reverse = printer.GetTransformSelection(Authoring.PdfRenderingIntent.RelativeColorimetric,
            PdfColorTransformDirection.ConnectionToDevice);
        if (printer.ProfileClassSignature != "prtr" || !forward.IsAvailable || forward.UsesIntentFallback
            || !reverse.IsAvailable || reverse.UsesIntentFallback)
            throw new NotSupportedException("Printer gamut checking requires actual relative-colorimetric output transforms in both directions.");
        _printer = printer;
    }

    /// <summary>Evaluates intended relative D50 XYZ before display conversion or clipping.</summary>
    /// <remarks>
    /// Uses an uncompensated relative-colorimetric printer round trip and CIE1976 Lab difference.
    /// The threshold is an application warning policy, not a physical measurement or visibility guarantee.
    /// The check does not change intended colors, proof pixels or display pixels.
    /// </remarks>
    public PdfGamutCheckResult EvaluateXyz(ReadOnlySpan<double> xyz)
    {
        if (xyz.Length != 3) throw new ArgumentException("D50 XYZ requires three channels.", nameof(xyz));
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(xyz[channel]))
                throw new ArgumentException("D50 XYZ channels must be finite.", nameof(xyz));
        Span<double> device = stackalloc double[_printer.ComponentCount];
        Span<double> returned = stackalloc double[3];
        _printer.FromXyz(xyz, device, Authoring.PdfRenderingIntent.RelativeColorimetric);
        _printer.ToXyz(device, returned, Authoring.PdfRenderingIntent.RelativeColorimetric);
        var intendedLab = Lab(xyz);
        var returnedLab = Lab(returned);
        double lightness = intendedLab.Lightness - returnedLab.Lightness;
        double a = intendedLab.A - returnedLab.A, b = intendedLab.B - returnedLab.B;
        double maximum = Math.Max(Math.Abs(lightness), Math.Max(Math.Abs(a), Math.Abs(b)));
        double difference = maximum == 0 ? 0 : maximum * Math.Sqrt(
            Math.Pow(lightness / maximum, 2) + Math.Pow(a / maximum, 2) + Math.Pow(b / maximum, 2));
        if (!double.IsFinite(difference))
            throw new NotSupportedException("The printer gamut difference exceeds finite numerical range.");
        var classification = difference < WarningThreshold - BoundaryTolerance ? PdfGamutClassification.Inside
            : difference > WarningThreshold + BoundaryTolerance ? PdfGamutClassification.Outside
            : PdfGamutClassification.Boundary;
        return new(difference, classification);
    }

    private static (double Lightness, double A, double B) Lab(ReadOnlySpan<double> xyz)
    {
        double x = Response(xyz[0] / .9642), y = Response(xyz[1]), z = Response(xyz[2] / .8249);
        return (116 * y - 16, 500 * (x - y), 200 * (y - z));

        static double Response(double ratio) => ratio > 216d / 24389
            ? Math.Cbrt(ratio) : (841d / 108) * ratio + 4d / 29;
    }
}
