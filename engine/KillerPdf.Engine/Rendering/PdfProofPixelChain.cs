using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

// The printer proof of one frame pixel: D50 connection values from the source through the printer's
// device values to the D50 values the display shows. PDF and native (EPUB, DjVu) frames share it.
//
// Relative colorimetric maps media white to the reference white, so an unprinted area must reach the
// printer as no ink and come back as display white, or, with paper simulation, as the paper's own
// absolute color. Printer tables only approximate those endpoints: Windows' RSWOP.icm sends PCS white
// to 0.6% cyan, 1.7% magenta and 2.0% yellow, and reads no ink back as Lab (100, 1, 1), which moved
// the proofed paper 8.4 levels toward yellow. As LittleCMS does by default for every intent but
// absolute colorimetric, the chain corrects the white endpoint of each table it uses: source white
// takes the printer's no-ink value, and the relative viewing step reads no ink as reference white.
// Absolute conversion and paper simulation retain the profile's own absolute endpoints.
// Each correction fades to nothing within one 1/32 step of white, the reach of the white node in a
// 33-point device-link grid, so colors away from white keep the profile's own transform. Only table
// error is corrected: an endpoint that lands a just-noticeable difference (2.3 CIELAB units) or more
// from its target is the profile's own characterization, such as a printer that cannot reach a light
// value at all, and stays. RSWOP.icm's endpoints land 2.1 (source white) and 1.4 (no ink) away.
internal sealed class PdfProofPixelChain
{
    private const double WhiteReach = 32;
    private const double NoticeableDifference = 2.3;
    private static readonly double[] ReferenceWhite = [0.9642, 1, 0.8249];
    private readonly PdfColorProfile _printer;
    private readonly PdfProofOptions _options;
    private readonly PdfBlackPointCompensation _sourceCompensation;
    private readonly PdfBlackPointCompensation _viewingCompensation;
    private readonly double[] _paperBlack = new double[3];
    private readonly double[] _paperScale = new double[3];
    private readonly double[] _deviceWhite;
    private readonly double[]? _sourceWhiteError;
    private readonly double[]? _viewingWhiteError;
    private readonly PdfRenderingIntent _viewingIntent;

    internal PdfProofPixelChain(PdfColorProfile printer, PdfProofOptions options)
    {
        _printer = printer;
        _options = options;
        PdfProofGate.PreparePaper(printer, options, _paperBlack, _paperScale);
        _sourceCompensation = options.BlackPointCompensation
            && options.RenderingIntent != PdfRenderingIntent.AbsoluteColorimetric
            ? printer.ReferenceBlackToDeviceCompensation(options.RenderingIntent) : new(1);
        _viewingCompensation = options.SimulateBlack || options.SimulatePaper ? new(1)
            : printer.DeviceToReferenceBlackCompensation(PdfRenderingIntent.RelativeColorimetric);
        _viewingIntent = options.SimulatePaper
            ? PdfRenderingIntent.AbsoluteColorimetric : PdfRenderingIntent.RelativeColorimetric;
        // No ink is device zero for CMYK and full value for gray and RGB output.
        _deviceWhite = new double[printer.ComponentCount];
        if (printer.ComponentCount != 4) _deviceWhite.AsSpan().Fill(1);
        Span<double> device = stackalloc double[printer.ComponentCount];
        Span<double> xyz = stackalloc double[3];
        Span<double> noInk = stackalloc double[3];
        printer.ToXyz(_deviceWhite, noInk, PdfRenderingIntent.RelativeColorimetric);
        if (!options.SimulatePaper && options.RenderingIntent != PdfRenderingIntent.AbsoluteColorimetric)
        {
            // Compensation keeps reference white fixed, so source white reaches the table unchanged.
            // The error is measured as the color the printer would put down instead of no ink.
            printer.FromXyz(ReferenceWhite, device, options.RenderingIntent);
            printer.ToXyz(device, xyz, PdfRenderingIntent.RelativeColorimetric);
            if (LabDistance(xyz, noInk) < NoticeableDifference)
                _sourceWhiteError = Difference(device, _deviceWhite);
        }
        // Only the relative viewing hop gets a white correction.
        xyz = noInk;
        if (!options.SimulatePaper && options.RenderingIntent != PdfRenderingIntent.AbsoluteColorimetric
            && LabDistance(xyz, ReferenceWhite) < NoticeableDifference)
            _viewingWhiteError = Difference(ReferenceWhite, xyz);

        static double[]? Difference(ReadOnlySpan<double> value, ReadOnlySpan<double> target)
        {
            var error = new double[value.Length];
            bool any = false;
            for (int index = 0; index < error.Length; index++)
            {
                error[index] = value[index] - target[index];
                if (!double.IsFinite(error[index]))
                    throw new NotSupportedException("Printer proof white endpoints are not finite.");
                any |= error[index] != 0;
            }
            return any ? error : null;
        }

        static double LabDistance(ReadOnlySpan<double> value, ReadOnlySpan<double> target)
        {
            double x = F(value[0] / ReferenceWhite[0]), y = F(value[1]), z = F(value[2] / ReferenceWhite[2]);
            double targetX = F(target[0] / ReferenceWhite[0]), targetY = F(target[1]), targetZ = F(target[2] / ReferenceWhite[2]);
            double lightness = 116 * (y - targetY), a = 500 * (x - y - targetX + targetY), b = 200 * (y - z - targetY + targetZ);
            return Math.Sqrt(lightness * lightness + a * a + b * b);
        }

        static double F(double value) => value > 216d / 24389 ? Math.Cbrt(value) : value * 841 / 108 + 4d / 29;
    }

    internal int DeviceComponents => _printer.ComponentCount;

    // Converts source D50 values in place to the D50 values the display shows. sourceWhiteness is
    // the smallest encoded sRGB source channel, 1 for white.
    internal void Apply(Span<double> xyz, double sourceWhiteness, Span<double> device)
    {
        ToDevice(xyz, sourceWhiteness, device);
        _printer.ToXyz(device, xyz, _viewingIntent);
        CorrectConnectionWhite(xyz, device);
        if (_options.SimulatePaper && !_options.SimulateBlack)
            for (int channel = 0; channel < 3; channel++)
                xyz[channel] = (xyz[channel] - _paperBlack[channel]) * _paperScale[channel];
        _viewingCompensation.Apply(xyz);
    }

    // The source half of the chain: source D50 values (changed in place by black-point compensation)
    // to the printer's device values, the values a color-managed print job sends to the driver.
    internal void ToDevice(Span<double> xyz, double sourceWhiteness, Span<double> device)
    {
        _sourceCompensation.Apply(xyz);
        _printer.FromXyz(xyz, device, _options.RenderingIntent);
        CorrectDeviceWhite(device, sourceWhiteness);
    }

    internal void CorrectDeviceWhite(Span<double> device, double sourceWhiteness)
    {
        if (_sourceWhiteError is not null)
        {
            double weight = Math.Clamp(1 - WhiteReach * (1 - sourceWhiteness), 0, 1);
            if (weight > 0)
                for (int component = 0; component < device.Length; component++)
                    device[component] = Math.Clamp(device[component] - weight * _sourceWhiteError[component], 0, 1);
        }
    }

    internal void CorrectConnectionWhite(Span<double> xyz, ReadOnlySpan<double> device)
    {
        if (_viewingWhiteError is not null)
        {
            double distance = 0;
            for (int component = 0; component < device.Length; component++)
                distance = Math.Max(distance, Math.Abs(device[component] - _deviceWhite[component]));
            double weight = Math.Clamp(1 - WhiteReach * distance, 0, 1);
            if (weight > 0)
                for (int channel = 0; channel < 3; channel++) xyz[channel] += weight * _viewingWhiteError[channel];
        }
    }
}
