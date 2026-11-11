using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Writing;
using KillerPdf.Engine.Fonts;
using System.Text;

namespace KillerPdf.Engine.Authoring;

/// <summary>Builds deterministic PDF graphics operators for a page content stream.</summary>
public sealed class PdfContentStreamBuilder
{
    private readonly MemoryStream _output = new();
    private readonly Dictionary<PdfStandardFont, PdfName> _fonts = [];
    private readonly Dictionary<TrueTypeFont, EmbeddedFontUsage> _embeddedFonts = [];
    private readonly Dictionary<PdfImage, PdfName> _images = [];
    private readonly Dictionary<PdfImageAsset, PdfName> _assets = [];
    private readonly Dictionary<PdfOptionalContentGroup, PdfName> _optionalContentGroups = [];
    private readonly Dictionary<PdfGraphicsState, PdfName> _graphicsStates = [];
    private readonly Dictionary<PdfShading, PdfName> _shadings = [];
    private readonly Dictionary<PdfFormXObject, PdfName> _forms = [];
    private readonly Dictionary<PdfTilingPattern, PdfName> _patterns = [];
    private readonly Dictionary<PdfIccProfile, PdfName> _iccColorSpaces = [];
    private readonly Dictionary<PdfSpotColor, PdfName> _spotColors = [];
    private readonly Dictionary<PdfLabColorSpace, PdfName> _labColorSpaces = [];
    private readonly Dictionary<PdfIndexedColorSpace, PdfName> _indexedColorSpaces = [];
    private readonly Dictionary<PdfCalibratedColorSpace, PdfName> _calibratedColorSpaces = [];
    private int _nextColorSpaceResource = 1;
    private int _savedStateDepth;
    private readonly Stack<bool> _markedContentStack = [];
    private int _accessibleMarkedContentDepth;
    private bool _insideText;
    private bool _hasUntaggedContent;
    private bool _hasColorOperators;
    private int _nextFontResource = 1;
    private EmbeddedFontUsage? _activeEmbeddedFont;
    private readonly HashSet<int> _markedContentIds = [];

    internal IReadOnlyDictionary<PdfStandardFont, PdfName> FontResources => _fonts;
    internal IReadOnlyCollection<EmbeddedFontUsage> EmbeddedFontResources => _embeddedFonts.Values;
    internal IReadOnlyDictionary<PdfImage, PdfName> ImageResources => _images;
    internal IReadOnlyDictionary<PdfImageAsset, PdfName> AssetResources => _assets;
    internal IReadOnlyDictionary<PdfOptionalContentGroup, PdfName> OptionalContentResources =>
        _optionalContentGroups;
    internal IReadOnlyDictionary<PdfGraphicsState, PdfName> GraphicsStateResources =>
        _graphicsStates;
    internal IReadOnlyDictionary<PdfShading, PdfName> ShadingResources => _shadings;
    internal IReadOnlyDictionary<PdfFormXObject, PdfName> FormResources => _forms;
    internal IReadOnlyDictionary<PdfTilingPattern, PdfName> PatternResources => _patterns;
    internal IReadOnlyDictionary<PdfIccProfile, PdfName> IccColorSpaceResources => _iccColorSpaces;
    internal IReadOnlyDictionary<PdfSpotColor, PdfName> SpotColorResources => _spotColors;
    internal IReadOnlyDictionary<PdfLabColorSpace, PdfName> LabColorSpaceResources => _labColorSpaces;
    internal IReadOnlyDictionary<PdfIndexedColorSpace, PdfName> IndexedColorSpaceResources => _indexedColorSpaces;
    internal IReadOnlyDictionary<PdfCalibratedColorSpace, PdfName> CalibratedColorSpaceResources => _calibratedColorSpaces;
    internal IReadOnlyCollection<int> MarkedContentIds => _markedContentIds;
    internal bool HasUntaggedContent => _hasUntaggedContent;
    internal bool HasColorOperators => _hasColorOperators;

    /// <summary>Begins tagged marked content associated with a page-local MCID.</summary>
    public PdfContentStreamBuilder BeginMarkedContent(
        PdfStructureType type, int markedContentId)
    {
        if (!Enum.IsDefined(type))
            throw new ArgumentOutOfRangeException(nameof(type));
        ArgumentOutOfRangeException.ThrowIfNegative(markedContentId);
        if (!_markedContentIds.Add(markedContentId))
            throw new ArgumentException(
                "Marked-content identifiers must be unique within a page.", nameof(markedContentId));
        _output.Write(PdfObjectWriter.Write(new PdfName(
            Encoding.ASCII.GetBytes(PdfStructureTypeNames.Name(type)))));
        _output.WriteByte((byte)' ');
        _output.Write(PdfObjectWriter.Write(new PdfDictionary([
            new KeyValuePair<PdfName, PdfObject>(
                new PdfName("MCID"u8), new PdfInteger(markedContentId))])));
        _output.Write(" BDC\n"u8);
        _markedContentStack.Push(true);
        _accessibleMarkedContentDepth++;
        return this;
    }

    /// <summary>Begins pagination or layout content that assistive technology should ignore.</summary>
    public PdfContentStreamBuilder BeginArtifact()
    {
        _output.Write("/Artifact BMC\n"u8);
        _markedContentStack.Push(true);
        _accessibleMarkedContentDepth++;
        return this;
    }

    /// <summary>Begins content controlled by a named PDF layer.</summary>
    public PdfContentStreamBuilder BeginOptionalContent(PdfOptionalContentGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);
        if (!_optionalContentGroups.TryGetValue(group, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes(
                $"OC{_optionalContentGroups.Count + 1}"));
            _optionalContentGroups.Add(group, resource);
        }
        _output.Write("/OC "u8);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" BDC\n"u8);
        _markedContentStack.Push(false);
        return this;
    }

    /// <summary>Ends the innermost marked-content or optional-content sequence.</summary>
    public PdfContentStreamBuilder EndMarkedContent()
    {
        if (_markedContentStack.Count == 0)
            throw new InvalidOperationException("The marked-content stack is empty.");
        WriteOperator("EMC"u8);
        if (_markedContentStack.Pop()) _accessibleMarkedContentDepth--;
        return this;
    }

    /// <summary>Saves the current graphics state on the content-stream stack.</summary>
    public PdfContentStreamBuilder SaveState()
    {
        WriteOperator("q"u8);
        _savedStateDepth++;
        return this;
    }

    /// <summary>Restores the most recently saved graphics state.</summary>
    public PdfContentStreamBuilder RestoreState()
    {
        if (_savedStateDepth == 0)
            throw new InvalidOperationException("The graphics state stack is empty.");
        WriteOperator("Q"u8);
        _savedStateDepth--;
        return this;
    }

    /// <summary>Concatenates a six-value affine matrix with the current transformation matrix.</summary>
    public PdfContentStreamBuilder Transform(double a, double b, double c, double d, double e, double f) =>
        Operator("cm"u8, a, b, c, d, e, f);

    /// <summary>Sets the stroked path width in user-space units.</summary>
    public PdfContentStreamBuilder SetLineWidth(double width)
    {
        if (!double.IsFinite(width) || width < 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        return Operator("w"u8, width);
    }

    /// <summary>Sets the shape used at the ends of open stroked paths.</summary>
    public PdfContentStreamBuilder SetLineCap(PdfLineCap cap)
    {
        if (!Enum.IsDefined(cap)) throw new ArgumentOutOfRangeException(nameof(cap));
        return Operator("J"u8, (int)cap);
    }

    /// <summary>Sets the shape used where stroked path segments meet.</summary>
    public PdfContentStreamBuilder SetLineJoin(PdfLineJoin join)
    {
        if (!Enum.IsDefined(join)) throw new ArgumentOutOfRangeException(nameof(join));
        return Operator("j"u8, (int)join);
    }

    /// <summary>Sets the maximum miter-to-line-width ratio for mitered joins.</summary>
    public PdfContentStreamBuilder SetMiterLimit(double limit)
    {
        if (!double.IsFinite(limit) || limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return Operator("M"u8, limit);
    }

    /// <summary>Sets the alternating painted and unpainted lengths used for stroked paths.</summary>
    public PdfContentStreamBuilder SetDashPattern(
        IReadOnlyList<double> lengths, double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        if (!double.IsFinite(phase) || phase < 0)
            throw new ArgumentOutOfRangeException(nameof(phase));
        if (lengths.Any(length => !double.IsFinite(length) || length < 0))
            throw new ArgumentOutOfRangeException(nameof(lengths),
                "Dash lengths must be finite and nonnegative.");
        if (lengths.Count > 0 && lengths.All(length => length == 0))
            throw new ArgumentException("A dash pattern cannot contain only zero lengths.", nameof(lengths));

        _output.WriteByte((byte)'[');
        for (int index = 0; index < lengths.Count; index++)
        {
            if (index > 0) _output.WriteByte((byte)' ');
            WriteNumber(lengths[index]);
        }
        _output.Write("] "u8);
        WriteNumber(phase);
        _output.Write(" d\n"u8);
        return this;
    }

    /// <summary>Restores solid strokes.</summary>
    public PdfContentStreamBuilder SetSolidStroke() => SetDashPattern([]);

    /// <summary>Selects the color-rendering intent used by subsequent painting operations.</summary>
    public PdfContentStreamBuilder SetRenderingIntent(PdfRenderingIntent intent)
    {
        if (!Enum.IsDefined(intent)) throw new ArgumentOutOfRangeException(nameof(intent));
        _output.Write(PdfObjectWriter.Write(new PdfName(Encoding.ASCII.GetBytes(
            PdfRenderingIntentNames.Name(intent)))));
        _output.Write(" ri\n"u8);
        return this;
    }

    /// <summary>Sets the maximum device-pixel error used when approximating curves.</summary>
    public PdfContentStreamBuilder SetFlatnessTolerance(double tolerance)
    {
        if (!double.IsFinite(tolerance) || tolerance is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(tolerance));
        return Operator("i"u8, tolerance);
    }

    /// <summary>Applies reusable fill opacity, stroke opacity, and blend-mode settings.</summary>
    public PdfContentStreamBuilder SetGraphicsState(PdfGraphicsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!_graphicsStates.TryGetValue(state, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"GS{_graphicsStates.Count + 1}"));
            _graphicsStates.Add(state, resource);
        }
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" gs\n"u8);
        return this;
    }

    /// <summary>Sets equal nonstroking and stroking opacity values.</summary>
    public PdfContentStreamBuilder SetOpacity(double opacity) =>
        SetGraphicsState(new PdfGraphicsState(opacity, opacity));

    /// <summary>Sets independent nonstroking and stroking opacity values.</summary>
    public PdfContentStreamBuilder SetOpacity(double fillOpacity, double strokeOpacity) =>
        SetGraphicsState(new PdfGraphicsState(fillOpacity, strokeOpacity));

    /// <summary>Sets the transparency blend mode for subsequent painting operations.</summary>
    public PdfContentStreamBuilder SetBlendMode(PdfBlendMode blendMode) =>
        SetGraphicsState(new PdfGraphicsState(blendMode: blendMode));

    /// <summary>Paints an axial or radial gradient across the current clipping path.</summary>
    public PdfContentStreamBuilder PaintShading(PdfShading shading)
    {
        ArgumentNullException.ThrowIfNull(shading);
        if (!_shadings.TryGetValue(shading, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"Sh{_shadings.Count + 1}"));
            _shadings.Add(shading, resource);
        }
        RecordPaintedContent();
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" sh\n"u8);
        return this;
    }

    /// <summary>Places reusable form content at its natural size.</summary>
    public PdfContentStreamBuilder DrawForm(PdfFormXObject form, double x, double y)
    {
        ArgumentNullException.ThrowIfNull(form);
        return DrawForm(form, x, y, form.Width, form.Height);
    }

    /// <summary>Places reusable form content in the requested rectangle.</summary>
    public PdfContentStreamBuilder DrawForm(
        PdfFormXObject form, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(form);
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width == 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height == 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (!_forms.TryGetValue(form, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"Fm{_forms.Count + 1}"));
            _forms.Add(form, resource);
        }
        RecordPaintedContent();
        WriteOperator("q"u8);
        Operator("cm"u8, width / form.Width, 0, 0, height / form.Height, x, y);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" Do\n"u8);
        WriteOperator("Q"u8);
        return this;
    }

    /// <summary>Selects a reusable coloured tiling pattern for subsequent fills.</summary>
    public PdfContentStreamBuilder SetFillPattern(PdfTilingPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.PaintType != PdfTilingPatternPaintType.Colored)
            throw new ArgumentException(
                "An uncolored pattern must be selected together with a base color.", nameof(pattern));
        PdfName resource = PatternResource(pattern);
        _hasColorOperators = true;
        _output.Write("/Pattern cs\n"u8);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" scn\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceRGB base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(PdfTilingPattern pattern, PdfRgbColor color)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.PaintType != PdfTilingPatternPaintType.Uncolored)
            throw new ArgumentException(
                "A base color can only be supplied for an uncolored pattern.", nameof(pattern));
        PdfName resource = PatternResource(pattern);
        _hasColorOperators = true;
        _output.Write("[/Pattern /DeviceRGB] cs\n"u8);
        WriteNumber(color.Red);
        _output.WriteByte((byte)' ');
        WriteNumber(color.Green);
        _output.WriteByte((byte)' ');
        WriteNumber(color.Blue);
        _output.WriteByte((byte)' ');
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" scn\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceGray base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(PdfTilingPattern pattern, double gray)
    {
        ValidateColorComponent(gray, nameof(gray));
        ValidateUncoloredPattern(pattern);
        return SetPattern(pattern, new PdfName("DeviceGray"u8), [gray], stroke: false);
    }

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceCMYK base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(PdfTilingPattern pattern, PdfCmykColor color)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.PaintType != PdfTilingPatternPaintType.Uncolored)
            throw new ArgumentException(
                "A base color can only be supplied for an uncolored pattern.", nameof(pattern));
        PdfName resource = PatternResource(pattern);
        _hasColorOperators = true;
        _output.Write("[/Pattern /DeviceCMYK] cs\n"u8);
        WriteNumber(color.Cyan);
        _output.WriteByte((byte)' ');
        WriteNumber(color.Magenta);
        _output.WriteByte((byte)' ');
        WriteNumber(color.Yellow);
        _output.WriteByte((byte)' ');
        WriteNumber(color.Black);
        _output.WriteByte((byte)' ');
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" scn\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern with an ICCBased base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(
        PdfTilingPattern pattern, PdfIccProfile profile, params double[] components)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(components);
        ValidateIccComponents(profile, components);
        if (pattern.PaintType != PdfTilingPatternPaintType.Uncolored)
            throw new ArgumentException(
                "A base color can only be supplied for an uncolored pattern.", nameof(pattern));
        PdfName patternResource = PatternResource(pattern);
        PdfName colorSpaceResource = IccColorSpaceResource(profile);
        _hasColorOperators = true;
        _output.Write("[/Pattern "u8);
        _output.Write(PdfObjectWriter.Write(colorSpaceResource));
        _output.Write("] cs\n"u8);
        foreach (double component in components)
        {
            WriteNumber(component);
            _output.WriteByte((byte)' ');
        }
        _output.Write(PdfObjectWriter.Write(patternResource));
        _output.Write(" scn\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern with a calibrated base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(
        PdfTilingPattern pattern, PdfCalibratedColorSpace colorSpace, params double[] components)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(colorSpace);
        ArgumentNullException.ThrowIfNull(components);
        ValidateCalibratedComponents(colorSpace, components);
        if (pattern.PaintType != PdfTilingPatternPaintType.Uncolored)
            throw new ArgumentException(
                "A base color can only be supplied for an uncolored pattern.", nameof(pattern));
        PdfName patternResource = PatternResource(pattern);
        PdfName colorSpaceResource = CalibratedColorSpaceResource(colorSpace);
        _hasColorOperators = true;
        _output.Write("[/Pattern "u8);
        _output.Write(PdfObjectWriter.Write(colorSpaceResource));
        _output.Write("] cs\n"u8);
        foreach (double component in components)
        {
            WriteNumber(component);
            _output.WriteByte((byte)' ');
        }
        _output.Write(PdfObjectWriter.Write(patternResource));
        _output.Write(" scn\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern with a Separation spot base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(
        PdfTilingPattern pattern, PdfSpotColor color, double tint)
    {
        ValidateUncoloredPattern(pattern);
        ValidateSpotTint(color, tint);
        return SetPattern(pattern, SpotColorResource(color), [tint], stroke: false);
    }

    /// <summary>Selects an uncoloured stencil pattern with a CIE L*a*b* base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(
        PdfTilingPattern pattern, PdfLabColorSpace colorSpace,
        double lightness, double a, double b)
    {
        ValidateUncoloredPattern(pattern);
        ValidateLabComponents(colorSpace, lightness, a, b);
        return SetPattern(pattern, LabColorSpaceResource(colorSpace),
            [lightness, a, b], stroke: false);
    }

    /// <summary>Selects an uncoloured stencil pattern with an Indexed base colour.</summary>
    public PdfContentStreamBuilder SetFillPattern(
        PdfTilingPattern pattern, PdfIndexedColorSpace colorSpace, int index)
    {
        ValidateUncoloredPattern(pattern);
        ValidateIndexedColor(colorSpace, index);
        return SetPattern(pattern, IndexedColorSpaceResource(colorSpace), [index], stroke: false);
    }

    /// <summary>Selects a reusable coloured tiling pattern for subsequent strokes.</summary>
    public PdfContentStreamBuilder SetStrokePattern(PdfTilingPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.PaintType != PdfTilingPatternPaintType.Colored)
            throw new ArgumentException(
                "An uncolored pattern must be selected together with a base color.", nameof(pattern));
        PdfName resource = PatternResource(pattern);
        _hasColorOperators = true;
        _output.Write("/Pattern CS\n"u8);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" SCN\n"u8);
        return this;
    }

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceRGB stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(PdfTilingPattern pattern, PdfRgbColor color) =>
        SetStrokePattern(pattern, "DeviceRGB", [color.Red, color.Green, color.Blue]);

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceGray stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(PdfTilingPattern pattern, double gray)
    {
        ValidateColorComponent(gray, nameof(gray));
        return SetStrokePattern(pattern, "DeviceGray", [gray]);
    }

    /// <summary>Selects an uncoloured stencil pattern and supplies its DeviceCMYK stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(PdfTilingPattern pattern, PdfCmykColor color) =>
        SetStrokePattern(pattern, "DeviceCMYK",
            [color.Cyan, color.Magenta, color.Yellow, color.Black]);

    /// <summary>Selects an uncoloured stencil pattern with an ICCBased stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfIccProfile profile, params double[] components)
    {
        ValidateUncoloredPattern(pattern);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(components);
        ValidateIccComponents(profile, components);
        return SetStrokePattern(pattern, IccColorSpaceResource(profile), components);
    }

    /// <summary>Selects an uncoloured stencil pattern with a calibrated stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfCalibratedColorSpace colorSpace, params double[] components)
    {
        ValidateUncoloredPattern(pattern);
        ArgumentNullException.ThrowIfNull(colorSpace);
        ArgumentNullException.ThrowIfNull(components);
        ValidateCalibratedComponents(colorSpace, components);
        return SetStrokePattern(pattern, CalibratedColorSpaceResource(colorSpace), components);
    }

    /// <summary>Selects an uncoloured stencil pattern with a Separation spot stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfSpotColor color, double tint)
    {
        ValidateUncoloredPattern(pattern);
        ValidateSpotTint(color, tint);
        return SetStrokePattern(pattern, SpotColorResource(color), [tint]);
    }

    /// <summary>Selects an uncoloured stencil pattern with a CIE L*a*b* stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfLabColorSpace colorSpace,
        double lightness, double a, double b)
    {
        ValidateUncoloredPattern(pattern);
        ValidateLabComponents(colorSpace, lightness, a, b);
        return SetStrokePattern(pattern, LabColorSpaceResource(colorSpace),
            [lightness, a, b]);
    }

    /// <summary>Selects an uncoloured stencil pattern with an Indexed stroke colour.</summary>
    public PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfIndexedColorSpace colorSpace, int index)
    {
        ValidateUncoloredPattern(pattern);
        ValidateIndexedColor(colorSpace, index);
        return SetStrokePattern(pattern, IndexedColorSpaceResource(colorSpace), [index]);
    }

    private PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, string baseColorSpace, IReadOnlyList<double> components) =>
        SetStrokePattern(pattern, new PdfName(Encoding.ASCII.GetBytes(baseColorSpace)), components);

    private PdfContentStreamBuilder SetStrokePattern(
        PdfTilingPattern pattern, PdfName baseColorSpace, IReadOnlyList<double> components)
        => SetPattern(pattern, baseColorSpace, components, stroke: true);

    private PdfContentStreamBuilder SetPattern(
        PdfTilingPattern pattern, PdfName baseColorSpace,
        IReadOnlyList<double> components, bool stroke)
    {
        ValidateUncoloredPattern(pattern);
        PdfName patternResource = PatternResource(pattern);
        _hasColorOperators = true;
        _output.Write("[/Pattern "u8);
        _output.Write(PdfObjectWriter.Write(baseColorSpace));
        _output.Write(stroke ? "] CS\n"u8 : "] cs\n"u8);
        foreach (double component in components)
        {
            WriteNumber(component);
            _output.WriteByte((byte)' ');
        }
        _output.Write(PdfObjectWriter.Write(patternResource));
        _output.Write(stroke ? " SCN\n"u8 : " scn\n"u8);
        return this;
    }

    private static void ValidateUncoloredPattern(PdfTilingPattern pattern)
    {
        ArgumentNullException.ThrowIfNull(pattern);
        if (pattern.PaintType != PdfTilingPatternPaintType.Uncolored)
            throw new ArgumentException(
                "A base color can only be supplied for an uncolored pattern.", nameof(pattern));
    }

    private static void ValidateColorComponent(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name);
    }

    private static void ValidateShapeRectangle(
        double x, double y, double width, double height)
    {
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!double.IsFinite(width) || width <= 0 || !double.IsFinite(x + width))
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height <= 0 || !double.IsFinite(y + height))
            throw new ArgumentOutOfRangeException(nameof(height));
    }

    private PdfName PatternResource(PdfTilingPattern pattern)
    {
        if (!_patterns.TryGetValue(pattern, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"P{_patterns.Count + 1}"));
            _patterns.Add(pattern, resource);
        }
        return resource;
    }

    /// <summary>Begins a new path subpath at the specified point.</summary>
    public PdfContentStreamBuilder MoveTo(double x, double y) => Operator("m"u8, x, y);
    /// <summary>Adds a straight segment from the current point.</summary>
    public PdfContentStreamBuilder LineTo(double x, double y) => Operator("l"u8, x, y);
    /// <summary>Adds a cubic Bézier segment using two control points and a final point.</summary>
    public PdfContentStreamBuilder CurveTo(
        double x1, double y1, double x2, double y2, double x3, double y3) =>
        Operator("c"u8, x1, y1, x2, y2, x3, y3);
    /// <summary>Appends a cubic Bézier whose first control point is the current point.</summary>
    public PdfContentStreamBuilder CurveTo(double x2, double y2, double x3, double y3) =>
        Operator("v"u8, x2, y2, x3, y3);
    /// <summary>Appends a cubic Bézier whose second control point is the final point.</summary>
    public PdfContentStreamBuilder CurveToFinalControl(double x1, double y1, double x3, double y3) =>
        Operator("y"u8, x1, y1, x3, y3);
    /// <summary>Adds a closed rectangular subpath.</summary>
    public PdfContentStreamBuilder Rectangle(double x, double y, double width, double height) =>
        Operator("re"u8, x, y, width, height);

    /// <summary>Appends a closed elliptical path inside the supplied rectangle.</summary>
    public PdfContentStreamBuilder Ellipse(
        double x, double y, double width, double height)
    {
        ValidateShapeRectangle(x, y, width, height);
        const double kappa = 0.5522847498307936;
        double radiusX = width / 2;
        double radiusY = height / 2;
        double centerX = x + radiusX;
        double centerY = y + radiusY;
        return MoveTo(centerX + radiusX, centerY)
            .CurveTo(centerX + radiusX, centerY + radiusY * kappa,
                centerX + radiusX * kappa, centerY + radiusY,
                centerX, centerY + radiusY)
            .CurveTo(centerX - radiusX * kappa, centerY + radiusY,
                centerX - radiusX, centerY + radiusY * kappa,
                centerX - radiusX, centerY)
            .CurveTo(centerX - radiusX, centerY - radiusY * kappa,
                centerX - radiusX * kappa, centerY - radiusY,
                centerX, centerY - radiusY)
            .CurveTo(centerX + radiusX * kappa, centerY - radiusY,
                centerX + radiusX, centerY - radiusY * kappa,
                centerX + radiusX, centerY)
            .ClosePath();
    }

    /// <summary>Appends a closed circular path.</summary>
    public PdfContentStreamBuilder Circle(double centerX, double centerY, double radius)
    {
        if (!double.IsFinite(centerX)) throw new ArgumentOutOfRangeException(nameof(centerX));
        if (!double.IsFinite(centerY)) throw new ArgumentOutOfRangeException(nameof(centerY));
        if (!double.IsFinite(radius) || radius <= 0)
            throw new ArgumentOutOfRangeException(nameof(radius));
        return Ellipse(centerX - radius, centerY - radius, radius * 2, radius * 2);
    }

    /// <summary>Appends a closed rectangle whose corners use circular arcs.</summary>
    public PdfContentStreamBuilder RoundedRectangle(
        double x, double y, double width, double height, double radius)
    {
        ValidateShapeRectangle(x, y, width, height);
        if (!double.IsFinite(radius) || radius < 0
            || radius > Math.Min(width, height) / 2)
            throw new ArgumentOutOfRangeException(nameof(radius));
        if (radius == 0) return Rectangle(x, y, width, height);
        const double kappa = 0.5522847498307936;
        double control = radius * kappa;
        double right = x + width;
        double top = y + height;
        return MoveTo(x + radius, y)
            .LineTo(right - radius, y)
            .CurveTo(right - radius + control, y, right, y + radius - control, right, y + radius)
            .LineTo(right, top - radius)
            .CurveTo(right, top - radius + control, right - radius + control, top,
                right - radius, top)
            .LineTo(x + radius, top)
            .CurveTo(x + radius - control, top, x, top - radius + control, x, top - radius)
            .LineTo(x, y + radius)
            .CurveTo(x, y + radius - control, x + radius - control, y, x + radius, y)
            .ClosePath();
    }
    /// <summary>Closes the current path subpath.</summary>
    public PdfContentStreamBuilder ClosePath() => NoOperand("h"u8);
    /// <summary>Strokes the current path using the nonzero winding rule.</summary>
    public PdfContentStreamBuilder Stroke() => PaintingOperator("S"u8);
    /// <summary>Closes and strokes the current path.</summary>
    public PdfContentStreamBuilder CloseAndStroke() => PaintingOperator("s"u8);
    /// <summary>Fills the current path using the nonzero winding rule.</summary>
    public PdfContentStreamBuilder Fill() => PaintingOperator("f"u8);
    /// <summary>Fills the current path using the even-odd rule.</summary>
    public PdfContentStreamBuilder FillEvenOdd() => PaintingOperator("f*"u8);
    /// <summary>Fills and strokes the current path using the nonzero winding rule.</summary>
    public PdfContentStreamBuilder FillAndStroke() => PaintingOperator("B"u8);
    /// <summary>Fills and strokes the current path using the even-odd rule.</summary>
    public PdfContentStreamBuilder FillAndStrokeEvenOdd() => PaintingOperator("B*"u8);
    /// <summary>Closes, fills, and strokes the current path using the nonzero winding rule.</summary>
    public PdfContentStreamBuilder CloseFillAndStroke() => PaintingOperator("b"u8);
    /// <summary>Closes, fills, and strokes the current path using the even-odd rule.</summary>
    public PdfContentStreamBuilder CloseFillAndStrokeEvenOdd() => PaintingOperator("b*"u8);
    /// <summary>Ends the current path without painting it.</summary>
    public PdfContentStreamBuilder EndPath() => NoOperand("n"u8);
    /// <summary>Intersects the clipping path using the nonzero winding rule.</summary>
    public PdfContentStreamBuilder Clip()
    {
        WriteOperator("W"u8);
        WriteOperator("n"u8);
        return this;
    }
    /// <summary>Intersects the clipping path using the even-odd rule.</summary>
    public PdfContentStreamBuilder ClipEvenOdd()
    {
        WriteOperator("W*"u8);
        WriteOperator("n"u8);
        return this;
    }

    /// <summary>Places an immutable detached image in the page coordinate system.</summary>
    public PdfContentStreamBuilder DrawImage(
        PdfImageAsset asset, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!double.IsFinite(width) || width == 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height == 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!_assets.TryGetValue(asset, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"Ia{_assets.Count + 1}"));
            _assets.Add(asset, resource);
        }
        // A mirrored source lands in the same rectangle, drawn backwards.
        if (asset.Mirrored) { x += width; width = -width; }
        if (asset.Flipped) { y += height; height = -height; }
        RecordPaintedContent();
        WriteOperator("q"u8);
        Operator("cm"u8, width, 0, 0, height, x, y);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" Do\n"u8);
        WriteOperator("Q"u8);
        return this;
    }

    /// <summary>Places an image in the page coordinate system.</summary>
    public PdfContentStreamBuilder DrawImage(
        PdfImage image, double x, double y, double width, double height)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!double.IsFinite(width) || width == 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        if (!double.IsFinite(height) || height == 0)
            throw new ArgumentOutOfRangeException(nameof(height));
        if (!double.IsFinite(x)) throw new ArgumentOutOfRangeException(nameof(x));
        if (!double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(y));
        if (!_images.TryGetValue(image, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"Im{_images.Count + 1}"));
            _images.Add(image, resource);
        }
        RecordPaintedContent();
        WriteOperator("q"u8);
        Operator("cm"u8, width, 0, 0, height, x, y);
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(" Do\n"u8);
        WriteOperator("Q"u8);
        return this;
    }

    /// <summary>Sets the stroking color in the DeviceGray color space.</summary>
    public PdfContentStreamBuilder SetStrokeGray(double gray) => ColorOperator("G"u8, Component(gray, nameof(gray)));
    /// <summary>Sets the nonstroking color in the DeviceGray color space.</summary>
    public PdfContentStreamBuilder SetFillGray(double gray) => ColorOperator("g"u8, Component(gray, nameof(gray)));
    /// <summary>Sets the stroking color in the DeviceRGB color space.</summary>
    public PdfContentStreamBuilder SetStrokeRgb(double red, double green, double blue) =>
        ColorOperator("RG"u8, Component(red, nameof(red)), Component(green, nameof(green)), Component(blue, nameof(blue)));
    /// <summary>Sets the nonstroking color in the DeviceRGB color space.</summary>
    public PdfContentStreamBuilder SetFillRgb(double red, double green, double blue) =>
        ColorOperator("rg"u8, Component(red, nameof(red)), Component(green, nameof(green)), Component(blue, nameof(blue)));
    /// <summary>Sets the stroking color in the DeviceCMYK color space.</summary>
    public PdfContentStreamBuilder SetStrokeCmyk(double cyan, double magenta, double yellow, double black) =>
        ColorOperator("K"u8, Component(cyan, nameof(cyan)), Component(magenta, nameof(magenta)),
            Component(yellow, nameof(yellow)), Component(black, nameof(black)));
    /// <summary>Sets the nonstroking color in the DeviceCMYK color space.</summary>
    public PdfContentStreamBuilder SetFillCmyk(double cyan, double magenta, double yellow, double black) =>
        ColorOperator("k"u8, Component(cyan, nameof(cyan)), Component(magenta, nameof(magenta)),
            Component(yellow, nameof(yellow)), Component(black, nameof(black)));

    /// <summary>Sets the nonstroking color using an ICCBased color space.</summary>
    public PdfContentStreamBuilder SetFillIccColor(
        PdfIccProfile profile, params double[] components) =>
        SetIccColor(profile, components, stroke: false);

    /// <summary>Sets the stroking color using an ICCBased color space.</summary>
    public PdfContentStreamBuilder SetStrokeIccColor(
        PdfIccProfile profile, params double[] components) =>
        SetIccColor(profile, components, stroke: true);

    /// <summary>Sets a nonstroking Separation spot color and tint.</summary>
    public PdfContentStreamBuilder SetFillSpotColor(PdfSpotColor color, double tint) =>
        SetSpotColor(color, tint, stroke: false);

    /// <summary>Sets a stroking Separation spot color and tint.</summary>
    public PdfContentStreamBuilder SetStrokeSpotColor(PdfSpotColor color, double tint) =>
        SetSpotColor(color, tint, stroke: true);

    /// <summary>Sets the nonstroking color in a CIE L*a*b* color space.</summary>
    public PdfContentStreamBuilder SetFillLabColor(
        PdfLabColorSpace colorSpace, double lightness, double a, double b) =>
        SetLabColor(colorSpace, lightness, a, b, stroke: false);

    /// <summary>Sets the stroking color in a CIE L*a*b* color space.</summary>
    public PdfContentStreamBuilder SetStrokeLabColor(
        PdfLabColorSpace colorSpace, double lightness, double a, double b) =>
        SetLabColor(colorSpace, lightness, a, b, stroke: true);

    /// <summary>Sets the nonstroking color to an indexed palette entry.</summary>
    public PdfContentStreamBuilder SetFillIndexedColor(
        PdfIndexedColorSpace colorSpace, int index) =>
        SetIndexedColor(colorSpace, index, stroke: false);

    /// <summary>Sets the stroking color to an indexed palette entry.</summary>
    public PdfContentStreamBuilder SetStrokeIndexedColor(
        PdfIndexedColorSpace colorSpace, int index) =>
        SetIndexedColor(colorSpace, index, stroke: true);

    /// <summary>Sets the nonstroking color in a calibrated Gray or RGB color space.</summary>
    public PdfContentStreamBuilder SetFillCalibratedColor(
        PdfCalibratedColorSpace colorSpace, params double[] components) =>
        SetCalibratedColor(colorSpace, components, stroke: false);

    /// <summary>Sets the stroking color in a calibrated Gray or RGB color space.</summary>
    public PdfContentStreamBuilder SetStrokeCalibratedColor(
        PdfCalibratedColorSpace colorSpace, params double[] components) =>
        SetCalibratedColor(colorSpace, components, stroke: true);

    /// <summary>Begins a text object and resets its text-state tracking.</summary>
    public PdfContentStreamBuilder BeginText()
    {
        if (_insideText)
            throw new InvalidOperationException("A text object is already open.");
        WriteOperator("BT"u8);
        _insideText = true;
        return this;
    }

    /// <summary>Ends the current text object.</summary>
    public PdfContentStreamBuilder EndText()
    {
        RequireText();
        WriteOperator("ET"u8);
        _insideText = false;
        return this;
    }

    /// <summary>Selects a standard Type 1 font and text size.</summary>
    public PdfContentStreamBuilder SetFont(PdfStandardFont font, double size)
    {
        RequireText();
        if (!Enum.IsDefined(font))
            throw new ArgumentOutOfRangeException(nameof(font));
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (!_fonts.TryGetValue(font, out PdfName? resource))
        {
            resource = FontResourceName();
            _fonts.Add(font, resource);
        }
        _activeEmbeddedFont = null;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.WriteByte((byte)' ');
        WriteNumber(size);
        _output.Write(" Tf\n"u8);
        return this;
    }

    /// <summary>Selects an embedded TrueType font for Unicode text.</summary>
    public PdfContentStreamBuilder SetFont(TrueTypeFont font, double size)
    {
        RequireText();
        ArgumentNullException.ThrowIfNull(font);
        if (!font.EmbeddingAllowed)
            throw new InvalidOperationException($"The embedding permissions in {font.PostScriptName} prohibit PDF embedding.");
        if (!double.IsFinite(size) || size <= 0)
            throw new ArgumentOutOfRangeException(nameof(size));
        if (!_embeddedFonts.TryGetValue(font, out EmbeddedFontUsage? usage))
        {
            usage = new EmbeddedFontUsage(font, FontResourceName());
            _embeddedFonts.Add(font, usage);
        }
        _activeEmbeddedFont = usage;
        _output.Write(PdfObjectWriter.Write(usage.ResourceName));
        _output.WriteByte((byte)' ');
        WriteNumber(size);
        _output.Write(" Tf\n"u8);
        return this;
    }

    /// <summary>Moves the text line origin by the specified translation.</summary>
    public PdfContentStreamBuilder MoveText(double x, double y)
    {
        RequireText();
        return Operator("Td"u8, x, y);
    }

    /// <summary>Sets the text matrix and text-line matrix explicitly.</summary>
    public PdfContentStreamBuilder SetTextMatrix(
        double a, double b, double c, double d, double x, double y)
    {
        RequireText();
        return Operator("Tm"u8, a, b, c, d, x, y);
    }

    /// <summary>Sets the vertical distance between successive text lines.</summary>
    public PdfContentStreamBuilder SetTextLeading(double leading)
    {
        RequireText();
        return Operator("TL"u8, leading);
    }

    /// <summary>Moves to the next text line using the current leading.</summary>
    public PdfContentStreamBuilder MoveToNextTextLine()
    {
        RequireText();
        return NoOperand("T*"u8);
    }

    /// <summary>Sets additional spacing applied after each text character.</summary>
    public PdfContentStreamBuilder SetCharacterSpacing(double spacing)
    {
        RequireText();
        return Operator("Tc"u8, spacing);
    }

    /// <summary>Sets additional spacing applied to word-space characters.</summary>
    public PdfContentStreamBuilder SetWordSpacing(double spacing)
    {
        RequireText();
        return Operator("Tw"u8, spacing);
    }

    /// <summary>Sets horizontal text scaling as a percentage of normal width.</summary>
    public PdfContentStreamBuilder SetHorizontalTextScale(double percent)
    {
        RequireText();
        if (!double.IsFinite(percent) || percent <= 0)
            throw new ArgumentOutOfRangeException(nameof(percent));
        return Operator("Tz"u8, percent);
    }

    /// <summary>Sets the vertical displacement of text from the baseline.</summary>
    public PdfContentStreamBuilder SetTextRise(double rise)
    {
        RequireText();
        return Operator("Ts"u8, rise);
    }

    /// <summary>Sets whether text is filled, stroked, clipped, or combined.</summary>
    public PdfContentStreamBuilder SetTextRenderingMode(PdfTextRenderingMode mode)
    {
        RequireText();
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        return Operator("Tr"u8, (int)mode);
    }

    /// <summary>Shows text using the selected standard font and Latin-1-compatible encoding.</summary>
    public PdfContentStreamBuilder ShowLatin1Text(string text)
    {
        RequireText();
        ArgumentNullException.ThrowIfNull(text);
        RecordPaintedContent();
        byte[] bytes = new byte[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] > 0xFF)
                throw new ArgumentException("Built-in font text is limited to Latin-1 bytes.", nameof(text));
            bytes[index] = (byte)text[index];
        }
        _output.Write(PdfObjectWriter.Write(new PdfString(bytes, PdfStringForm.Literal)));
        _output.Write(" Tj\n"u8);
        return this;
    }

    /// <summary>Writes Latin-1 text segments separated by PDF text-position adjustments.</summary>
    public PdfContentStreamBuilder ShowPositionedLatin1Text(
        IReadOnlyList<string> segments, IReadOnlyList<double> adjustments)
    {
        RequireText();
        ValidatePositionedText(segments, adjustments);
        RecordPaintedContent();
        _output.WriteByte((byte)'[');
        for (int index = 0; index < segments.Count; index++)
        {
            if (index > 0)
            {
                WriteNumber(adjustments[index - 1]);
                _output.WriteByte((byte)' ');
            }
            _output.Write(PdfObjectWriter.Write(new PdfString(
                Latin1Bytes(segments[index]), PdfStringForm.Literal)));
            if (index + 1 < segments.Count) _output.WriteByte((byte)' ');
        }
        _output.Write("] TJ\n"u8);
        return this;
    }

    /// <summary>Writes Unicode text as two-byte glyph identifiers and records its ToUnicode mappings.</summary>
    public PdfContentStreamBuilder ShowUnicodeText(string text)
    {
        RequireText();
        ArgumentNullException.ThrowIfNull(text);
        RecordPaintedContent();
        EmbeddedFontUsage usage = _activeEmbeddedFont
            ?? throw new InvalidOperationException("Select an embedded TrueType font before writing Unicode text.");
        WriteUnicodeHexString(usage, text);
        _output.Write(" Tj\n"u8);
        return this;
    }

    /// <summary>
    /// Draws the single glyph of <paramref name="glyphText"/> but records <paramref name="actualText"/>
    /// in the ToUnicode map, so copy and search read the original text. Right-to-left text uses this
    /// to show a mirrored bracket while keeping the bracket that was typed.
    /// </summary>
    public PdfContentStreamBuilder ShowUnicodeText(string glyphText, string actualText)
    {
        RequireText();
        ArgumentNullException.ThrowIfNull(glyphText);
        ArgumentException.ThrowIfNullOrEmpty(actualText);
        EmbeddedFontUsage usage = _activeEmbeddedFont
            ?? throw new InvalidOperationException("Select an embedded TrueType font before writing Unicode text.");
        IReadOnlyList<FontGlyphMapping> glyphs = usage.Font.MapText(glyphText);
        if (glyphs.Count != 1 || glyphs[0].Glyph == 0)
            throw new ArgumentException($"Font {usage.Font.PostScriptName} has no single glyph for {glyphText}.", nameof(glyphText));
        RecordPaintedContent();
        ushort characterCode = usage.AddSubstituteMapping(glyphs[0].Glyph, actualText);
        _output.WriteByte((byte)'<');
        WriteHexByte((byte)(characterCode >> 8));
        WriteHexByte((byte)characterCode);
        _output.Write("> Tj\n"u8);
        return this;
    }

    /// <summary>Paints explicit glyphs with one logical replacement string, outside a text object.</summary>
    /// <remarks>Origins are in current user space before the CTM. Glyph zero is rejected.
    /// Calls must follow logical cluster order for extraction. Each cluster accepts at most
    /// 4096 glyphs and 65536 UTF-16 code units, with at least one of each.
    /// A single glyph also records logical text in ToUnicode; multiple glyphs rely on ActualText.</remarks>
    public PdfContentStreamBuilder DrawGlyphCluster(TrueTypeFont font, double fontSize,
        string logicalText, IReadOnlyList<PdfPositionedGlyph> glyphs)
    {
        if (_insideText)
            throw new InvalidOperationException("A glyph cluster requires a closed text object.");
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(logicalText);
        ArgumentNullException.ThrowIfNull(glyphs);
        if (!font.EmbeddingAllowed)
            throw new InvalidOperationException($"The embedding permissions in {font.PostScriptName} prohibit PDF embedding.");
        if (!double.IsFinite(fontSize) || fontSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(fontSize));
        if (logicalText.Length is < 1 or > 65536)
            throw new ArgumentOutOfRangeException(nameof(logicalText));
        if (glyphs.Count is < 1 or > 4096)
            throw new ArgumentOutOfRangeException(nameof(glyphs));
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(logicalText);
        PdfPositionedGlyph[] positioned = glyphs.ToArray();
        foreach (PdfPositionedGlyph glyph in positioned)
        {
            if (glyph.GlyphId == 0 || glyph.GlyphId >= font.GlyphCount)
                throw new ArgumentOutOfRangeException(nameof(glyphs), "Glyph IDs must be nonzero and belong to the font.");
            if (!double.IsFinite(glyph.X) || !double.IsFinite(glyph.Y))
                throw new ArgumentOutOfRangeException(nameof(glyphs), "Glyph origins must be finite.");
        }
        _embeddedFonts.TryGetValue(font, out EmbeddedFontUsage? usage);
        usage ??= new EmbeddedFontUsage(font, new PdfName("Pending"u8));
        if (positioned.Length == 1)
            usage.ValidateSubstituteGlyphCapacity(positioned[0].GlyphId, logicalText);
        else
            usage.ValidateUnmappedGlyphCapacity(positioned.Select(glyph => glyph.GlyphId));
        byte[] replacement = new byte[text.Length + 2];
        replacement[0] = 0xFE;
        replacement[1] = 0xFF;
        text.CopyTo(replacement, 2);
        byte[] actualText = PdfObjectWriter.Write(new PdfString(replacement, PdfStringForm.Hexadecimal));

        EmbeddedFontUsage? previousFont = _activeEmbeddedFont;
        SaveState().BeginText().SetFont(font, fontSize)
            .SetHorizontalTextScale(100).SetCharacterSpacing(0).SetWordSpacing(0)
            .SetTextRise(0).SetTextRenderingMode(PdfTextRenderingMode.Fill);
        usage = _activeEmbeddedFont!;
        RecordPaintedContent();
        _output.Write("/Span << /ActualText "u8);
        _output.Write(actualText);
        _output.Write(" >> BDC\n"u8);
        foreach (PdfPositionedGlyph glyph in positioned)
        {
            ushort code = positioned.Length == 1
                ? usage.AddSubstituteMapping(glyph.GlyphId, logicalText)
                : usage.AddUnmappedGlyph(glyph.GlyphId);
            SetTextMatrix(1, 0, 0, 1, glyph.X, glyph.Y);
            _output.WriteByte((byte)'<');
            WriteHexByte((byte)(code >> 8));
            WriteHexByte((byte)code);
            _output.Write("> Tj\n"u8);
        }
        _output.Write("EMC\n"u8);
        EndText().RestoreState();
        _activeEmbeddedFont = previousFont;
        return this;
    }

    /// <summary>Writes embedded Unicode text segments separated by PDF text-position adjustments.</summary>
    public PdfContentStreamBuilder ShowPositionedUnicodeText(
        IReadOnlyList<string> segments, IReadOnlyList<double> adjustments)
    {
        RequireText();
        ValidatePositionedText(segments, adjustments);
        EmbeddedFontUsage usage = _activeEmbeddedFont
            ?? throw new InvalidOperationException("Select an embedded TrueType font before writing Unicode text.");
        RecordPaintedContent();
        _output.WriteByte((byte)'[');
        for (int index = 0; index < segments.Count; index++)
        {
            if (index > 0)
            {
                WriteNumber(adjustments[index - 1]);
                _output.WriteByte((byte)' ');
            }
            WriteUnicodeHexString(usage, segments[index]);
            if (index + 1 < segments.Count) _output.WriteByte((byte)' ');
        }
        _output.Write("] TJ\n"u8);
        return this;
    }

    /// <summary>Validates balanced graphics, text, and marked-content state and returns the stream bytes.</summary>
    public byte[] Build()
    {
        if (_savedStateDepth != 0)
            throw new InvalidOperationException("Every saved graphics state must be restored before building.");
        if (_insideText)
            throw new InvalidOperationException("The text object must be ended before building.");
        if (_markedContentStack.Count != 0)
            throw new InvalidOperationException(
                "Every marked-content sequence must be ended before building.");
        return _output.ToArray();
    }

    private PdfContentStreamBuilder Operator(ReadOnlySpan<byte> name, params double[] operands)
    {
        foreach (double operand in operands)
        {
            WriteNumber(operand);
            _output.WriteByte((byte)' ');
        }
        WriteOperator(name);
        return this;
    }

    private PdfContentStreamBuilder NoOperand(ReadOnlySpan<byte> name)
    {
        WriteOperator(name);
        return this;
    }

    private PdfContentStreamBuilder ColorOperator(ReadOnlySpan<byte> name, params double[] operands)
    {
        _hasColorOperators = true;
        return Operator(name, operands);
    }

    private PdfContentStreamBuilder SetIccColor(
        PdfIccProfile profile, IReadOnlyList<double> components, bool stroke)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(components);
        ValidateIccComponents(profile, components);
        PdfName resource = IccColorSpaceResource(profile);
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(stroke ? " CS\n"u8 : " cs\n"u8);
        foreach (double component in components)
        {
            WriteNumber(component);
            _output.WriteByte((byte)' ');
        }
        _output.Write(stroke ? "SCN\n"u8 : "scn\n"u8);
        return this;
    }

    private PdfName IccColorSpaceResource(PdfIccProfile profile)
    {
        if (!_iccColorSpaces.TryGetValue(profile, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"CS{_nextColorSpaceResource++}"));
            _iccColorSpaces.Add(profile, resource);
        }
        return resource;
    }

    private PdfContentStreamBuilder SetSpotColor(
        PdfSpotColor color, double tint, bool stroke)
    {
        ValidateSpotTint(color, tint);
        PdfName resource = SpotColorResource(color);
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(stroke ? " CS\n"u8 : " cs\n"u8);
        WriteNumber(tint);
        _output.Write(stroke ? " SCN\n"u8 : " scn\n"u8);
        return this;
    }

    private PdfContentStreamBuilder SetLabColor(
        PdfLabColorSpace colorSpace, double lightness, double a, double b, bool stroke)
    {
        ValidateLabComponents(colorSpace, lightness, a, b);
        PdfName resource = LabColorSpaceResource(colorSpace);
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(stroke ? " CS\n"u8 : " cs\n"u8);
        WriteNumber(lightness); _output.WriteByte((byte)' ');
        WriteNumber(a); _output.WriteByte((byte)' ');
        WriteNumber(b);
        _output.Write(stroke ? " SCN\n"u8 : " scn\n"u8);
        return this;
    }

    private PdfContentStreamBuilder SetIndexedColor(
        PdfIndexedColorSpace colorSpace, int index, bool stroke)
    {
        ValidateIndexedColor(colorSpace, index);
        PdfName resource = IndexedColorSpaceResource(colorSpace);
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(stroke ? " CS\n"u8 : " cs\n"u8);
        WriteNumber(index);
        _output.Write(stroke ? " SCN\n"u8 : " scn\n"u8);
        return this;
    }

    private static void ValidateSpotTint(PdfSpotColor color, double tint)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (!double.IsFinite(tint) || tint is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(tint));
    }

    private PdfName SpotColorResource(PdfSpotColor color)
    {
        if (!_spotColors.TryGetValue(color, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"CS{_nextColorSpaceResource++}"));
            _spotColors.Add(color, resource);
        }
        return resource;
    }

    private static void ValidateLabComponents(
        PdfLabColorSpace colorSpace, double lightness, double a, double b)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        if (!double.IsFinite(lightness) || lightness is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(lightness));
        if (!double.IsFinite(a) || a < colorSpace.MinimumA || a > colorSpace.MaximumA)
            throw new ArgumentOutOfRangeException(nameof(a));
        if (!double.IsFinite(b) || b < colorSpace.MinimumB || b > colorSpace.MaximumB)
            throw new ArgumentOutOfRangeException(nameof(b));
    }

    private PdfName LabColorSpaceResource(PdfLabColorSpace colorSpace)
    {
        if (!_labColorSpaces.TryGetValue(colorSpace, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"CS{_nextColorSpaceResource++}"));
            _labColorSpaces.Add(colorSpace, resource);
        }
        return resource;
    }

    private static void ValidateIndexedColor(PdfIndexedColorSpace colorSpace, int index)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        if ((uint)index >= (uint)colorSpace.EntryCount)
            throw new ArgumentOutOfRangeException(nameof(index));
    }

    private PdfName IndexedColorSpaceResource(PdfIndexedColorSpace colorSpace)
    {
        if (!_indexedColorSpaces.TryGetValue(colorSpace, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"CS{_nextColorSpaceResource++}"));
            _indexedColorSpaces.Add(colorSpace, resource);
        }
        return resource;
    }

    private PdfContentStreamBuilder SetCalibratedColor(
        PdfCalibratedColorSpace colorSpace, IReadOnlyList<double> components, bool stroke)
    {
        ArgumentNullException.ThrowIfNull(colorSpace);
        ArgumentNullException.ThrowIfNull(components);
        ValidateCalibratedComponents(colorSpace, components);
        PdfName resource = CalibratedColorSpaceResource(colorSpace);
        _hasColorOperators = true;
        _output.Write(PdfObjectWriter.Write(resource));
        _output.Write(stroke ? " CS\n"u8 : " cs\n"u8);
        foreach (double component in components)
        {
            WriteNumber(component);
            _output.WriteByte((byte)' ');
        }
        _output.Write(stroke ? "SCN\n"u8 : "scn\n"u8);
        return this;
    }

    private static void ValidateCalibratedComponents(
        PdfCalibratedColorSpace colorSpace, IReadOnlyList<double> components)
    {
        if (components.Count != colorSpace.ComponentCount)
            throw new ArgumentException(
                $"This calibrated color space requires {colorSpace.ComponentCount} components.",
                nameof(components));
        if (components.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(components));
    }

    private PdfName CalibratedColorSpaceResource(PdfCalibratedColorSpace colorSpace)
    {
        if (!_calibratedColorSpaces.TryGetValue(colorSpace, out PdfName? resource))
        {
            resource = new PdfName(Encoding.ASCII.GetBytes($"CS{_nextColorSpaceResource++}"));
            _calibratedColorSpaces.Add(colorSpace, resource);
        }
        return resource;
    }

    private static void ValidateIccComponents(
        PdfIccProfile profile, IReadOnlyList<double> components)
    {
        if (components.Count != profile.ComponentCount)
            throw new ArgumentException(
                $"The {profile.ColorSpace} ICC profile requires {profile.ComponentCount} color components.",
                nameof(components));
        if (components.Any(value => !double.IsFinite(value) || value is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(components));
    }

    private PdfContentStreamBuilder PaintingOperator(ReadOnlySpan<byte> name)
    {
        RecordPaintedContent();
        return NoOperand(name);
    }

    private void RecordPaintedContent()
    {
        if (_accessibleMarkedContentDepth == 0) _hasUntaggedContent = true;
    }

    private void WriteNumber(double value)
    {
        if (!double.IsFinite(value))
            throw new ArgumentOutOfRangeException(nameof(value), "Graphics operands must be finite.");
        PdfObject number = value == Math.Truncate(value) && value is >= long.MinValue and <= long.MaxValue
            ? new PdfInteger((long)value)
            : new PdfReal(value);
        _output.Write(PdfObjectWriter.Write(number));
    }

    private static byte[] Latin1Bytes(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        byte[] bytes = new byte[text.Length];
        for (int index = 0; index < text.Length; index++)
        {
            if (text[index] > 0xFF)
                throw new ArgumentException("Built-in font text is limited to Latin-1 bytes.", nameof(text));
            bytes[index] = (byte)text[index];
        }
        return bytes;
    }

    private static void ValidatePositionedText(
        IReadOnlyList<string> segments, IReadOnlyList<double> adjustments)
    {
        ArgumentNullException.ThrowIfNull(segments);
        ArgumentNullException.ThrowIfNull(adjustments);
        if (segments.Count == 0)
            throw new ArgumentException("Positioned text requires at least one text segment.", nameof(segments));
        if (adjustments.Count != segments.Count - 1)
            throw new ArgumentException(
                "Positioned text requires exactly one fewer adjustment than text segments.",
                nameof(adjustments));
        if (segments.Any(segment => segment is null))
            throw new ArgumentException("A positioned text segment cannot be null.", nameof(segments));
        if (adjustments.Any(adjustment => !double.IsFinite(adjustment)))
            throw new ArgumentOutOfRangeException(nameof(adjustments));
    }

    private void WriteUnicodeHexString(EmbeddedFontUsage usage, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _output.WriteByte((byte)'<');
        foreach (FontGlyphMapping mapping in usage.Font.MapText(text))
        {
            if (mapping.Glyph == 0 && mapping.UnicodeSequence != "\0")
                throw new ArgumentException(
                    $"Font {usage.Font.PostScriptName} has no glyph for Unicode sequence {mapping.UnicodeSequence}.", nameof(text));
            ushort characterCode = usage.AddMapping(mapping.Glyph, mapping.UnicodeSequence);
            WriteHexByte((byte)(characterCode >> 8));
            WriteHexByte((byte)characterCode);
        }
        _output.WriteByte((byte)'>');
    }

    private void WriteOperator(ReadOnlySpan<byte> value)
    {
        _output.Write(value);
        _output.WriteByte((byte)'\n');
    }

    private static double Component(double value, string name)
    {
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new ArgumentOutOfRangeException(name, "Color components must be between zero and one.");
        return value;
    }

    private void RequireText()
    {
        if (!_insideText)
            throw new InvalidOperationException("This operator requires an open text object.");
    }

    private PdfName FontResourceName() =>
        new(Encoding.ASCII.GetBytes($"F{_nextFontResource++}"));

    private void WriteHexByte(byte value)
    {
        const string digits = "0123456789ABCDEF";
        _output.WriteByte((byte)digits[value >> 4]);
        _output.WriteByte((byte)digits[value & 0x0F]);
    }
}

internal sealed class EmbeddedFontUsage(TrueTypeFont font, PdfName resourceName)
{
    private readonly SortedDictionary<ushort, EmbeddedCharacterMapping> _mappings = [];
    private readonly Dictionary<string, ushort> _codeByUnicode = new(StringComparer.Ordinal);
    private readonly Dictionary<(ushort Glyph, string Unicode), ushort> _substituteCodes = [];
    private readonly Dictionary<ushort, ushort> _unmappedGlyphCodes = [];

    public TrueTypeFont Font { get; } = font;
    public PdfName ResourceName { get; } = resourceName;
    public IReadOnlyDictionary<ushort, EmbeddedCharacterMapping> Mappings => _mappings;

    public void ValidateSubstituteGlyphCapacity(ushort glyph, string unicodeSequence)
    {
        if (_codeByUnicode.TryGetValue(unicodeSequence, out ushort natural) && _mappings[natural].Glyph == glyph
            || _substituteCodes.ContainsKey((glyph, unicodeSequence))) return;
        int available = ushort.MaxValue - _mappings.Count + (_mappings.ContainsKey(0) ? 1 : 0);
        if (available == 0)
            throw new InvalidOperationException("The embedded-font character-code range is exhausted.");
    }

    public void ValidateUnmappedGlyphCapacity(IEnumerable<ushort> glyphs)
    {
        int required = glyphs.Distinct().Count(glyph => !_unmappedGlyphCodes.ContainsKey(glyph));
        int available = ushort.MaxValue - _mappings.Count + (_mappings.ContainsKey(0) ? 1 : 0);
        if (required > available)
            throw new InvalidOperationException("The embedded-font character-code range is exhausted.");
    }

    public ushort AddUnmappedGlyph(ushort glyph)
    {
        if (_unmappedGlyphCodes.TryGetValue(glyph, out ushort existing)) return existing;
        ushort code = glyph;
        if (_mappings.ContainsKey(code))
            code = (ushort)Enumerable.Range(1, ushort.MaxValue)
                .First(candidate => !_mappings.ContainsKey((ushort)candidate));
        _mappings.Add(code, new EmbeddedCharacterMapping(glyph, null));
        _unmappedGlyphCodes.Add(glyph, code);
        return code;
    }

    public ushort AddMapping(ushort glyph, int unicodeScalar)
        => AddMapping(glyph, char.ConvertFromUtf32(unicodeScalar));

    public ushort AddMapping(ushort glyph, string unicodeSequence)
    {
        if (_codeByUnicode.TryGetValue(unicodeSequence, out ushort existingCode))
        {
            if (_mappings[existingCode].Glyph != glyph)
                throw new InvalidOperationException(
                    "One Unicode sequence resolved to conflicting font glyphs.");
            return existingCode;
        }
        ushort code = glyph;
        if (_mappings.ContainsKey(code))
        {
            int available = Enumerable.Range(1, ushort.MaxValue)
                .FirstOrDefault(candidate => !_mappings.ContainsKey((ushort)candidate));
            if (available == 0)
                throw new InvalidOperationException("The embedded-font character-code range is exhausted.");
            code = (ushort)available;
        }
        _mappings.Add(code, new EmbeddedCharacterMapping(glyph, unicodeSequence));
        if (!_codeByUnicode.TryAdd(unicodeSequence, code))
            throw new InvalidOperationException(
                "The embedded-font Unicode mapping could not be registered.");
        return code;
    }

    /// <summary>A code that draws <paramref name="glyph"/> and extracts as
    /// <paramref name="unicodeSequence"/>, kept apart from the natural mapping of that text.</summary>
    public ushort AddSubstituteMapping(ushort glyph, string unicodeSequence)
    {
        if (_codeByUnicode.TryGetValue(unicodeSequence, out ushort natural) && _mappings[natural].Glyph == glyph)
            return natural;
        if (_substituteCodes.TryGetValue((glyph, unicodeSequence), out ushort existing)) return existing;
        ushort code = glyph;
        if (_mappings.ContainsKey(code))
        {
            int available = Enumerable.Range(1, ushort.MaxValue)
                .FirstOrDefault(candidate => !_mappings.ContainsKey((ushort)candidate));
            if (available == 0)
                throw new InvalidOperationException("The embedded-font character-code range is exhausted.");
            code = (ushort)available;
        }
        _mappings.Add(code, new EmbeddedCharacterMapping(glyph, unicodeSequence));
        _substituteCodes.Add((glyph, unicodeSequence), code);
        return code;
    }
}

internal sealed record EmbeddedCharacterMapping(ushort Glyph, string? UnicodeSequence);
