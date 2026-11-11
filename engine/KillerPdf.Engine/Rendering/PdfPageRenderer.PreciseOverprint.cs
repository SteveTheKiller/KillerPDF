using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private sealed record PreciseProcessPaint(double Cyan, double Magenta, double Yellow, double Black,
        byte ExactZeros, PreciseProcessPlan Binding, byte Preserve = 0);

    private enum PreciseProcessSourceKind { Working, Catalog, Default }

    // One immutable source definition owns the interpretation of every accumulated plate.
    private sealed record PreciseProcessPlan(PreciseProcessSourceKind Kind, PdfIccProfileTransform? Transform,
        string ContentSha256, int Intent)
    {
        internal bool OpmOneEligible => Kind != PreciseProcessSourceKind.Default;

        internal bool Matches(ImageColorSpace source)
        {
            if (source.Components != 4 || source.ComponentRange is not null || source.Palette is not null
                || source.MultiConverter is not null || source.HasSpotColorants || source.BlackPointCompensationEnabled)
                return false;
            if (Kind == PreciseProcessSourceKind.Working)
                return source.IsDeviceCmyk && source.Profile is null && !source.HasIccSource && !source.IsDefault;
            return source.Intent == Intent && source.Profile is PdfIccProfileTransform profile
                && Transform!.HasSameIdentity(profile) && source.HasIccSource
                && (Kind == PreciseProcessSourceKind.Default ? source.IsDefault && source.IsIccBased
                    : source.IsDeviceCmyk && !source.IsDefault);
        }

        internal bool SameBinding(PreciseProcessPlan other) => Kind == other.Kind && Intent == other.Intent
            && ContentSha256 == other.ContentSha256;

        internal Color Convert(PreciseCmykWorkingSpace working, ReadOnlySpan<double> ink)
        {
            if (Transform is null) return working.Convert(ink[0], ink[1], ink[2], ink[3]);
            Span<double> xyz = stackalloc double[3];
            Transform.ToXyz(ink, xyz);
            return IccXyzToDisplay(xyz[0], xyz[1], xyz[2]) with { Connection = (xyz[0], xyz[1], xyz[2]) };
        }
    }

    private PreciseProcessPlan? ReadPreciseProcessSource(PdfDictionary resources, int intent, PdfRenderOptions options)
    {
        ImageColorSpace source = ReadColorSpace(Name("DeviceCMYK"), resources, 0, intent: intent);
        bool declaredDefault = resources.TryGetValue(Name("ColorSpace"), out var spacesValue)
            && Resolve(spacesValue) is PdfDictionary spaces && spaces.ContainsKey(Name("DefaultCMYK"));
        if (declaredDefault && !source.IsDefault) return null;
        if (source.ComponentRange is not null || source.Components != 4 || source.Palette is not null
            || source.MultiConverter is not null || source.BlackPointCompensationEnabled) return null;
        if (source is { IsDeviceCmyk: true, Profile: null, HasIccSource: false, IsDefault: false })
            return new(PreciseProcessSourceKind.Working, null, options.WorkingCmykProfile.ContentSha256,
                (int)options.WorkingCmykProfile.WorkingCmykForwardIntent!.Value);
        if (intent != 1 || source.Profile is not PdfIccProfileTransform
            { Components: 4, IsLabInput: false, RenderingIntent: 1, ForwardTableSignature: "A2B1" } profile)
            return null;
        if (source.IsDefault && source.IsIccBased)
            return new(PreciseProcessSourceKind.Default, profile, profile.ContentSha256, 1);
        return source.IsDeviceCmyk && !source.IsDefault
            ? new(PreciseProcessSourceKind.Catalog, profile, profile.ContentSha256, 1) : null;
    }

    private static void ValidatePreciseProcessSource(RasterSurface surface, ImageColorSpace? source,
        int intent, bool blackPointCompensation)
    {
        if (surface.PreciseProcessPlan is not { } plan) return;
        if (source is null || !plan.Matches(source) || blackPointCompensation
            || plan.Kind != PreciseProcessSourceKind.Working && intent != plan.Intent
            || !ReferenceEquals(source.PreciseProcessBinding, plan))
            throw new NotSupportedException("The native process paint does not match the render's fixed source profile and intent.");
    }

    private readonly record struct PreciseProcessState(bool FillOverprint = false, bool StrokeOverprint = false,
        bool FillDeviceCmyk = false, bool StrokeDeviceCmyk = false,
        bool SupportedNormal = true, bool NormalBlend = true, int RenderingIntent = 1);

    // Qualification reads the whole page before allocating or clipping a backdrop. Until an
    // inverse transform is supported, RGB paint cannot supply native process plates.
    // A proof of an overprint page the native plates cannot carry (spot inks, images, shadings, text,
    // forms, groups, annotations or mixed sources) composes its inks in the byte renderer's ink surface,
    // the model a separation preview uses, and converts the composed ink precisely; composeInk is set.
    private PreciseProcessPlan? PreparePreciseProcessPlan(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken, out bool composeInk)
    {
        composeInk = false;
        bool proofing = options.Proof is { Enabled: true };
        var diagnostics = new HashSet<string>();
        PdfDictionary resources = _pageResources[pageIndex];
        var state = new PreciseProcessState(SupportedNormal: true, NormalBlend: true);
        var saved = new Stack<PreciseProcessState>();
        var visibility = new Stack<bool>();
        bool visible = true;
        var hiddenGroups = options.ForPrinting ? _printHiddenOptionalContentGroups : _hiddenOptionalContentGroups;
        bool actualOverprint = false, unsupported = false, rectangle = false;
        int paintedIntents = 0;
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
                case "ri":
                    if (operands.Count == 1 && Resolve(operands[0]) is PdfName intent)
                        state = state with { RenderingIntent = ReadRenderingIntent(intent) };
                    else unsupported = true;
                    break;
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
                    paintedIntents |= 1 << state.RenderingIntent;
                    actualOverprint |= state.FillOverprint;
                    unsupported |= !state.FillDeviceCmyk || !state.SupportedNormal || !state.NormalBlend;
                    rectangle = false;
                    break;
                case "S": case "s":
                    paintedIntents |= 1 << state.RenderingIntent;
                    actualOverprint |= state.StrokeOverprint;
                    unsupported |= !state.StrokeDeviceCmyk || !state.SupportedNormal || !state.NormalBlend;
                    rectangle = false;
                    break;
                case "B": case "B*": case "b": case "b*":
                    paintedIntents |= 1 << state.RenderingIntent;
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
        var page = _tree.Pages[pageIndex].Dictionary;
        if (!actualOverprint)
        {
            // Overprint set inside forms, patterns, Type 3 glyphs or annotation appearances.
            composeInk = proofing && ResourcesSetOverprint(page, resources, cancellationToken);
            return null;
        }
        PreciseProcessPlan? plan = null;
        for (int intent = 0; intent < 4; intent++)
        {
            if ((paintedIntents & (1 << intent)) == 0) continue;
            var candidate = ReadPreciseProcessSource(resources, intent, options);
            if (candidate is null || plan is not null && !plan.SameBinding(candidate)) unsupported = true;
            else plan ??= candidate;
        }
        if (unsupported || plan is null || page.ContainsKey(Name("Group")) || page.ContainsKey(Name("Annots")))
        {
            composeInk = proofing;
            if (proofing) return null;
            throw new NotSupportedException("This high-precision graphics state requires Normal CMYK path paints with one supported source profile and intent, without groups or other paint sources.");
        }
        return plan;
    }

    // Whether any graphics state the page can reach turns overprint on: page resources, nested form,
    // pattern and Type 3 glyph resources, and annotation appearance streams. Each object is visited once.
    private bool ResourcesSetOverprint(PdfDictionary page, PdfDictionary resources, CancellationToken cancellationToken)
    {
        var traversed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        var judged = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance);
        // The flag records that a form, pattern, glyph or appearance reached the dictionary. The page's own
        // states were already followed through its content, so only those reached that way are judged by
        // their definitions, even when the dictionary is the page's own.
        var pending = new Stack<(PdfDictionary Resources, bool Reached)>();
        pending.Push((resources, false));
        if (page.TryGetValue(Name("Annots"), out var annotsValue) && Resolve(annotsValue) is PdfArray annots)
            foreach (PdfObject item in annots)
                if (Resolve(item) is PdfDictionary annotation
                    && annotation.TryGetValue(Name("AP"), out var apValue) && Resolve(apValue) is PdfDictionary ap)
                    foreach (var entry in ap)
                        PushAppearance(Resolve(entry.Value));
        while (pending.TryPop(out var item))
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfDictionary current = item.Resources;
            if (item.Reached && judged.Add(current)
                && current.TryGetValue(Name("ExtGState"), out var statesValue) && Resolve(statesValue) is PdfDictionary states)
                foreach (var entry in states)
                    if (Resolve(entry.Value) is PdfDictionary settings && SetsOverprint(settings)) return true;
            if (!traversed.Add(current)) continue;
            foreach (string category in (string[])["XObject", "Pattern", "Font"])
                if (current.TryGetValue(Name(category), out var groupValue) && Resolve(groupValue) is PdfDictionary group)
                    foreach (var entry in group)
                    {
                        PdfObject resolved = Resolve(entry.Value);
                        PdfDictionary? owner = resolved is PdfStream stream ? stream.Dictionary : resolved as PdfDictionary;
                        if (owner is null) continue;
                        if (owner.TryGetValue(Name("Resources"), out var nested) && Resolve(nested) is PdfDictionary nestedResources)
                            pending.Push((nestedResources, true));
                        // A form, tiling pattern or Type 3 font without resources of its own draws with the page's.
                        else if (ReachesPageResources(category, resolved, owner)) pending.Push((resources, true));
                    }
        }
        return false;

        void PushAppearance(PdfObject value)
        {
            if (value is PdfStream stream)
            {
                if (stream.Dictionary.TryGetValue(Name("Resources"), out var nested) && Resolve(nested) is PdfDictionary r)
                    pending.Push((r, true));
                else pending.Push((resources, true));
            }
            else if (value is PdfDictionary states)
                foreach (var entry in states) if (Resolve(entry.Value) is PdfStream state) PushAppearance(state);
        }

        bool ReachesPageResources(string category, PdfObject resolved, PdfDictionary owner) => category switch
        {
            "XObject" => resolved is PdfStream && IsName(owner, "Subtype", "Form"),
            "Pattern" => resolved is PdfStream,
            _ => IsName(owner, "Subtype", "Type3")
        };

        bool SetsOverprint(PdfDictionary settings) => IsTrue(settings, "op") || IsTrue(settings, "OP");

        bool IsTrue(PdfDictionary settings, string key) =>
            settings.TryGetValue(Name(key), out var value) && Resolve(value) is PdfBoolean { Value: true };
    }
    private static void PaintPreciseProcessPixel(RasterSurface surface, int offset,
        PreciseProcessPaint source, double opacity)
    {
        if (!ReferenceEquals(surface.PreciseProcessPlan, source.Binding))
            throw new NotSupportedException("The native process paint has a different source binding.");
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
        Color appearance = surface.PreciseProcessPlan!.Convert(surface.PreciseCmyk!, ink);
        var color = appearance.Connection is { } xyz ? PreciseConnectionToRgb(xyz.X, xyz.Y, xyz.Z)
            : throw new NotSupportedException("The process source profile did not retain its connection values.");
        rgb[offset] = (float)color.Red;
        rgb[offset + 1] = (float)color.Green;
        rgb[offset + 2] = (float)color.Blue;
        rgb[offset + 3] = (float)alpha;
    }
}
