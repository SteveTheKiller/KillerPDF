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

    /// <summary>Converts source components into display components through printer components.</summary>
    public void Convert(ReadOnlySpan<double> source, Span<double> result)
    {
        Span<double> printer = stackalloc double[_printer.ComponentCount];
        _source.ConvertTo(_printer, source, printer,
            _intent, _blackPointCompensation);
        _printer.ConvertTo(_display, printer, result,
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);
    }
}
