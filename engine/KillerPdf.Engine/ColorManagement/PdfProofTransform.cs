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
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(printer);
        ArgumentNullException.ThrowIfNull(display);
        _source = source;
        _printer = printer;
        _display = display;
    }

    /// <summary>Creates a proof with selected source-to-printer conversion settings.</summary>
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display,
        PdfRenderingIntent intent, bool blackPointCompensation) : this(source, printer, display)
    {
        _intent = intent;
        _blackPointCompensation = blackPointCompensation;
    }

    /// <summary>Creates a proof using independent conversion and simulation selections.</summary>
    /// <exception cref="NotSupportedException">
    /// Requested paper simulation lacks reliable media-white or colorimetric data.
    /// Paper-only viewing also needs available profile black endpoints; CMYK uses
    /// perceptual reverse-table black estimation rather than an all-ink sample.
    /// </exception>
    public PdfProofTransform(PdfColorProfile source, PdfColorProfile printer, PdfColorProfile display,
        PdfProofOptions options) : this(source, printer, display)
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
