using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Converts source colors through a printer profile to a display profile.</summary>
public sealed class PdfProofTransform
{
    private readonly PdfColorProfile _source;
    private readonly PdfColorProfile _printer;
    private readonly PdfColorProfile _display;

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

    /// <summary>Converts source components into display components through printer components.</summary>
    public void Convert(ReadOnlySpan<double> source, Span<double> result)
    {
        Span<double> printer = stackalloc double[_printer.ComponentCount];
        _source.ConvertTo(_printer, source, printer,
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);
        _printer.ConvertTo(_display, printer, result,
            PdfRenderingIntent.RelativeColorimetric, blackPointCompensation: true);
    }
}
