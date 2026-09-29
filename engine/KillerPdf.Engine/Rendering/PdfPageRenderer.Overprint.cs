using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private sealed class OutputOverprintRequiredException(PdfColorTransform profile) : Exception
    {
        internal PdfColorTransform Profile { get; } = profile;
    }

    private bool HasPotentialOutputOverprint(PdfDictionary pageResources)
    {
        HashSet<PdfDictionary>? visited = null;
        try
        {
            if (!_document.Trailer.TryGetValue(Name("Root"), out PdfObject? root)
                || Resolve(root) is not PdfDictionary catalog
                || !catalog.ContainsKey(Name("OutputIntents"))) return false;
            visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
            return Scan(pageResources, 0);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A failed hint leaves the existing render-and-retry path in charge.
            return false;
        }

        bool Scan(PdfDictionary resources, int depth)
        {
            if (depth > 32 || !visited!.Add(resources)) return false;
            if (resources.TryGetValue(Name("ExtGState"), out PdfObject? statesValue)
                && Resolve(statesValue) is PdfDictionary states)
                foreach (PdfObject value in states.Values)
                {
                    if (Resolve(value) is not PdfDictionary state) continue;
                    if (state.TryGetValue(Name("OP"), out PdfObject? stroke)
                        && Resolve(stroke) is PdfBoolean { Value: true }
                        || state.TryGetValue(Name("op"), out PdfObject? fill)
                        && Resolve(fill) is PdfBoolean { Value: true })
                        return true;
                }
            if (!resources.TryGetValue(Name("XObject"), out PdfObject? objectsValue)
                || Resolve(objectsValue) is not PdfDictionary objects) return false;
            foreach (PdfObject value in objects.Values)
            {
                if (Resolve(value) is not PdfStream stream
                    || !stream.Dictionary.TryGetValue(Name("Subtype"), out PdfObject? subtype)
                    || Resolve(subtype) is not PdfName name || name.ValueAsLatin1() != "Form"
                    || !stream.Dictionary.TryGetValue(Name("Resources"), out PdfObject? child)
                    || Resolve(child) is not PdfDictionary childResources) continue;
                if (Scan(childResources, depth + 1)) return true;
            }
            return false;
        }
    }

    private GraphicsState RebindNamedColors(GraphicsState state, RasterSurface destination)
    {
        if (state.FillColorSpace is { } fill && (fill.HasProcessColorants || fill.HasIccSource))
        {
            ImageColorSpace space = fill.ForDestination(destination);
            if (!ReferenceEquals(space, fill)) state = state with
            {
                FillColorSpace = space,
                Fill = state.FillOperands is { } operands ? ReadDevicePaint(space, operands, out _)
                    : state.FillComponents is { } values ? space.Convert(values) : space.InitialPaint(out _)
            };
        }
        if (state.StrokeColorSpace is { } stroke && (stroke.HasProcessColorants || stroke.HasIccSource))
        {
            ImageColorSpace space = stroke.ForDestination(destination);
            if (!ReferenceEquals(space, stroke)) state = state with
            {
                StrokeColorSpace = space,
                Stroke = state.StrokeOperands is { } operands ? ReadDevicePaint(space, operands, out _)
                    : state.StrokeComponents is { } values ? space.Convert(values) : space.InitialPaint(out _)
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
        ReadOnlySpan<double> source = [first, second, third, fourth];
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
        : enabled && space?.ContainsSpotColorants == true
            ? color with { OverprintComponents = (byte)(color.OverprintComponents | 16 | 64) }
        : enabled && mode == 1 && space is { Components: 4, IsIccBased: false, Palette: null,
            Converter: null, MultiConverter: null }
            ? color with { OverprintComponents = (byte)(color.OverprintComponents | 16) } : color;
}
