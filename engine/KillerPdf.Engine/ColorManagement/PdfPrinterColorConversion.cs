using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Converts sRGB page pixels to an RGB printer profile's device values for color-managed printing.</summary>
/// <remarks>
/// The source half of a printer proof: sRGB through D50 XYZ, optional black-point compensation and the
/// printer profile's reverse table for the selected intent, with the proof's white endpoint correction
/// so unprinted paper reaches the printer as no ink. The output is what a print job sends to a driver
/// whose own color management is off. <see cref="ProofSrgbBgra32"/> continues the same values back to
/// sRGB for an on-screen preview of the print. Instances hold no per-call state and can be shared across threads.
/// </remarks>
public sealed class PdfPrinterColorConversion
{
    private const int GridPoints = 33;
    private static readonly int[] NodeBelow = BuildNodeBelow();
    private static readonly float[] NodeFraction = BuildNodeFraction();
    private readonly Lazy<float[]> _outputGrid;
    private readonly Lazy<float[]> _proofGrid;

    /// <summary>Creates a conversion to an RGB output profile with the selected intent.</summary>
    /// <exception cref="NotSupportedException">
    /// The profile is not an RGB output profile, or it lacks an actual table for the intent.
    /// </exception>
    public PdfPrinterColorConversion(PdfColorProfile printer, PdfRenderingIntent intent, bool blackPointCompensation)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (printer.ProfileClassSignature != "prtr")
            throw new NotSupportedException("Color-managed printing requires an output (printer) profile.");
        if (printer.DeviceColorSpaceSignature != "RGB ")
            throw new NotSupportedException("Color-managed printing requires an RGB output profile.");
        if (!Supports(printer, intent))
            throw new NotSupportedException("The printer profile has no actual " + intent + " table for color-managed printing.");
        Printer = printer;
        RenderingIntent = intent;
        BlackPointCompensation = blackPointCompensation && intent != PdfRenderingIntent.AbsoluteColorimetric;
        var output = new PdfProofPixelChain(printer, new PdfProofOptions
        {
            RenderingIntent = intent, BlackPointCompensation = BlackPointCompensation, SimulateBlack = true
        });
        _outputGrid = new(() => BuildGrid(output, proof: false));
        _proofGrid = new(() => BuildGrid(new PdfProofPixelChain(printer, new PdfProofOptions
        {
            RenderingIntent = intent, BlackPointCompensation = BlackPointCompensation
        }), proof: true));
    }

    /// <summary>Gets the printer profile.</summary>
    public PdfColorProfile Printer { get; }
    /// <summary>Gets the source-to-printer rendering intent.</summary>
    public PdfRenderingIntent RenderingIntent { get; }
    /// <summary>Gets whether source black is mapped to printer black. Always false for absolute colorimetric.</summary>
    public bool BlackPointCompensation { get; }

    /// <summary>Gets whether a profile can drive color-managed printing with an intent.</summary>
    /// <remarks>
    /// True for an RGB output profile with an actual reverse table for the intent (no fallback to another
    /// intent's table) and an actual relative colorimetric forward table for the preview. Absolute
    /// colorimetric also needs a valid media-white point.
    /// </remarks>
    public static bool Supports(PdfColorProfile printer, PdfRenderingIntent intent)
    {
        ArgumentNullException.ThrowIfNull(printer);
        if (printer.ProfileClassSignature != "prtr" || printer.DeviceColorSpaceSignature != "RGB ") return false;
        if (intent is not (PdfRenderingIntent.Perceptual or PdfRenderingIntent.RelativeColorimetric
            or PdfRenderingIntent.Saturation or PdfRenderingIntent.AbsoluteColorimetric)) return false;
        if (intent == PdfRenderingIntent.AbsoluteColorimetric && !printer.HasExplicitValidMediaWhite) return false;
        return Actual(printer, intent, PdfColorTransformDirection.ConnectionToDevice)
            && Actual(printer, PdfRenderingIntent.RelativeColorimetric, PdfColorTransformDirection.DeviceToConnection);

        static bool Actual(PdfColorProfile profile, PdfRenderingIntent intent, PdfColorTransformDirection direction) =>
            profile.GetTransformSelection(intent, direction) is { IsAvailable: true, UsesIntentFallback: false };
    }

    /// <summary>Replaces sRGB BGRA32 pixels in place with the printer's device RGB values.</summary>
    /// <remarks>Alpha is kept. Pixels are straight-alpha sRGB, as page renders produce them.</remarks>
    public void ConvertSrgbBgra32(Span<byte> pixels, CancellationToken cancellationToken = default) =>
        Convert(pixels, proof: false, cancellationToken);

    /// <summary>Replaces sRGB BGRA32 pixels in place with how the printer is predicted to reproduce them, as sRGB.</summary>
    /// <remarks>
    /// Converts to printer device values exactly as <see cref="ConvertSrgbBgra32"/> does, then back through
    /// the printer's relative colorimetric forward table with printer black mapped to display black.
    /// The result is for viewing only, never for output.
    /// </remarks>
    public void ProofSrgbBgra32(Span<byte> pixels, CancellationToken cancellationToken = default) =>
        Convert(pixels, proof: true, cancellationToken);

    private void Convert(Span<byte> pixels, bool proof, CancellationToken cancellationToken)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("BGRA32 pixels must be a whole number of four-byte pixels.", nameof(pixels));
        cancellationToken.ThrowIfCancellationRequested();
        float[] grid = (proof ? _proofGrid : _outputGrid).Value;
        int lastKey = -1, lastValue = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 262143) == 0) cancellationToken.ThrowIfCancellationRequested();
            byte blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
            int key = red << 16 | green << 8 | blue;
            if (key != lastKey)
            {
                lastKey = key;
                lastValue = Interpolate(grid, red, green, blue);
            }
            pixels[offset] = (byte)lastValue;
            pixels[offset + 1] = (byte)(lastValue >> 8);
            pixels[offset + 2] = (byte)(lastValue >> 16);
        }
    }

    // The whole conversion sampled once on a 33-point grid in encoded sRGB, the default precision of
    // common color management modules. Each pixel then costs a tetrahedral interpolation instead of a
    // profile evaluation, so a photo page with millions of distinct colors converts in a fraction of a second.
    // Grid nodes include 0 and 255 on every channel, so paper white and full black are exact.
    private float[] BuildGrid(PdfProofPixelChain chain, bool proof)
    {
        var grid = new float[GridPoints * GridPoints * GridPoints * 3];
        Parallel.For(0, GridPoints, red =>
        {
            Span<double> xyz = stackalloc double[3];
            Span<double> device = stackalloc double[3];
            for (int green = 0; green < GridPoints; green++)
                for (int blue = 0; blue < GridPoints; blue++)
                {
                    double r = Linear(red), g = Linear(green), b = Linear(blue);
                    xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
                    xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
                    xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
                    double whiteness = Math.Min(red, Math.Min(green, blue)) / (double)(GridPoints - 1);
                    int at = ((red * GridPoints + green) * GridPoints + blue) * 3;
                    if (proof)
                    {
                        chain.Apply(xyz, whiteness, device);
                        var shown = PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
                        grid[at] = Unit(shown.Red); grid[at + 1] = Unit(shown.Green); grid[at + 2] = Unit(shown.Blue);
                    }
                    else
                    {
                        chain.ToDevice(xyz, whiteness, device);
                        grid[at] = Unit(device[0]); grid[at + 1] = Unit(device[1]); grid[at + 2] = Unit(device[2]);
                    }
                }
        });
        return grid;

        static double Linear(int node)
        {
            double value = node / (double)(GridPoints - 1);
            return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
    }

    private static int Interpolate(float[] grid, byte red, byte green, byte blue)
    {
        int r0 = NodeBelow[red], g0 = NodeBelow[green], b0 = NodeBelow[blue];
        float fr = NodeFraction[red], fg = NodeFraction[green], fb = NodeFraction[blue];
        const int StrideR = GridPoints * GridPoints * 3, StrideG = GridPoints * 3, StrideB = 3;
        int c000 = (r0 * GridPoints + g0) * StrideG + b0 * StrideB;
        int dr = fr > 0 ? StrideR : 0, dg = fg > 0 ? StrideG : 0, db = fb > 0 ? StrideB : 0;
        int value = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            int c = c000 + channel;
            float p000 = grid[c], p111 = grid[c + dr + dg + db], result;
            // Tetrahedral interpolation: the cube splits into six tetrahedra along its gray diagonal.
            if (fr >= fg)
            {
                if (fg >= fb)
                    result = p000 + fr * (grid[c + dr] - p000) + fg * (grid[c + dr + dg] - grid[c + dr]) + fb * (p111 - grid[c + dr + dg]);
                else if (fr >= fb)
                    result = p000 + fr * (grid[c + dr] - p000) + fb * (grid[c + dr + db] - grid[c + dr]) + fg * (p111 - grid[c + dr + db]);
                else
                    result = p000 + fb * (grid[c + db] - p000) + fr * (grid[c + dr + db] - grid[c + db]) + fg * (p111 - grid[c + dr + db]);
            }
            else
            {
                if (fb >= fg)
                    result = p000 + fb * (grid[c + db] - p000) + fg * (grid[c + dg + db] - grid[c + db]) + fr * (p111 - grid[c + dg + db]);
                else if (fb >= fr)
                    result = p000 + fg * (grid[c + dg] - p000) + fb * (grid[c + dg + db] - grid[c + dg]) + fr * (p111 - grid[c + dg + db]);
                else
                    result = p000 + fg * (grid[c + dg] - p000) + fr * (grid[c + dr + dg] - grid[c + dg]) + fb * (p111 - grid[c + dr + dg]);
            }
            int quantized = (int)(Math.Clamp(result, 0f, 1f) * 255f + .5f);
            value |= quantized << (16 - channel * 8);
        }
        return value;
    }

    private static float Unit(double value) => (float)Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1);

    private static int[] BuildNodeBelow()
    {
        var table = new int[256];
        for (int index = 0; index < 256; index++)
            table[index] = Math.Min(GridPoints - 2, index * (GridPoints - 1) / 255);
        return table;
    }

    private static float[] BuildNodeFraction()
    {
        var table = new float[256];
        for (int index = 0; index < 256; index++)
        {
            double position = index * (GridPoints - 1) / 255d;
            table[index] = (float)(position - Math.Min(GridPoints - 2, (int)position));
        }
        return table;
    }
}
