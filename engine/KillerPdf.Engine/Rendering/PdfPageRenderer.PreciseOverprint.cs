using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private sealed record PreciseProcessPaint(double Cyan, double Magenta, double Yellow, double Black,
        byte ExactZeros, byte Preserve = 0);

    private readonly record struct PreciseProcessState(bool FillOverprint = false, bool StrokeOverprint = false,
        bool FillDeviceCmyk = false, bool StrokeDeviceCmyk = false,
        bool SupportedNormal = true, bool NormalBlend = true);

    // Qualification reads the whole page before allocating or clipping a backdrop. Until an
    // inverse transform is supported, RGB paint cannot supply native process plates.
    private bool RequiresPreciseProcessPlates(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken)
    {
        var diagnostics = new HashSet<string>();
        PdfDictionary resources = _pageResources[pageIndex];
        var state = new PreciseProcessState(SupportedNormal: true, NormalBlend: true);
        var saved = new Stack<PreciseProcessState>();
        var visibility = new Stack<bool>();
        bool visible = true;
        var hiddenGroups = options.ForPrinting ? _printHiddenOptionalContentGroups : _hiddenOptionalContentGroups;
        bool actualOverprint = false, unsupported = false, rectangle = false;
        foreach (var instruction in ReadInstructions(pageIndex, diagnostics, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var operands = instruction.Operands;
            if (instruction.Operator is "BMC" or "BDC")
            {
                visibility.Push(visible);
                if (instruction.Operator == "BDC")
                    visible &= OptionalContentVisible(operands, resources, hiddenGroups);
                continue;
            }
            if (instruction.Operator == "EMC")
            {
                if (visibility.TryPop(out bool previousVisibility)) visible = previousVisibility;
                continue;
            }
            if (!visible) continue;
            switch (instruction.Operator)
            {
                case "q": saved.Push(state); break;
                case "Q": if (saved.TryPop(out var previous)) state = previous; else unsupported = true; break;
                case "k":
                    state = state with { FillDeviceCmyk = operands.Count == 4
                        && operands.All(value => Number(Resolve(value)) is >= 0 and <= 1) };
                    break;
                case "g": case "rg": case "cs": case "sc": case "scn":
                    state = state with { FillDeviceCmyk = false }; break;
                case "K":
                    state = state with { StrokeDeviceCmyk = operands.Count == 4
                        && operands.All(value => Number(Resolve(value)) is >= 0 and <= 1) };
                    break;
                case "G": case "RG": case "CS": case "SC": case "SCN":
                    state = state with { StrokeDeviceCmyk = false }; break;
                case "gs":
                    if (operands.Count != 1 || Resolve(operands[0]) is not PdfName key
                        || !resources.TryGetValue(Name("ExtGState"), out var statesValue)
                        || Resolve(statesValue) is not PdfDictionary states
                        || !states.TryGetValue(key, out var settingsValue)
                        || Resolve(settingsValue) is not PdfDictionary settings)
                    { unsupported = true; break; }
                    bool fill = state.FillOverprint, stroke = state.StrokeOverprint;
                    if (settings.TryGetValue(Name("OP"), out var op) && Resolve(op) is PdfBoolean strokeOp)
                        fill = stroke = strokeOp.Value;
                    if (settings.TryGetValue(Name("op"), out var fillOp) && Resolve(fillOp) is PdfBoolean fillValue)
                        fill = fillValue.Value;
                    bool supported = state.SupportedNormal;
                    bool normalBlend = state.NormalBlend;
                    if (settings.TryGetValue(Name("ca"), out var alpha)) supported &= Number(Resolve(alpha)) is >= 0 and <= 1;
                    if (settings.TryGetValue(Name("BM"), out var blend))
                        normalBlend = Resolve(blend) is PdfName blendName && blendName.ValueAsLatin1() is "Normal" or "Compatible";
                    if (settings.TryGetValue(Name("SMask"), out var mask))
                        supported &= Resolve(mask) is PdfName maskName && maskName.ValueAsLatin1() == "None";
                    supported &= PreciseGraphicsStateSupported(settings, allowProcessOverprint: true);
                    state = state with { FillOverprint = fill, StrokeOverprint = stroke,
                        SupportedNormal = supported, NormalBlend = normalBlend };
                    break;
                case "re": rectangle = true; break;
                case "m": case "l": case "c": case "v": case "y": rectangle = false; break;
                case "W": case "W*": unsupported |= !rectangle; break;
                case "f": case "F": case "f*":
                    actualOverprint |= state.FillOverprint;
                    unsupported |= !state.FillDeviceCmyk || !state.SupportedNormal || !state.NormalBlend;
                    rectangle = false;
                    break;
                case "S": case "s":
                    actualOverprint |= state.StrokeOverprint;
                    unsupported |= !state.StrokeDeviceCmyk || !state.SupportedNormal || !state.NormalBlend;
                    rectangle = false;
                    break;
                case "B": case "B*": case "b": case "b*":
                    actualOverprint |= state.FillOverprint || state.StrokeOverprint;
                    unsupported |= !state.FillDeviceCmyk || !state.StrokeDeviceCmyk
                        || !state.SupportedNormal || !state.NormalBlend;
                    rectangle = false;
                    break;
                case "Tj": case "TJ": case "'": case "\"": case "Do": case "sh":
                    actualOverprint |= state.FillOverprint || state.StrokeOverprint;
                    unsupported = true; break;
                case "n": rectangle = false; break;
            }
        }
        if (!actualOverprint) return false;
        var page = _tree.Pages[pageIndex].Dictionary;
        ImageColorSpace source = ReadColorSpace(Name("DeviceCMYK"), resources, 0);
        if (unsupported || page.ContainsKey(Name("Group")) || page.ContainsKey(Name("Annots"))
            || source.IsDefault || source.HasIccSource || source.Profile is not null
            || OutputProfile(null) is not null)
            throw new NotSupportedException("This high-precision graphics state requires an unprofiled page of Normal DeviceCMYK path paints without groups or other paint sources.");
        return true;
    }

    private static void PaintPreciseProcessPixel(RasterSurface surface, int offset,
        PreciseProcessPaint source, double opacity)
    {
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        if (sourceAlpha == 0) return;
        float[] rgb = surface.PreciseRgb!, plates = surface.PreciseProcess!;
        double backdropAlpha = rgb[offset + 3];
        double alpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        Span<double> ink = stackalloc double[4];
        ReadOnlySpan<double> native = [source.Cyan, source.Magenta, source.Yellow, source.Black];
        for (int component = 0; component < 4; component++)
        {
            double backdrop = plates[offset + component];
            double blend = (source.Preserve & (1 << component)) != 0 ? backdrop : native[component];
            ink[component] = ((1 - sourceAlpha) * backdropAlpha * backdrop
                + (1 - backdropAlpha) * sourceAlpha * native[component]
                + sourceAlpha * backdropAlpha * blend) / alpha;
            plates[offset + component] = (float)ink[component];
        }
        Color appearance = surface.PreciseCmyk!.Convert(ink[0], ink[1], ink[2], ink[3]);
        var color = appearance.Connection is { } xyz ? PreciseConnectionToRgb(xyz.X, xyz.Y, xyz.Z)
            : throw new NotSupportedException("The process source profile did not retain its connection values.");
        rgb[offset] = (float)color.Red;
        rgb[offset + 1] = (float)color.Green;
        rgb[offset + 2] = (float)color.Blue;
        rgb[offset + 3] = (float)alpha;
    }
}
