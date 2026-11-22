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
    private const int CacheSize = 16384;
    private static readonly double[] LinearChannel = BuildLinearChannel();
    private readonly PdfProofPixelChain _output;
    private readonly Lazy<PdfProofPixelChain> _proof;

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
        _output = new PdfProofPixelChain(printer, new PdfProofOptions
        {
            RenderingIntent = intent, BlackPointCompensation = BlackPointCompensation, SimulateBlack = true
        });
        _proof = new(() => new PdfProofPixelChain(printer, new PdfProofOptions
        {
            RenderingIntent = intent, BlackPointCompensation = BlackPointCompensation
        }));
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
        Convert(pixels, _output, proof: false, cancellationToken);

    /// <summary>Replaces sRGB BGRA32 pixels in place with how the printer is predicted to reproduce them, as sRGB.</summary>
    /// <remarks>
    /// Converts to printer device values exactly as <see cref="ConvertSrgbBgra32"/> does, then back through
    /// the printer's relative colorimetric forward table with printer black mapped to display black.
    /// The result is for viewing only, never for output.
    /// </remarks>
    public void ProofSrgbBgra32(Span<byte> pixels, CancellationToken cancellationToken = default) =>
        Convert(pixels, _proof.Value, proof: true, cancellationToken);

    private static void Convert(Span<byte> pixels, PdfProofPixelChain chain, bool proof, CancellationToken cancellationToken)
    {
        if (pixels.Length % 4 != 0)
            throw new ArgumentException("BGRA32 pixels must be a whole number of four-byte pixels.", nameof(pixels));
        cancellationToken.ThrowIfCancellationRequested();
        // A page repeats few colors (paper, ink, fills), so converted colors are remembered by their
        // 24-bit value: a repeat costs a table lookup instead of a profile conversion.
        var keys = new int[CacheSize];
        var values = new int[CacheSize];
        keys.AsSpan().Fill(-1);
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[3];
        int lastKey = -1, lastValue = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            byte blue = pixels[offset], green = pixels[offset + 1], red = pixels[offset + 2];
            int key = red << 16 | green << 8 | blue;
            if (key != lastKey)
            {
                uint mix = (uint)key * 0x9E3779B1u;
                int slot = (int)(mix >> 18);
                if (keys[slot] != key)
                {
                    double r = LinearChannel[red], g = LinearChannel[green], b = LinearChannel[blue];
                    xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
                    xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
                    xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
                    double whiteness = Math.Min(red, Math.Min(green, blue)) / 255d;
                    int converted;
                    if (proof)
                    {
                        chain.Apply(xyz, whiteness, device);
                        var shown = PdfPageRenderer.PreciseConnectionToRgb(xyz[0], xyz[1], xyz[2]);
                        converted = Quantize(shown.Red) << 16 | Quantize(shown.Green) << 8 | Quantize(shown.Blue);
                    }
                    else
                    {
                        chain.ToDevice(xyz, whiteness, device);
                        converted = Quantize(device[0]) << 16 | Quantize(device[1]) << 8 | Quantize(device[2]);
                    }
                    keys[slot] = key;
                    values[slot] = converted;
                }
                lastKey = key;
                lastValue = values[slot];
            }
            pixels[offset] = (byte)lastValue;
            pixels[offset + 1] = (byte)(lastValue >> 8);
            pixels[offset + 2] = (byte)(lastValue >> 16);
        }
    }

    private static int Quantize(double value) =>
        (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1) * 255, MidpointRounding.AwayFromZero);

    private static double[] BuildLinearChannel()
    {
        var table = new double[256];
        for (int index = 0; index < 256; index++)
        {
            double value = index / 255d;
            table[index] = value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        }
        return table;
    }
}
