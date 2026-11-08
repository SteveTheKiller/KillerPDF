using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Converts source colors through a printer profile to a display profile.</summary>
public sealed class PdfProofTransform
{
    private readonly PdfColorProfile _source;
    private readonly PdfColorProfile _printer;
    private readonly PdfColorProfile _display;
    private readonly PdfRenderingIntent _intent = PdfRenderingIntent.RelativeColorimetric;
    private readonly bool _blackPointCompensation = true;
    private readonly bool _simulateBlack;
    private readonly bool _simulatePaper;
    private readonly double[]? _printerBlack;
    private readonly double[]? _displayBlack;
    private readonly double[]? _paperScale;

    /// <summary>Creates a proof with paper and black simulation disabled.</summary>
    /// <exception cref="NotSupportedException">A profile lacks an actual table for a required intent and direction.</exception>
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display)
        : this(source, printer, display, validated: false) => RequireActualTables();

    /// <summary>Creates a proof with selected source-to-printer conversion settings.</summary>
    /// <exception cref="NotSupportedException">A profile lacks an actual table for a required intent and direction.</exception>
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display,
        PdfRenderingIntent intent, bool blackPointCompensation) : this(source, printer, display, validated: false)
    {
        _intent = intent;
        _blackPointCompensation = blackPointCompensation;
        RequireActualTables();
    }

    private PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display, bool validated)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(display);
        _source = source;
        _printer = printer;
        _display = display;
    }

    // A proof must never be built from a table chosen for a different intent than the one requested.
    private void RequireActualTables()
    {
        var viewing = _simulatePaper ? PdfRenderingIntent.AbsoluteColorimetric : PdfRenderingIntent.RelativeColorimetric;
        Require(_source, _intent, PdfColorTransformDirection.DeviceToConnection);
        Require(_printer, _intent, PdfColorTransformDirection.ConnectionToDevice);
        Require(_printer, viewing, PdfColorTransformDirection.DeviceToConnection);
        Require(_display, viewing, PdfColorTransformDirection.ConnectionToDevice);

        static void Require(PdfColorProfile profile, PdfRenderingIntent intent, PdfColorTransformDirection direction)
        {
            var selection = profile.GetTransformSelection(intent, direction);
            if (!selection.IsAvailable || selection.UsesIntentFallback)
                throw new NotSupportedException("Printer proof requires an actual " + intent + " "
                    + (direction == PdfColorTransformDirection.DeviceToConnection ? "forward" : "reverse")
                    + " transform without intent fallback.");
        }
    }

    /// <summary>Creates a proof using independent conversion and simulation selections.</summary>
    /// <exception cref="NotSupportedException">
    /// Requested paper simulation lacks reliable media-white or colorimetric data.
    /// Paper-only viewing also needs available profile black endpoints; CMYK uses
    /// perceptual reverse-table black estimation rather than an all-ink sample.
    /// </exception>
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display,
        PdfProofOptions options) : this(source, printer, display, validated: false)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.SimulatePaper && (!printer.HasExplicitValidMediaWhite || !display.HasExplicitValidMediaWhite))
            throw new NotSupportedException("Paper simulation requires explicit valid media-white data in the printer and display profiles.");
        if (options.SimulatePaper && !display.HasD50MediaWhite)
            throw new NotSupportedException("Paper simulation requires a display profile with unambiguous D50 media-white data.");
        if (options.SimulatePaper && (!printer.HasColorimetricForward || !display.HasColorimetricReverse))
            throw new NotSupportedException("Paper simulation requires actual colorimetric printer and display transforms.");
        double[]? paper = null;
        if (options.SimulatePaper)
        {
            paper = new double[3];
            printer.ReadPaperWhite(paper);
        }
        if (options.SimulatePaper && !options.SimulateBlack)
        {
            if (!display.HasColorimetricForward)
                throw new NotSupportedException("Paper-only viewing requires a colorimetric display black endpoint.");
            _printerBlack = new double[3];
            _displayBlack = new double[3];
            _paperScale = new double[3];
            printer.ReadPaperBlack(_printerBlack);
            display.ReadPaperBlack(_displayBlack);
            // A per-XYZ-channel affine viewing aid, not an ICC rendering intent.
            // Keep paper fixed while moving printer black to display black.
            const double minimumEndpointSeparation = 1e-9;
            for (int channel = 0; channel < 3; channel++)
            {
                double printerRange = paper![channel] - _printerBlack[channel];
                double displayRange = paper[channel] - _displayBlack[channel];
                if (!double.IsFinite(printerRange) || !double.IsFinite(displayRange)
                    || printerRange <= minimumEndpointSeparation || displayRange <= minimumEndpointSeparation)
                    throw new NotSupportedException("Paper-only viewing has unavailable or ill-conditioned black and paper endpoints.");
                _paperScale[channel] = displayRange / printerRange;
            }
        }
        _intent = options.RenderingIntent;
        _blackPointCompensation = options.BlackPointCompensation;
        _simulateBlack = options.SimulateBlack;
        _simulatePaper = options.SimulatePaper;
        RequireActualTables();
    }

    /// <summary>Converts source components into display components through printer components.</summary>
    public void Convert(ReadOnlySpan<double> source, Span<double> result)
    {
        Span<double> printer = stackalloc double[_printer.ComponentCount];
        _source.ConvertTo(_printer, source, printer,
            _intent, _blackPointCompensation);
        if (_paperScale is not null)
        {
            Span<double> xyz = stackalloc double[3];
            _printer.ToXyz(printer, xyz, PdfRenderingIntent.AbsoluteColorimetric);
            for (int channel = 0; channel < 3; channel++)
                xyz[channel] = _displayBlack![channel]
                    + (xyz[channel] - _printerBlack![channel]) * _paperScale[channel];
            _display.FromXyz(xyz, result, PdfRenderingIntent.AbsoluteColorimetric);
            return;
        }
        _printer.ConvertTo(_display, printer, result,
            _simulatePaper ? PdfRenderingIntent.AbsoluteColorimetric : PdfRenderingIntent.RelativeColorimetric,
            blackPointCompensation: !_simulateBlack);
    }
}
