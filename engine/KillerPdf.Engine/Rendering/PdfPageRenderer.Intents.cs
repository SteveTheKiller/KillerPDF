using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static int ReadRenderingIntent(PdfName name) => name.ValueAsLatin1() switch
    {
        "Perceptual" => 0,
        "Saturation" => 2,
        "AbsoluteColorimetric" => 3,
        _ => 1
    };

    private int ReadRenderingIntent(PdfDictionary image, int inherited) =>
        image.TryGetValue(Name("Intent"), out PdfObject? value) && Resolve(value) is PdfName name
            ? ReadRenderingIntent(name) : inherited;

    private GraphicsState ApplyRenderingIntent(GraphicsState state, int intent,
        RasterSurface destination, HashSet<string> diagnostics)
    {
        if (state.RenderingIntent == intent) return state;
        state = state with { RenderingIntent = intent };
        if (state.FillColorSpace is { HasIccSource: true } fill)
        {
            ImageColorSpace space = Rebind(fill).ForDestination(destination);
            Color color = state.FillOperands is { } operands
                ? ReadDevicePaint(space, operands, out double[]? components)
                : space.InitialPaint(out components);
            state = state with { FillColorSpace = space, Fill = color, FillComponents = components };
        }
        if (state.StrokeColorSpace is { HasIccSource: true } stroke)
        {
            ImageColorSpace space = Rebind(stroke).ForDestination(destination);
            Color color = state.StrokeOperands is { } operands
                ? ReadDevicePaint(space, operands, out double[]? components)
                : space.InitialPaint(out components);
            state = state with { StrokeColorSpace = space, Stroke = color, StrokeComponents = components };
        }
        if (state.FillPatternBase is { HasIccSource: true } fillBase)
        {
            ImageColorSpace space = Rebind(fillBase);
            PatternPaint? paint = state.FillPattern;
            if (paint?.BaseColor is not null && state.FillOperands is { } operands)
                paint = paint with { BaseColor = ReadDevicePaint(space, operands, out _) };
            state = state with { FillPatternBase = space, FillPattern = paint };
        }
        if (state.StrokePatternBase is { HasIccSource: true } strokeBase)
        {
            ImageColorSpace space = Rebind(strokeBase);
            PatternPaint? paint = state.StrokePattern;
            if (paint?.BaseColor is not null && state.StrokeOperands is { } operands)
                paint = paint with { BaseColor = ReadDevicePaint(space, operands, out _) };
            state = state with { StrokePatternBase = space, StrokePattern = paint };
        }
        return state;

        ImageColorSpace Rebind(ImageColorSpace space) => space.Definition is { } definition
            && space.SourceResources is { } resources
                ? ReadColorSpace(definition, resources, 0, diagnostics: diagnostics, intent: intent) : space;
    }
}
