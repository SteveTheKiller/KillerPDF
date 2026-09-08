using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static GraphicsState RebindNamedColors(GraphicsState state, RasterSurface destination)
    {
        if (state.FillColorSpace is { HasProcessColorants: true } fill)
        {
            ImageColorSpace space = fill.ForDestination(destination);
            if (!ReferenceEquals(space, fill)) state = state with
            {
                FillColorSpace = space,
                Fill = state.FillComponents is { } values ? space.Convert(values) : state.Fill
            };
        }
        if (state.StrokeColorSpace is { HasProcessColorants: true } stroke)
        {
            ImageColorSpace space = stroke.ForDestination(destination);
            if (!ReferenceEquals(space, stroke)) state = state with
            {
                StrokeColorSpace = space,
                Stroke = state.StrokeComponents is { } values ? space.Convert(values) : state.Stroke
            };
        }
        return state;
    }

    private GraphicsState ApplyOverprintSettings(GraphicsState state, PdfDictionary dictionary)
    {
        if (dictionary.TryGetValue(Name("OP"), out PdfObject? strokeValue)
            && Resolve(strokeValue) is PdfBoolean stroke)
            state = state with { StrokeOverprint = stroke.Value, FillOverprint = stroke.Value };
        if (dictionary.TryGetValue(Name("op"), out PdfObject? fillValue)
            && Resolve(fillValue) is PdfBoolean fill)
            state = state with { FillOverprint = fill.Value };
        if (dictionary.TryGetValue(Name("OPM"), out PdfObject? modeValue)
            && Resolve(modeValue) is PdfInteger { Value: 0 or 1 } mode)
            state = state with { OverprintMode = (int)mode.Value };
        return state;
    }

    private static int ProcessChannel(string name) => name switch
    {
        "Cyan" => 0, "Magenta" => 1, "Yellow" => 2, "Black" => 3, "None" => -1, _ => -2
    };

    private static Color ProcessColor(int[] channels, double first, double second, double third, double fourth)
    {
        ReadOnlySpan<double> source = stackalloc double[4] { first, second, third, fourth };
        return ProcessColor(channels, source);
    }

    private static Color ProcessColor(int[] channels, ReadOnlySpan<double> source)
    {
        Span<double> ink = stackalloc double[4];
        ink.Clear();
        for (int index = 0; index < channels.Length; index++)
            if (channels[index] >= 0) ink[channels[index]] = source[index];
        return Color.Cmyk(ink[0], ink[1], ink[2], ink[3]);
    }

    private static Color OverprintColor(in Color color, ImageColorSpace? space, bool enabled, int mode) =>
        space?.DoesNotPaint == true ? Color.NonPainting
        : enabled && space?.NativeProcessMask is byte mask
            ? color with { OverprintComponents = (byte)((~mask & 15) | 16) }
        : enabled && mode == 1 && space is { Components: 4, IsIccBased: false, Palette: null,
            Converter: null, MultiConverter: null }
            ? color with { OverprintComponents = (byte)(color.OverprintComponents | 16) } : color;
}
