using System.Buffers;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Rendering;

/// <summary>Renders PDF page content through the engine-owned CPU raster pipeline.</summary>
public sealed partial class PdfPageRenderer
{
    private const long MaximumDecodedImageCacheBytes = 64L * 1024 * 1024;
    private const long MaximumFlattenedGlyphCacheBytes = 16L * 1024 * 1024;
    private const long MaximumRenderedPageCacheBytes = 64L * 1024 * 1024;
    private const int MaximumMeshVerticesPerRow = 65_536;
    private static readonly ArrayPool<byte> RasterBuffers = PdfScratchBuffers.Bytes;
    private readonly PdfDocument _document;
    private readonly PdfPageContentReader _content;
    private readonly IReadOnlyList<PdfPageInformation> _pages;
    private readonly IReadOnlyList<PdfPageBoxInformation> _boxes;
    private readonly PdfPageTree _tree;
    private readonly IPdfFontResolver? _fontResolver;
    private readonly IReadOnlyList<PdfDictionary> _pageResources;
    private readonly HashSet<int> _recoveredPageResources = [];
    private readonly IReadOnlySet<int> _hiddenOptionalContentGroups;
    private readonly BoundedCache<int, (IReadOnlyList<PdfContentInstruction> Instructions,
        IReadOnlySet<string> Diagnostics)> _instructionCache = new(32);
    private readonly BoundedCache<PdfDictionary, PdfExtractionFont> _fontCache =
        new(256, ReferenceEqualityComparer.Instance);
    private readonly BoundedCache<PdfName, PdfDictionary> _recoveredFontCache = new(14);
    private readonly BoundedCache<PdfGlyphOutline, IReadOnlyList<Point[]>> _glyphPathCache = new(
        4096, ReferenceEqualityComparer.Instance,
        MaximumFlattenedGlyphCacheBytes,
        paths => paths.Sum(path => (long)path.Length * sizeof(double) * 2));
    private readonly BoundedCache<ImageCacheKey, DecodedImage> _imageCache = new(
        64, maximumWeight: MaximumDecodedImageCacheBytes,
        weight: image => image.Samples.LongLength + (image.Alpha?.LongLength ?? 0));
    private readonly BoundedCache<PdfStream, ParsedStream> _streamInstructionCache = new(
        128, ReferenceEqualityComparer.Instance,
        PdfContentStreamReader.MaximumSourceBytes, parsed => parsed.SourceBytes);
    private readonly BoundedCache<RenderCacheKey, PdfRenderedPage> _renderCache = new(
        16, maximumWeight: MaximumRenderedPageCacheBytes,
        weight: page => page.Pixels.Length);

    /// <summary>Creates a renderer for an immutable document.</summary>
    public PdfPageRenderer(PdfDocument document, IPdfFontResolver? fontResolver = null)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        _outputProfile = new(ReadOutputProfile);
        if (!document.CanReadPageContent)
            throw new InvalidOperationException("Authenticate the document before rendering pages.");
        _content = new PdfPageContentReader(document);
        _fontResolver = fontResolver;
        _tree = PdfPageTree.Read(document, allowCycleRecovery: true);
        _pages = PdfPageInformation.Read(document);
        _boxes = PdfPageBoxInformation.Read(document);
        _pageResources = Enumerable.Range(0, _pages.Count)
            .Select(PageResources).ToArray();
        _hiddenOptionalContentGroups = PdfOptionalContentReader.Read(_document).Groups
            .Where(group => !group.IsInitiallyVisible)
            .Select(group => group.ObjectNumber).ToHashSet();
    }

    /// <summary>Renders the currently supported page operators into BGRA32 pixels.</summary>
    public PdfRenderedPage Render(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.CacheResult) return RenderUncached(pageIndex, options, cancellationToken);
        var key = new RenderCacheKey(pageIndex, options.Width, options.Height,
            options.TransparentBackground, options.IncludeAnnotations,
            options.IncludeFormFields);
        return _renderCache.GetOrAdd(key,
            _ => RenderUncached(pageIndex, options, cancellationToken));
    }

    /// <summary>Renders into caller-owned BGRA32 storage and returns page diagnostics without caching pixels.</summary>
    /// <remarks>The destination must hold Width * Height * 4 bytes. Cancellation can leave partial pixels.</remarks>
    public IReadOnlyList<string> RenderInto(int pageIndex, PdfRenderOptions options, byte[] destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(destination);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        if (destination.Length < checked(options.Width * options.Height * 4))
            throw new ArgumentException("The destination is too small for the rendered page.", nameof(destination));
        cancellationToken.ThrowIfCancellationRequested();
        return RenderUncached(pageIndex, options, cancellationToken, destination).Diagnostics;
    }

    private PdfRenderedPage RenderUncached(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken, byte[]? destination = null)
    {
        byte background = options.TransparentBackground ? (byte)0 : (byte)255;
        var pixels = new RasterSurface(destination ?? GC.AllocateUninitializedArray<byte>(
            checked(options.Width * options.Height * 4)), 0, 0, options.Width, options.Height);
        cancellationToken.ThrowIfCancellationRequested();
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
            pixels.Data.AsSpan(0, checked(options.Width * options.Height * 4)))
            .Fill(0x00FFFFFFu | (uint)background << 24);

        PdfPageInformation page = _pages[pageIndex];
        PdfPageBoxBounds crop = _boxes[pageIndex].CropBox;
        bool quarterTurn = page.Rotation is 90 or 270;
        double displayWidth = quarterTurn ? page.Height : page.Width;
        double displayHeight = quarterTurn ? page.Width : page.Height;
        double scaleX = options.Width / displayWidth;
        double scaleY = options.Height / displayHeight;
        var frame = new RasterFrame(options.Width, options.Height, scaleX, scaleY);
        Matrix normalize = new(1, 0, 0, 1, -crop.Left, -crop.Bottom);
        Matrix rotate = page.Rotation switch
        {
            90 => new Matrix(0, -1, 1, 0, 0, page.Width),
            180 => new Matrix(-1, 0, 0, -1, page.Width, page.Height),
            270 => new Matrix(0, 1, -1, 0, page.Height, 0),
            _ => Matrix.Identity
        };
        var initialState = new GraphicsState(normalize.Then(rotate), Color.Black, Color.Black,
            1, 1, 1, RendererLineCap.Butt, RendererLineJoin.Miter, 10,
            [], 0, RendererBlendMode.Normal, [],
            false, null, null, false, null, null,
            new ImageColorSpace(1, null), new ImageColorSpace(1, null), null, null);
        var diagnostics = new HashSet<string>();
        if (_tree.RecoveredCycle) diagnostics.Add("A cyclic page-tree reference was omitted.");
        if (_recoveredPageResources.Contains(pageIndex))
            diagnostics.Add("Invalid page resources were treated as empty.");
        var activeForms = new HashSet<PdfStream>();
        int recoveredFormExpansions = 0;
        IReadOnlySet<int> hiddenOptionalContentGroups = _hiddenOptionalContentGroups;
        PdfDictionary pageResources = _pageResources[pageIndex];
        PdfColorTransform? pageProfile = ReadGroupProfile(_tree.Pages[pageIndex].Dictionary, pageResources, diagnostics);
        if (CmykGroup(_tree.Pages[pageIndex].Dictionary, pageResources, false))
            pixels.EnableInk(Color.White, profile: pageProfile);
        else if (pageProfile is { Components: 1 or 3 }) pixels.EnableRgb(Color.White, pageProfile);
        RasterSurface pageSurface = pixels;
        int previousParallelism = _rowParallelism;
        _rowParallelism = Math.Max(1, options.MaximumParallelism);
        try
        {
            Process(ReadInstructions(pageIndex, cancellationToken, diagnostics),
                pageResources, initialState, 0);
            RenderAppearances();
            pixels.ConvertToBgra(cancellationToken);
            return new PdfRenderedPage(options.Width, options.Height, pixels.Data, diagnostics);
        }
        finally
        {
            _rowParallelism = previousParallelism;
            pageSurface.ReleaseInk();
        }

        void Process(IEnumerable<PdfContentInstruction> instructions,
            PdfDictionary resources, GraphicsState initial, int depth,
            bool beginKnockoutObjects = true)
        {
            if (depth > 32) throw new FormatException("Form XObject nesting limit exceeded.");
            using var clipScratch = new ClipScratchScope();
            GraphicsState state = RebindNamedColors(initial, pixels);
            var stack = new Stack<(GraphicsState Graphics, PdfDictionary? Font,
                PdfExtractionFont? ExtractionFont, double FontSize,
                double CharacterSpacing, double WordSpacing, double HorizontalScale,
                double Leading, double Rise, int RenderingMode, int ClipMark)>();
            var path = new List<List<Point>>();
            List<Point>? subpath = null;
            var visibilityStack = new Stack<bool>();
            bool contentVisible = true;
            int compatibilityDepth = 0;
            byte[]? textClipCoverage = null;
            byte[]? textClipMask = null;
            bool? pendingClipEvenOdd = null;
            Matrix textMatrix = Matrix.Identity, textLineMatrix = Matrix.Identity;
            PdfDictionary? textFont = null;
            PdfExtractionFont? extractionFont = null;
            double textSize = 0, characterSpacing = 0, wordSpacing = 0;
            double horizontalScale = 1, textLeading = 0, textRise = 0;
            int textRenderingMode = 0;
            foreach (PdfContentInstruction instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<PdfObject> values = instruction.Operands;
                if (instruction.Operator is "BMC" or "BDC")
                {
                    visibilityStack.Push(contentVisible);
                    if (instruction.Operator == "BDC")
                        contentVisible &= OptionalContentVisible(values, resources);
                    continue;
                }
                if (instruction.Operator == "EMC")
                {
                    contentVisible = visibilityStack.Count > 0
                        ? visibilityStack.Pop() : contentVisible;
                    continue;
                }
                if (!contentVisible) continue;
                if (beginKnockoutObjects && IsPaintingOperation(instruction))
                    state.Knockout?.BeginObject();
                switch (instruction.Operator)
                {
                case "q":
                    stack.Push((state, textFont, extractionFont, textSize,
                        characterSpacing, wordSpacing, horizontalScale, textLeading,
                        textRise, textRenderingMode, clipScratch.Save()));
                    break;
                case "Q":
                    if (stack.Count > 0)
                    {
                        int clipMark;
                        (state, textFont, extractionFont, textSize, characterSpacing,
                            wordSpacing, horizontalScale, textLeading, textRise,
                            textRenderingMode, clipMark) = stack.Pop();
                        clipScratch.Restore(clipMark);
                    }
                    path.Clear();
                    subpath = null;
                    break;
                case "cm" when values.Count == 6:
                    state = state with { Transform = Matrix.From(values).Then(state.Transform) };
                    break;
                case "g" when values.Count == 1:
                    state = state with
                    {
                        Fill = Color.Gray(Number(values[0])),
                        FillColorSpace = new ImageColorSpace(1, null),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "rg" when values.Count == 3:
                    state = state with
                    {
                        Fill = Color.Rgb(Number(values[0]), Number(values[1]), Number(values[2])),
                        FillColorSpace = new ImageColorSpace(3, null),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "k" when values.Count == 4:
                    state = state with
                    {
                        Fill = DeviceCmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3])),
                        FillColorSpace = new ImageColorSpace(4, null, Profile: _outputProfile.Value),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "scn" when state.FillPatternSpace && values.Count > 0
                    && values[^1] is PdfName fillPatternName:
                    if (!TryGetPattern(resources, fillPatternName,
                        out PatternPaint? fillPattern, out int paintType)
                        || fillPattern!.Shading is not null && values.Count != 1
                        || fillPattern.Tiling is not null && paintType == 1 && values.Count != 1
                        || fillPattern.Tiling is not null && paintType == 2
                            && (state.FillPatternBase is null
                            || values.Count != state.FillPatternBase.Components + 1))
                    {
                        diagnostics.Add("Pattern rendering is not implemented.");
                        break;
                    }
                    Color? baseColor = fillPattern.Tiling is not null && paintType == 2
                        ? state.FillPatternBase!.Convert(values.Take(values.Count - 1)
                            .Select(value => Number(Resolve(value))).ToArray())
                        : null;
                    state = state with
                    {
                        FillPattern = fillPattern with { BaseColor = baseColor }
                    };
                    break;
                case "cs" when values.Count == 1:
                    if (TryReadPatternColorSpace(values[0], resources,
                        out ImageColorSpace? fillPatternBase))
                    {
                        state = state with
                        {
                            FillPatternSpace = true,
                            Fill = Color.NonPainting,
                            FillComponents = null,
                            FillColorSpace = null,
                            FillPatternBase = fillPatternBase,
                            FillPattern = null
                        };
                    }
                    else
                    {
                        ImageColorSpace fillSpace = ReadColorSpace(values[0], resources, 0).ForDestination(pixels);
                        state = state with
                        {
                            FillColorSpace = fillSpace,
                            Fill = fillSpace.InitialPaint(out double[]? initialFill),
                            FillComponents = initialFill,
                            FillPatternSpace = false,
                            FillPatternBase = null,
                            FillPattern = null
                        };
                    }
                    break;
                case "sc" or "scn" when !state.FillPatternSpace
                    && state.FillColorSpace is not null:
                    state = state with
                    {
                        Fill = ReadPaintColor(state.FillColorSpace, values, state.Fill, diagnostics,
                            state.FillComponents, out double[]? fillComponents),
                        FillComponents = fillComponents
                    };
                    break;
                case "G" when values.Count == 1:
                    state = state with
                    {
                        Stroke = Color.Gray(Number(values[0])),
                        StrokeColorSpace = new ImageColorSpace(1, null),
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "RG" when values.Count == 3:
                    state = state with
                    {
                        Stroke = Color.Rgb(Number(values[0]), Number(values[1]), Number(values[2])),
                        StrokeColorSpace = new ImageColorSpace(3, null),
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "K" when values.Count == 4:
                    state = state with
                    {
                        Stroke = DeviceCmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3])),
                        StrokeColorSpace = new ImageColorSpace(4, null, Profile: _outputProfile.Value),
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "CS" when values.Count == 1:
                    if (TryReadPatternColorSpace(values[0], resources,
                        out ImageColorSpace? strokePatternBase))
                    {
                        state = state with
                        {
                            StrokePatternSpace = true,
                            Stroke = Color.NonPainting,
                            StrokeComponents = null,
                            StrokeColorSpace = null,
                            StrokePatternBase = strokePatternBase,
                            StrokePattern = null
                        };
                    }
                    else
                    {
                        ImageColorSpace strokeSpace = ReadColorSpace(values[0], resources, 0).ForDestination(pixels);
                        state = state with
                        {
                            StrokeColorSpace = strokeSpace,
                            Stroke = strokeSpace.InitialPaint(out double[]? initialStroke),
                            StrokeComponents = initialStroke,
                            StrokePatternSpace = false,
                            StrokePatternBase = null,
                            StrokePattern = null
                        };
                    }
                    break;
                case "SC" or "SCN" when !state.StrokePatternSpace
                    && state.StrokeColorSpace is not null:
                    state = state with
                    {
                        Stroke = ReadPaintColor(state.StrokeColorSpace, values, state.Stroke, diagnostics,
                            state.StrokeComponents, out double[]? strokeComponents),
                        StrokeComponents = strokeComponents
                    };
                    break;
                case "SCN" when state.StrokePatternSpace && values.Count > 0
                    && values[^1] is PdfName strokePatternName:
                    if (!TryGetPattern(resources, strokePatternName,
                        out PatternPaint? strokePattern, out int strokePaintType)
                        || strokePattern!.Shading is not null && values.Count != 1
                        || strokePattern.Tiling is not null && strokePaintType == 1
                            && values.Count != 1
                        || strokePattern.Tiling is not null && strokePaintType == 2
                            && (state.StrokePatternBase is null
                            || values.Count != state.StrokePatternBase.Components + 1))
                    {
                        diagnostics.Add("Pattern rendering is not implemented.");
                        break;
                    }
                    Color? strokeBaseColor = strokePattern.Tiling is not null
                        && strokePaintType == 2
                        ? state.StrokePatternBase!.Convert(values.Take(values.Count - 1)
                            .Select(value => Number(Resolve(value))).ToArray())
                        : null;
                    state = state with
                    {
                        StrokePattern = strokePattern with { BaseColor = strokeBaseColor }
                    };
                    break;
                case "w" when values.Count == 1:
                    state = state with { LineWidth = Math.Max(0, Number(values[0])) };
                    break;
                case "J" when values.Count == 1:
                    double capValue = Number(Resolve(values[0]));
                    if (capValue != Math.Truncate(capValue) || capValue is < 0 or > 2)
                        throw new FormatException("A line cap style is invalid.");
                    state = state with { LineCap = (RendererLineCap)(int)capValue };
                    break;
                case "j" when values.Count == 1:
                    PdfObject joinOperand = Resolve(values[0]);
                    if (_document.UsesCompatibilityRecovery && joinOperand is not (PdfInteger or PdfReal))
                    {
                        diagnostics.Add("An invalid line-join operation was ignored.");
                        break;
                    }
                    double joinValue = Number(joinOperand);
                    if (joinValue != Math.Truncate(joinValue) || joinValue is < 0 or > 2)
                        throw new FormatException("A line join style is invalid.");
                    state = state with { LineJoin = (RendererLineJoin)(int)joinValue };
                    break;
                case "M" when values.Count == 1:
                    double miterLimit = Number(Resolve(values[0]));
                    if (!double.IsFinite(miterLimit) || miterLimit < 1)
                        throw new FormatException("A miter limit is invalid.");
                    state = state with { MiterLimit = miterLimit };
                    break;
                case "d" when values.Count == 2 && Resolve(values[0]) is PdfArray dashArray:
                    double[] dashPattern = dashArray.Select(item => Number(Resolve(item))).ToArray();
                    double dashPhase = Number(Resolve(values[1]));
                    if (dashPattern.Any(length => !double.IsFinite(length) || length < 0)
                        || dashPattern.Length > 0 && dashPattern.All(length => length == 0)
                        || !double.IsFinite(dashPhase))
                        throw new FormatException("A line dash pattern is invalid.");
                    if (dashPhase < 0 && dashPattern.Length > 0)
                    {
                        double cycle = dashPattern.Sum()
                            * (dashPattern.Length % 2 == 0 ? 1 : 2);
                        dashPhase = (dashPhase % cycle + cycle) % cycle;
                    }
                    state = state with
                    {
                        DashPattern = Array.AsReadOnly(dashPattern),
                        DashPhase = dashPhase
                    };
                    break;
                case "gs" when values.Count == 1 && values[0] is PdfName stateName:
                    if (TryGetGraphicsState(resources, stateName, out double? fillAlpha,
                        out double? strokeAlpha, out RendererBlendMode? blendMode,
                        out bool unsupportedBlend, out PdfObject? softMaskValue,
                        out PdfObject? graphicsFontValue, out PdfDictionary? strokeSettings))
                    {
                        state = ApplyGraphicsStrokeSettings(state, strokeSettings!, diagnostics);
                        state = ApplyOverprintSettings(state, strokeSettings!);
                        state = state with
                        {
                            FillAlpha = fillAlpha ?? state.FillAlpha,
                            StrokeAlpha = strokeAlpha ?? state.StrokeAlpha,
                            BlendMode = blendMode ?? state.BlendMode,
                            GraphicsSoftMask = softMaskValue is null
                                ? state.GraphicsSoftMask
                                : ReadGraphicsSoftMask(softMaskValue, resources, state, depth)
                        };
                        if (graphicsFontValue is not null)
                        {
                            PdfArray font = ResolveArray(graphicsFontValue, 2, "A graphics-state font");
                            PdfDictionary dictionary = Resolve(font[0]) as PdfDictionary
                                ?? throw new FormatException("A graphics-state font dictionary is invalid.");
                            double size = Number(Resolve(font[1]));
                            if (!double.IsFinite(size))
                                throw new FormatException("A graphics-state font size is invalid.");
                            textFont = dictionary;
                            textSize = size;
                            extractionFont = null;
                        }
                    }
                    if (unsupportedBlend)
                        diagnostics.Add("Transparency blend-mode rendering is not implemented.");
                    break;
                case "m" when values.Count == 2:
                    subpath = [state.Transform.Apply(Number(values[0]), Number(values[1]))];
                    path.Add(subpath);
                    break;
                case "l" when values.Count == 2 && subpath is not null:
                    subpath.Add(state.Transform.Apply(Number(values[0]), Number(values[1])));
                    break;
                case "c" when values.Count == 6 && subpath is { Count: > 0 }:
                    AddCubic(subpath, subpath[^1],
                        state.Transform.Apply(Number(values[0]), Number(values[1])),
                        state.Transform.Apply(Number(values[2]), Number(values[3])),
                        state.Transform.Apply(Number(values[4]), Number(values[5])));
                    break;
                case "v" when values.Count == 4 && subpath is { Count: > 0 }:
                    AddCubic(subpath, subpath[^1], subpath[^1],
                        state.Transform.Apply(Number(values[0]), Number(values[1])),
                        state.Transform.Apply(Number(values[2]), Number(values[3])));
                    break;
                case "y" when values.Count == 4 && subpath is { Count: > 0 }:
                    Point end = state.Transform.Apply(Number(values[2]), Number(values[3]));
                    AddCubic(subpath, subpath[^1],
                        state.Transform.Apply(Number(values[0]), Number(values[1])), end, end);
                    break;
                case "h" when subpath is { Count: > 1 }:
                    subpath.Add(subpath[0]);
                    break;
                case "h":
                    break;
                case "W":
                    pendingClipEvenOdd = false;
                    break;
                case "W*":
                    pendingClipEvenOdd = true;
                    break;
                case "re" when values.Count == 4:
                    double x = Number(values[0]), y = Number(values[1]);
                    double w = Number(values[2]), h = Number(values[3]);
                    subpath =
                    [
                        state.Transform.Apply(x, y), state.Transform.Apply(x + w, y),
                        state.Transform.Apply(x + w, y + h), state.Transform.Apply(x, y + h),
                        state.Transform.Apply(x, y)
                    ];
                    path.Add(subpath);
                    break;
                case "f" or "F" or "f*" when path.Count > 0:
                    PaintFill(path, instruction.Operator == "f*");
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd, frame);
                    path.Clear();
                    subpath = null;
                    break;
                case "S" or "s" when path.Count > 0:
                    if (instruction.Operator == "s" && subpath is { Count: > 1 }) subpath.Add(subpath[0]);
                    PaintStroke(path);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd, frame);
                    path.Clear();
                    subpath = null;
                    break;
                case "B" or "B*" or "b" or "b*" when path.Count > 0:
                    if (instruction.Operator[0] == 'b' && subpath is { Count: > 1 })
                        subpath.Add(subpath[0]);
                    PaintFill(path, instruction.Operator.EndsWith('*'));
                    PaintStroke(path);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd, frame);
                    path.Clear();
                    subpath = null;
                    break;
                case "f" or "F" or "f*" or "S" or "s" or "B" or "B*" or "b" or "b*":
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd, frame);
                    path.Clear();
                    subpath = null;
                    break;
                case "n":
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd, frame);
                    path.Clear();
                    subpath = null;
                    break;
                case "BT":
                    textMatrix = textLineMatrix = Matrix.Identity;
                    textClipCoverage = null;
                    textClipMask = null;
                    break;
                case "ET":
                    if (textClipCoverage is not null || textClipMask is not null)
                    {
                        CoverageMask mask = textClipCoverage is null
                            ? CoverageMask.Empty
                            : CoverageMask.FromPageBuffer(textClipCoverage, options.Width,
                                options.Height);
                        if (textClipMask is not null)
                        {
                            CoverageMask type3 = CoverageMask.FromPageBuffer(textClipMask,
                                options.Width, options.Height, 4, 3);
                            mask = mask.IsEmpty ? type3 : UnionMasks(mask, type3, options.Width,
                                options.Height);
                        }
                        state = state with { Clips = AddClip(state.Clips, mask) };
                        textClipCoverage = null;
                        textClipMask = null;
                    }
                    break;
                case "Tf" when values.Count == 2 && values[0] is PdfName fontName:
                    textFont = ResolveFont(resources, fontName);
                    if (textFont is null)
                    {
                        textFont = RecoverStandardFont(fontName);
                        if (textFont is not null)
                            diagnostics.Add($"Missing standard font resource /{fontName.ValueAsLatin1()} was recovered.");
                    }
                    extractionFont = null;
                    textSize = Number(values[1]);
                    break;
                case "Tm" when values.Count == 6:
                    textMatrix = textLineMatrix = Matrix.From(values);
                    break;
                case "Td" or "TD" when values.Count == 2:
                    if (_document.UsesCompatibilityRecovery
                        && (values[0] is not (PdfInteger or PdfReal)
                            || values[1] is not (PdfInteger or PdfReal)))
                    {
                        diagnostics.Add("An invalid text-position operation was ignored.");
                        break;
                    }
                    double textX = Number(values[0]), textY = Number(values[1]);
                    if (instruction.Operator == "TD") textLeading = -textY;
                    textLineMatrix = new Matrix(1, 0, 0, 1, textX, textY)
                        .Then(textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;
                case "T*":
                    textLineMatrix = new Matrix(1, 0, 0, 1, 0, -textLeading)
                        .Then(textLineMatrix);
                    textMatrix = textLineMatrix;
                    break;
                case "Tc" when values.Count == 1:
                    characterSpacing = Number(values[0]);
                    break;
                case "Tw" when values.Count == 1:
                    wordSpacing = Number(values[0]);
                    break;
                case "Tz" when values.Count == 1:
                    horizontalScale = Number(values[0]) / 100;
                    break;
                case "TL" when values.Count == 1:
                    textLeading = Number(values[0]);
                    break;
                case "Tr" when values.Count == 1:
                    textRenderingMode = (int)Number(values[0]);
                    break;
                case "Ts" when values.Count == 1:
                    textRise = Number(values[0]);
                    break;
                case "Tj" when values.Count == 1 && values[0] is PdfString text:
                    ShowText(text);
                    break;
                case "TJ" when values.Count == 1 && values[0] is PdfArray positionedText:
                    foreach (PdfObject item in positionedText)
                    {
                        PdfObject part = Resolve(item);
                        if (part is PdfString segment) ShowText(segment);
                        else if (part is PdfInteger or PdfReal)
                        {
                            double adjustment = -Number(part) / 1000 * textSize;
                            if (IsVerticalText()) AdvanceTextVector(0, adjustment);
                            else AdvanceText(adjustment * horizontalScale);
                        }
                    }
                    break;
                case "'" when values.Count == 1 && values[0] is PdfString nextLineText:
                    MoveToNextLine();
                    ShowText(nextLineText);
                    break;
                case "\"" when values.Count == 3 && values[2] is PdfString spacedText:
                    wordSpacing = Number(values[0]);
                    characterSpacing = Number(values[1]);
                    MoveToNextLine();
                    ShowText(spacedText);
                    break;
                case "Do" when values.Count == 1 && values[0] is PdfName xObjectName:
                    if (!TryGetXObject(resources, xObjectName, out PdfStream? xObject)
                        || xObject is null)
                        diagnostics.Add("An XObject resource could not be resolved.");
                    else if (xObject.Dictionary.TryGetValue(Name("OC"),
                        out PdfObject? optionalContent)
                        && !EvaluateOptionalContent(optionalContent,
                            hiddenOptionalContentGroups, 0))
                        break;
                    else if (IsName(xObject.Dictionary, "Subtype", "Image"))
                    {
                        if (!TryRenderImage(xObject, resources, state.Transform, state.Clips,
                            state.PaintFill, state.FillAlpha, state.BlendMode, state.GraphicsSoftMask,
                            state.Knockout, cancellationToken, pixels, options.Width,
                            options.Height, scaleX, scaleY,
                            out string? imageDiagnostic, state.FillOverprint))
                            diagnostics.Add(imageDiagnostic
                                ?? "Image rendering is not implemented.");
                    }
                    else if (IsName(xObject.Dictionary, "Subtype", "Form"))
                        RenderForm(xObject, resources, state, depth);
                    break;
                case "BI" when values.Count == 1 && values[0] is PdfDictionary inlineDictionary
                    && instruction.InlineImageData.HasValue:
                    var inlineImage = new PdfStream(inlineDictionary,
                        instruction.InlineImageData.Value.Span);
                    if (!TryRenderImage(inlineImage, resources, state.Transform, state.Clips,
                        state.PaintFill, state.FillAlpha, state.BlendMode, state.GraphicsSoftMask,
                        state.Knockout, cancellationToken, pixels, options.Width,
                        options.Height, scaleX, scaleY,
                        out string? inlineDiagnostic, state.FillOverprint))
                        diagnostics.Add(inlineDiagnostic
                            ?? "Inline-image rendering is not implemented.");
                    break;
                case "sh" when values.Count == 1 && values[0] is PdfName shadingName:
                    if (!TryRenderShading(resources, shadingName, state, pixels,
                        options.Width, options.Height, scaleX, scaleY, cancellationToken,
                        out string? shadingDiagnostic))
                        diagnostics.Add(shadingDiagnostic
                            ?? "Shading rendering is not implemented.");
                    break;
                case "ri" when values.Count == 1 && values[0] is PdfName:
                case "i" when values.Count == 1:
                case "MP" when values.Count == 1 && values[0] is PdfName:
                case "DP" when values.Count == 2 && values[0] is PdfName:
                case "d0" when values.Count == 2:
                case "d1" when values.Count == 6:
                    break;
                case "BX" when values.Count == 0:
                    compatibilityDepth++;
                    break;
                case "EX" when values.Count == 0:
                    if (compatibilityDepth > 0) compatibilityDepth--;
                    break;
                default:
                    if (compatibilityDepth == 0)
                        diagnostics.Add(
                            $"Rendering operator {instruction.Operator} is not implemented.");
                    break;
                }
            }

            bool OptionalContentVisible(
                IReadOnlyList<PdfObject> operands, PdfDictionary currentResources)
            {
                if (operands.Count != 2 || operands[0] is not PdfName tag
                    || tag.ValueAsLatin1() != "OC") return true;
                PdfObject property = operands[1];
                if (property is PdfName propertyName)
                {
                    if (!currentResources.TryGetValue(Name("Properties"),
                        out PdfObject? propertiesValue)
                        || Resolve(propertiesValue) is not PdfDictionary properties
                        || !properties.TryGetValue(propertyName, out property))
                        return true;
                }
                return EvaluateOptionalContent(property, hiddenOptionalContentGroups, 0);
            }

            void PaintFill(IReadOnlyList<List<Point>> fillPath, bool evenOdd)
            {
                if (state.FillPattern is null)
                {
                    FillPaths(pixels, options.Width, options.Height,
                        scaleX, scaleY, fillPath, state.PaintFill, state.FillAlpha, evenOdd,
                        state.BlendMode, state.Clips, state.GraphicsSoftMask, state.Knockout,
                        cancellationToken);
                    return;
                }
                var fillClip = new ClipRegion(RasterizeFill(fillPath, evenOdd, frame));
                RenderPattern(state.FillPattern, fillPath, fillClip, resources, state, depth);
            }

            void PaintStroke(IReadOnlyList<List<Point>> strokePath)
            {
                double lineWidth = state.LineWidth * state.Transform.StrokeScale;
                IReadOnlyList<List<Point>> paintedPath = state.DashPattern.Count == 0
                    ? strokePath : CreateDashedPaths(strokePath, state.Transform,
                        state.DashPattern, state.DashPhase);
                if (state.StrokePattern is null)
                {
                    StrokePaths(pixels, options.Width, options.Height,
                        scaleX, scaleY,
                        paintedPath, state.PaintStroke, state.StrokeAlpha, lineWidth,
                        state.LineCap, state.LineJoin, state.MiterLimit,
                        state.BlendMode, state.Clips, state.GraphicsSoftMask, state.Knockout,
                        cancellationToken);
                    return;
                }
                var strokeClip = new ClipRegion(RasterizeStroke(paintedPath, lineWidth,
                    state.LineCap, state.LineJoin, state.MiterLimit, frame));
                RenderPattern(state.StrokePattern, paintedPath, strokeClip,
                    resources, state, depth);
            }

            void RenderPattern(PatternPaint paint,
                IReadOnlyList<List<Point>> paintPath, ClipRegion paintClip,
                PdfDictionary parentResources, GraphicsState parentState, int patternDepth)
            {
                if (paint.Shading is not null)
                {
                    GraphicsState shadingState = parentState with
                    {
                        Transform = paint.Matrix.Then(initial.Transform),
                        Clips = AddClip(parentState.Clips, paintClip.Mask),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null,
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    if (!TryRenderResolvedShading(paint.Shading, parentResources,
                        shadingState, pixels, options.Width, options.Height, scaleX, scaleY,
                        cancellationToken, out string? shadingDiagnostic)
                        && shadingDiagnostic is not null)
                        diagnostics.Add(shadingDiagnostic);
                    return;
                }

                PdfStream pattern = paint.Tiling!;
                PdfArray box = pattern.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                    ? ResolveArray(boxValue, 4, "Tiling pattern bounding box")
                    : throw new FormatException("A tiling pattern has no bounding box.");
                double left = Number(Resolve(box[0])), bottom = Number(Resolve(box[1]));
                double right = Number(Resolve(box[2])), top = Number(Resolve(box[3]));
                double xStep = PatternStep(pattern.Dictionary, "XStep");
                double yStep = PatternStep(pattern.Dictionary, "YStep");
                Matrix patternMatrix = paint.Matrix;
                Matrix patternToPage = patternMatrix.Then(initial.Transform);
                if (!patternToPage.TryInverse(out Matrix pageToPattern)) return;
                Point[] patternBounds = paintPath.SelectMany(points => points)
                    .Select(point => pageToPattern.Apply(point.X, point.Y)).ToArray();
                if (patternBounds.Length == 0) return;
                double stepX = Math.Abs(xStep), stepY = Math.Abs(yStep);
                int firstX = checked((int)Math.Floor(
                    (patternBounds.Min(point => point.X) - right) / stepX));
                int lastX = checked((int)Math.Ceiling(
                    (patternBounds.Max(point => point.X) - left) / stepX));
                int firstY = checked((int)Math.Floor(
                    (patternBounds.Min(point => point.Y) - top) / stepY));
                int lastY = checked((int)Math.Ceiling(
                    (patternBounds.Max(point => point.Y) - bottom) / stepY));
                long cellCount = checked((long)(lastX - firstX + 1)
                    * (lastY - firstY + 1));
                if (cellCount > 100_000)
                    throw new FormatException("A tiling pattern requires too many cells.");
                PdfDictionary patternResources = pattern.Dictionary.TryGetValue(Name("Resources"),
                    out PdfObject? resourcesValue)
                    ? Resolve(resourcesValue) as PdfDictionary
                        ?? throw new FormatException("Tiling pattern resources are not a dictionary.")
                    : parentResources;
                IReadOnlyList<PdfContentInstruction> patternInstructions =
                    ReadStreamInstructions(pattern, cancellationToken);
                IReadOnlyList<ClipRegion> paintClips = AddClip(parentState.Clips, paintClip.Mask);
                if (paintClips[0].Mask.IsEmpty) return;
                for (int cellY = firstY; cellY <= lastY; cellY++)
                    for (int cellX = firstX; cellX <= lastX; cellX++)
                    {
                        Matrix cell = new Matrix(1, 0, 0, 1,
                            cellX * stepX, cellY * stepY).Then(patternToPage);
                        Point[] cellBox =
                        [
                            cell.Apply(left, bottom), cell.Apply(right, bottom),
                            cell.Apply(right, top), cell.Apply(left, top),
                            cell.Apply(left, bottom)
                        ];
                        CoverageMask cellMask = RasterizeFill([cellBox], false, frame);
                        IReadOnlyList<ClipRegion> clips = AddClip(paintClips, cellMask);
                        if (clips[0].Mask.IsEmpty) continue;
                        GraphicsState cellState = parentState with
                        {
                            Transform = cell,
                            Clips = clips,
                            FillPatternSpace = false,
                            FillPatternBase = null,
                            FillPattern = null,
                            StrokePatternSpace = false,
                            StrokePatternBase = null,
                            StrokePattern = null
                        };
                        if (paint.BaseColor.HasValue)
                            cellState = cellState with
                            {
                                Fill = paint.BaseColor.Value,
                                Stroke = paint.BaseColor.Value
                            };
                        Process(patternInstructions, patternResources, cellState,
                            patternDepth + 1, beginKnockoutObjects: false);
                    }
            }

            double PatternStep(PdfDictionary dictionary, string key)
            {
                if (!dictionary.TryGetValue(Name(key), out PdfObject? value))
                    throw new FormatException($"A tiling pattern has no /{key} value.");
                double step = Number(Resolve(value));
                if (!double.IsFinite(step) || step == 0)
                    throw new FormatException($"A tiling pattern has an invalid /{key} value.");
                return step;
            }

            void MoveToNextLine()
            {
                textLineMatrix = new Matrix(1, 0, 0, 1, 0, -textLeading)
                    .Then(textLineMatrix);
                textMatrix = textLineMatrix;
            }

            void AdvanceText(double distance) => AdvanceTextVector(distance, 0);

            void AdvanceTextVector(double x, double y) =>
                textMatrix = new Matrix(1, 0, 0, 1, x, y).Then(textMatrix);

            bool IsVerticalText()
            {
                if (textFont is null || NameValue(textFont, "Subtype") == "Type3")
                    return false;
                extractionFont ??= ReadFont(textFont);
                return extractionFont.IsVertical;
            }

            void ShowText(PdfString text)
            {
                if (textFont is null)
                {
                    if (textRenderingMode == 3)
                        return;
                    diagnostics.Add("Text rendering is not implemented.");
                    return;
                }
                if (textSize == 0) return;
                if (NameValue(textFont, "Subtype") != "Type3")
                {
                    ShowOutlineText(text);
                    return;
                }
                string[] encoding = ReadType3Encoding(textFont);
                PdfDictionary charProcs = textFont.TryGetValue(Name("CharProcs"),
                    out PdfObject? charProcsValue) && Resolve(charProcsValue) is PdfDictionary procs
                    ? procs : throw new FormatException("A Type 3 font has no CharProcs dictionary.");
                Matrix fontMatrix = textFont.TryGetValue(Name("FontMatrix"),
                    out PdfObject? fontMatrixValue)
                    ? Matrix.From(ResolveArray(fontMatrixValue, 6, "Type 3 font matrix"))
                    : new Matrix(0.001, 0, 0, 0.001, 0, 0);
                PdfDictionary fontResources = textFont.TryGetValue(Name("Resources"),
                    out PdfObject? fontResourcesValue)
                    ? Resolve(fontResourcesValue) as PdfDictionary
                        ?? throw new FormatException("Type 3 font resources are not a dictionary.")
                    : resources;
                bool paintsType3 = textRenderingMode is >= 0 and <= 2 or >= 4 and <= 6;
                bool clipsType3 = textRenderingMode is >= 4 and <= 7;
                if (textRenderingMode is < 0 or > 7)
                    diagnostics.Add("Text rendering is not implemented.");
                foreach (byte code in text.Bytes.Span)
                {
                    string glyphName = encoding[code];
                    if ((paintsType3 || clipsType3) && glyphName.Length > 0
                        && charProcs.TryGetValue(Name(glyphName), out PdfObject? glyphValue)
                        && Resolve(glyphValue) is PdfStream glyph)
                    {
                        Matrix textScale = new(textSize * horizontalScale, 0, 0,
                            textSize, 0, textRise);
                        GraphicsState glyphState = state with
                        {
                            Transform = fontMatrix.Then(textScale).Then(textMatrix)
                                .Then(state.Transform)
                        };
                        IReadOnlyList<PdfContentInstruction> glyphInstructions;
                        try { glyphInstructions = ReadStreamInstructions(glyph, cancellationToken); }
                        catch (PdfFilterException) when (_document.UsesCompatibilityRecovery)
                        {
                            diagnostics.Add("An undecodable Type 3 glyph was omitted.");
                            glyphInstructions = [];
                        }
                        if (paintsType3)
                            Process(glyphInstructions, fontResources, glyphState, depth + 1,
                                beginKnockoutObjects: false);
                        if (clipsType3)
                        {
                            textClipMask ??= new byte[checked(options.Width * options.Height * 4)];
                            RasterSurface pagePixels = pixels;
                            try
                            {
                                pixels = new RasterSurface(textClipMask, 0, 0, options.Width, options.Height);
                                Process(glyphInstructions, fontResources, glyphState with
                                {
                                    FillAlpha = 1,
                                    StrokeAlpha = 1,
                                    BlendMode = RendererBlendMode.Normal,
                                    GraphicsSoftMask = null,
                                    Knockout = null
                                }, depth + 1, beginKnockoutObjects: false);
                            }
                            finally
                            {
                                pixels = pagePixels;
                            }
                        }
                    }
                    double width = Type3Width(textFont, code);
                    Point widthOrigin = fontMatrix.Apply(0, 0);
                    Point widthEnd = fontMatrix.Apply(width, 0);
                    double spacing = characterSpacing + (code == 32 ? wordSpacing : 0);
                    AdvanceTextVector(((widthEnd.X - widthOrigin.X) * textSize + spacing)
                        * horizontalScale, (widthEnd.Y - widthOrigin.Y) * textSize);
                }
            }

            void ShowOutlineText(PdfString text)
            {
                try
                {
                    extractionFont ??= ReadFont(textFont!);
                    diagnostics.UnionWith(extractionFont.Diagnostics);
                    if (textRenderingMode is < 0 or > 7)
                    {
                        diagnostics.Add("Text rendering is not implemented.");
                        return;
                    }
                    IReadOnlyList<PdfDecodedCharacter> characters =
                        _document.UsesCompatibilityRecovery
                            ? extractionFont.DecodeWithCompatibilityRecovery(text.Bytes)
                            : extractionFont.Decode(text.Bytes);
                    foreach (PdfDecodedCharacter character in characters)
                    {
                        PdfVerticalGlyphMetrics vertical =
                            extractionFont.GetVerticalMetrics(character.Code);
                        double originX = extractionFont.IsVertical
                            ? -vertical.OriginX / 1000 * textSize * horizontalScale : 0;
                        double originY = extractionFont.IsVertical
                            ? -vertical.OriginY / 1000 * textSize : 0;
                        Matrix textScale = new(textSize * horizontalScale / 1000, 0, 0,
                            textSize / 1000, originX, originY + textRise);
                        Matrix glyphTransform = textScale.Then(textMatrix).Then(state.Transform);
                        PdfGlyphOutline? outline;
                        try { outline = extractionFont.GetGlyphOutline(character.Code); }
                        catch (PdfFontResourceReader.GlyphRecursionException)
                            when (_document.UsesCompatibilityRecovery)
                        {
                            diagnostics.Add("A cyclic or excessively nested text glyph was omitted.");
                            outline = new PdfGlyphOutline([]);
                        }
                        int paintMode = textRenderingMode % 4;
                        bool clipsText = textRenderingMode >= 4;
                        if ((paintMode != 3 || clipsText) && outline is not null)
                        {
                            CoverageMask? cachedFill = paintMode is 0 or 2 || clipsText
                                ? TryCachedGlyphFill(outline, glyphTransform, frame) : null;
                            IReadOnlyList<List<Point>>? glyphPaths = null;
                            if (paintMode is 0 or 2)
                            {
                                if (cachedFill is not null)
                                    PaintCoverage(pixels, options.Width, options.Height,
                                        cachedFill, state.PaintFill, state.FillAlpha,
                                        state.BlendMode, state.Clips, state.GraphicsSoftMask,
                                        state.Knockout, cancellationToken);
                                else
                                    FillPaths(pixels, options.Width,
                                        options.Height, scaleX, scaleY,
                                        glyphPaths ??= FlattenGlyphOutline(outline, glyphTransform),
                                        state.PaintFill, state.FillAlpha, false,
                                        state.BlendMode, state.Clips, state.GraphicsSoftMask,
                                        state.Knockout,
                                        cancellationToken);
                            }
                            if (paintMode is 1 or 2)
                                StrokePaths(pixels, options.Width,
                                    options.Height, scaleX, scaleY,
                                    glyphPaths ??= FlattenGlyphOutline(outline, glyphTransform),
                                    state.PaintStroke, state.StrokeAlpha,
                                    state.LineWidth * state.Transform.StrokeScale,
                                    state.LineCap, state.LineJoin, state.MiterLimit,
                                    state.BlendMode, state.Clips, state.GraphicsSoftMask,
                                    state.Knockout,
                                    cancellationToken);
                            if (clipsText)
                            {
                                textClipCoverage ??= new byte[options.Width * options.Height];
                                if (cachedFill is not null)
                                    UnionInto(textClipCoverage, options.Width, cachedFill);
                                else
                                {
                                    CoverageMask glyphMask = RasterizeFill(
                                        glyphPaths ??= FlattenGlyphOutline(outline, glyphTransform),
                                        false, frame, rent: true);
                                    UnionInto(textClipCoverage, options.Width, glyphMask);
                                    glyphMask.Return();
                                }
                            }
                        }
                        else if ((paintMode != 3 || clipsText)
                            && extractionFont.GetWidth(character.Code) != 0)
                        {
                            diagnostics.Add("A text glyph outline is not implemented.");
                            diagnostics.Add($"Text outlines for font "
                                + $"{extractionFont.FontName} are not available.");
                        }
                        double spacing = characterSpacing
                            + (character.Code == 32 && character.ByteLength == 1 ? wordSpacing : 0);
                        if (extractionFont.IsVertical)
                            AdvanceTextVector(0, vertical.Advance / 1000 * textSize + spacing);
                        else
                            AdvanceText((extractionFont.GetWidth(character.Code) / 1000
                                * textSize + spacing) * horizontalScale);
                    }
                }
                catch (NotSupportedException)
                {
                    diagnostics.Add("Text rendering is not implemented.");
                }
            }

            GraphicsSoftMask? ReadGraphicsSoftMask(PdfObject value,
                PdfDictionary inheritedResources, GraphicsState currentState, int currentDepth)
            {
                PdfObject resolved = Resolve(value);
                if (resolved is PdfName name && name.ValueAsLatin1() == "None") return null;
                if (resolved is not PdfDictionary dictionary
                    || !dictionary.TryGetValue(Name("S"), out PdfObject? subtypeValue)
                    || Resolve(subtypeValue) is not PdfName subtype
                    || subtype.ValueAsLatin1() is not ("Alpha" or "Luminosity")
                    || !dictionary.TryGetValue(Name("G"), out PdfObject? groupValue)
                    || Resolve(groupValue) is not PdfStream group
                    || !IsName(group.Dictionary, "Subtype", "Form"))
                {
                    diagnostics.Add("Graphics-state soft-mask rendering is not implemented.");
                    return null;
                }

                Point[]? maskBounds = null;
                if (group.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boundsValue))
                {
                    PdfArray box = ResolveArray(boundsValue, 4, "Soft-mask group bounding box");
                    Matrix transform = group.Dictionary.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
                        ? Matrix.From(ResolveArray(matrixValue, 6, "Soft-mask group matrix"))
                            .Then(currentState.Transform)
                        : currentState.Transform;
                    double x0 = Number(Resolve(box[0])), y0 = Number(Resolve(box[1]));
                    double x1 = Number(Resolve(box[2])), y1 = Number(Resolve(box[3]));
                    maskBounds = [transform.Apply(x0, y0), transform.Apply(x1, y0),
                        transform.Apply(x1, y1), transform.Apply(x0, y1)];
                }
                (int maskLeft, int maskTop, int maskRight, int maskBottom) = GetRasterBounds(
                    currentState.Clips, maskBounds, options.Width, options.Height, scaleX, scaleY);
                int maskWidth = maskRight - maskLeft;
                int maskHeight = maskBottom - maskTop;
                RasterSurface pagePixels = pixels;
                RasterSurface maskPixels = RasterSurface.Rent((maskLeft, maskTop, maskRight, maskBottom));
                bool luminosity = subtype.ValueAsLatin1() == "Luminosity";
                bool deviceLuminosity = true;
                Color backdrop = luminosity ? ReadBackdrop(dictionary, group, out deviceLuminosity) : Color.White;
                double Luminosity(Color color)
                {
                    if (!deviceLuminosity && color.Connection is { } connection)
                        return Math.Clamp(connection.Y, 0, 1);
                    if (deviceLuminosity && color.Ink is uint ink) color = InkColor(ink);
                    return (0.3 * color.Red + 0.59 * color.Green + 0.11 * color.Blue) / 255d;
                }
                try
                {
                    System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(
                        maskPixels.Data.AsSpan(0, maskPixels.Length)).Fill(
                            backdrop.Blue | (uint)backdrop.Green << 8 | (uint)backdrop.Red << 16
                            | (luminosity ? 0xFF000000u : 0u));
                    PdfColorTransform? maskProfile = ReadGroupProfile(group.Dictionary, inheritedResources, diagnostics);
                    if (CmykGroup(group.Dictionary, inheritedResources, false)) maskPixels.EnableInk(backdrop,
                        profile: maskProfile);
                    else if (maskProfile is { Components: 1 or 3 }) maskPixels.EnableRgb(backdrop, maskProfile);
                    try
                    {
                        pixels = maskPixels;
                        RenderForm(group, inheritedResources,
                            currentState with
                            {
                                FillAlpha = 1,
                                StrokeAlpha = 1,
                                BlendMode = RendererBlendMode.Normal,
                                GraphicsSoftMask = null,
                                Knockout = null
                            }, currentDepth);
                    }
                    finally
                    {
                        pixels = pagePixels;
                    }
                    int sampleCount = checked(maskWidth * maskHeight);
                    byte[]? samples = null;
                    byte constant = 0;
                    Func<double, Color>? transfer = null;
                    if (dictionary.TryGetValue(Name("TR"), out PdfObject? transferValue))
                    {
                        PdfObject resolvedTransfer = Resolve(transferValue);
                        if (resolvedTransfer is not PdfName transferName
                            || transferName.ValueAsLatin1() != "Identity")
                            transfer = ReadColorFunction(transferValue,
                                new ImageColorSpace(1, null), "soft-mask transfer function");
                    }
                    byte ConvertSample(double sample) => transfer is null
                        ? (byte)Math.Round(sample * 255) : transfer(sample).Red;
                    byte outside = maskWidth == options.Width && maskHeight == options.Height
                        ? (byte)0 : ConvertSample(luminosity
                            ? Luminosity(backdrop)
                            : 0);
                    bool plainRgbMask = maskPixels.Ink is null && maskPixels.RgbProfile is null;
                    bool plainInkMask = maskPixels.Ink is not null && maskPixels.InkProfile is null
                        && deviceLuminosity;
                    if ((plainRgbMask || plainInkMask) && transfer is null)
                    {
                        // Plain RGB or unprofiled ink mask surface: read the bytes directly. The
                        // arithmetic is the same as the general loop below, without per-pixel
                        // color objects. Ink pixels convert with InkColor's integer math.
                        byte[] maskData = maskPixels.Data;
                        for (int row = 0; row < maskHeight; row++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            int rowIndex = row * maskWidth;
                            for (int column = 0; column < maskWidth; column++)
                            {
                                int index = rowIndex + column;
                                int offset = index * 4;
                                byte converted;
                                if (!luminosity)
                                {
                                    // Alpha masks round-trip exactly: Round(a / 255 * 255) is a.
                                    converted = plainRgbMask ? maskData[offset + 3] : maskPixels.Alpha(offset);
                                }
                                else if (plainRgbMask)
                                {
                                    converted = (byte)Math.Round((0.3 * maskData[offset + 2]
                                        + 0.59 * maskData[offset + 1] + 0.11 * maskData[offset]) / 255d * 255);
                                }
                                else
                                {
                                    int light = 255 - maskData[offset + 3];
                                    int red = ((255 - maskData[offset]) * light + 127) / 255;
                                    int green = ((255 - maskData[offset + 1]) * light + 127) / 255;
                                    int blue = ((255 - maskData[offset + 2]) * light + 127) / 255;
                                    converted = (byte)Math.Round((0.3 * red + 0.59 * green + 0.11 * blue) / 255d * 255);
                                }
                                if (index == 0) constant = converted;
                                if (samples is null && converted != constant)
                                {
                                    samples = new byte[sampleCount];
                                    samples.AsSpan(0, index).Fill(constant);
                                }
                                if (samples is not null) samples[index] = converted;
                            }
                        }
                    }
                    else
                    for (int y = maskTop; y < maskBottom; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (int x = maskLeft; x < maskRight; x++)
                        {
                            int offset = maskPixels.Offset(x, y);
                            Color color = luminosity ? deviceLuminosity && maskPixels.Ink is not null
                                ? InkColor(ReadInk(maskPixels.Ink, offset)) : maskPixels.ReadColor(offset) : default;
                            double sample = luminosity
                                ? Luminosity(color)
                                : maskPixels.Alpha(offset) / 255d;
                            int index = (y - maskTop) * maskWidth + x - maskLeft;
                            byte converted = ConvertSample(sample);
                            if (index == 0) constant = converted;
                            if (samples is null && converted != constant)
                            {
                                samples = new byte[sampleCount];
                                samples.AsSpan(0, index).Fill(constant);
                            }
                            if (samples is not null) samples[index] = converted;
                        }
                    }
                    return new GraphicsSoftMask(samples, maskLeft, maskTop, maskWidth, maskHeight,
                        outside, constant);
                }
                finally
                {
                    pixels = pagePixels;
                    maskPixels.Return();
                }

                Color ReadBackdrop(PdfDictionary source, PdfStream maskGroup, out bool device)
                {
                    PdfDictionary groupResources = maskGroup.Dictionary.TryGetValue(
                        Name("Resources"), out PdfObject? resourcesValue)
                        ? Resolve(resourcesValue) as PdfDictionary
                            ?? throw new FormatException("Soft-mask group resources are not a dictionary.")
                        : inheritedResources;
                    ImageColorSpace colorSpace = maskGroup.Dictionary.TryGetValue(
                            Name("Group"), out PdfObject? attributesValue)
                        && Resolve(attributesValue) is PdfDictionary attributes
                        && attributes.TryGetValue(Name("CS"), out PdfObject? colorSpaceValue)
                            ? ReadColorSpace(colorSpaceValue, groupResources, 0)
                            : new ImageColorSpace(1, null);
                    device = !colorSpace.IsIccBased && colorSpace.Converter is null && colorSpace.MultiConverter is null;
                    if (!source.TryGetValue(Name("BC"), out PdfObject? backdropValue))
                        return colorSpace.Convert(new double[colorSpace.Components]);
                    PdfArray array = ResolveArray(backdropValue, colorSpace.Components,
                        "Soft-mask backdrop color");
                    return colorSpace.Convert(array.Select(item =>
                        Number(Resolve(item))).ToArray());
                }
            }

        }

        void RenderForm(PdfStream form, PdfDictionary inheritedResources,
            GraphicsState parentState, int depth)
        {
            bool addedForm = activeForms.Add(form);
            if (!addedForm)
            {
                if (!_document.UsesCompatibilityRecovery)
                    throw new FormatException("Cyclic Form XObject.");
                if (depth >= PdfPageContentReader.MaximumRecoveredFormDepth
                    || recoveredFormExpansions >= PdfPageContentReader.MaximumRecoveredFormExpansions)
                {
                    diagnostics.Add("Cyclic Form XObject expansion limit reached.");
                    return;
                }
                recoveredFormExpansions++;
            }
            try
            {
                IReadOnlyList<PdfContentInstruction> instructions =
                    ReadStreamInstructions(form, cancellationToken);
                if (_document.UsesCompatibilityRecovery && instructions.Count == 0) return;
                Matrix matrix = form.Dictionary.TryGetValue(Name("Matrix"), out PdfObject? value)
                    ? Matrix.From(ResolveArray(value, 6, "Form XObject matrix")) : Matrix.Identity;
                GraphicsState formState = parentState with
                {
                    Transform = matrix.Then(parentState.Transform)
                };
                Point[]? formBounds = null;
                if (form.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue))
                {
                    PdfArray box = ResolveArray(boxValue, 4, "Form XObject bounding box");
                    double left = Number(Resolve(box[0])), bottom = Number(Resolve(box[1]));
                    double right = Number(Resolve(box[2])), top = Number(Resolve(box[3]));
                    Point[] polygon =
                    [
                        formState.Transform.Apply(left, bottom),
                        formState.Transform.Apply(right, bottom),
                        formState.Transform.Apply(right, top),
                        formState.Transform.Apply(left, top)
                    ];
                    formBounds = polygon;
                    formState = formState with
                    {
                        Clips = AddClip(formState.Clips, RasterizeFill([polygon], false, frame))
                    };
                }
                PdfDictionary formResources = form.Dictionary.TryGetValue(
                    Name("Resources"), out PdfObject? resourceValue)
                    ? Resolve(resourceValue) as PdfDictionary
                        ?? throw new FormatException("Form XObject resources are not a dictionary.")
                    : inheritedResources;
                PdfDictionary? group = form.Dictionary.TryGetValue(Name("Group"),
                        out PdfObject? groupValue)
                    ? Resolve(groupValue) as PdfDictionary : null;
                bool transparencyGroup = group is not null
                    && IsName(group, "S", "Transparency");
                bool isolated = transparencyGroup
                    && group!.TryGetValue(Name("I"), out PdfObject? isolatedValue)
                    && Resolve(isolatedValue) is PdfBoolean { Value: true };
                bool knockout = transparencyGroup
                    && group!.TryGetValue(Name("K"), out PdfObject? knockoutValue)
                    && Resolve(knockoutValue) is PdfBoolean { Value: true };
                bool multipleKnockoutObjects = knockout && !isolated
                    && instructions.Count(IsPaintingOperation) > 1;
                if (multipleKnockoutObjects
                    && (parentState.FillAlpha != parentState.StrokeAlpha
                        || parentState.BlendMode is not (RendererBlendMode.Normal
                            or RendererBlendMode.Compatible)
                        || parentState.GraphicsSoftMask is not null
                        || parentState.Knockout is not null))
                {
                    string reason = parentState.Knockout is not null ? "nested knockout groups"
                        : parentState.GraphicsSoftMask is not null ? "a soft mask"
                        : parentState.FillAlpha != parentState.StrokeAlpha
                            ? "different fill and stroke opacity"
                            : "a non-normal blend mode";
                    diagnostics.Add("Non-isolated transparency knockout-group rendering "
                        + $"with {reason} is not implemented.");
                    return;
                }
                if (multipleKnockoutObjects)
                {
                    if (parentState.FillAlpha == 1)
                    {
                        Process(instructions, formResources,
                            formState with
                            {
                                FillAlpha = 1,
                                StrokeAlpha = 1,
                                BlendMode = RendererBlendMode.Normal,
                                GraphicsSoftMask = null,
                                Knockout = new KnockoutState(
                                    options.Width, GetRasterBounds(formState.Clips, formBounds,
                                        options.Width, options.Height, scaleX, scaleY), pixels)
                            }, depth + 1);
                        return;
                    }

                    RasterSurface nonisolatedPagePixels = pixels;
                    RasterSurface nonisolatedGroupPixels = RasterSurface.Rent(GetRasterBounds(
                        formState.Clips, formBounds, options.Width, options.Height, scaleX, scaleY),
                        pixels.Ink is not null, pixels.BlendProfile);
                    var groupKnockout = new KnockoutState(
                        options.Width, GetRasterBounds(formState.Clips, formBounds,
                            options.Width, options.Height, scaleX, scaleY), nonisolatedPagePixels);
                    try
                    {
                        nonisolatedGroupPixels.CopyFrom(nonisolatedPagePixels);
                        pixels = nonisolatedGroupPixels;
                        Process(instructions, formResources,
                            formState with
                            {
                                FillAlpha = 1,
                                StrokeAlpha = 1,
                                BlendMode = RendererBlendMode.Normal,
                                GraphicsSoftMask = null,
                                Knockout = groupKnockout
                            }, depth + 1);
                        pixels = nonisolatedPagePixels;
                        (int left, int top, int right, int bottom) = GetRasterBounds(
                            formState.Clips, formBounds, options.Width, options.Height,
                            scaleX, scaleY);
                        for (int y = top; y < bottom; y++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            for (int x = left; x < right; x++)
                            {
                                int offset = (y * options.Width + x) * 4;
                                if (!groupKnockout.WasTouched(offset)) continue;
                                offset = nonisolatedGroupPixels.Offset(x, y);
                                SetPixel(nonisolatedPagePixels, options.Width, x, y,
                                    nonisolatedGroupPixels.ReadColor(offset, nonisolatedPagePixels),
                                    nonisolatedGroupPixels.Alpha(offset) / 255d
                                        * parentState.FillAlpha,
                                    parentState.BlendMode, null, null);
                            }
                        }
                    }
                    finally
                    {
                        pixels = nonisolatedPagePixels;
                        nonisolatedGroupPixels.Return();
                    }
                    return;
                }
                if (transparencyGroup && !isolated && !knockout
                    && (parentState.BlendMode is not (RendererBlendMode.Normal or RendererBlendMode.Compatible)
                        || (pixels.GroupAlpha is not null
                            && (parentState.FillAlpha != 1 || parentState.GraphicsSoftMask is not null))))
                {
                    RasterSurface backdropPixels = pixels;
                    RasterSurface blendedGroupPixels = RasterSurface.Rent(GetRasterBounds(
                        formState.Clips, formBounds, options.Width, options.Height, scaleX, scaleY),
                        pixels.Ink is not null, pixels.BlendProfile);
                    try
                    {
                        blendedGroupPixels.CopyFrom(backdropPixels);
                        blendedGroupPixels.TrackGroupAlpha();
                        pixels = blendedGroupPixels;
                        Process(instructions, formResources, formState with
                        {
                            FillAlpha = 1,
                            StrokeAlpha = 1,
                            BlendMode = RendererBlendMode.Normal,
                            GraphicsSoftMask = null,
                            Knockout = null
                        }, depth + 1);
                        pixels = backdropPixels;
                        for (int y = blendedGroupPixels.Top; y < blendedGroupPixels.Bottom; y++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            for (int x = blendedGroupPixels.Left; x < blendedGroupPixels.Right; x++)
                            {
                                int offset = blendedGroupPixels.Offset(x, y);
                                double alpha = blendedGroupPixels.GroupAlpha![offset / 4] / 255d;
                                if (alpha == 0) continue;
                                Color source = RemoveGroupBackdrop(blendedGroupPixels, offset,
                                    backdropPixels, backdropPixels.Offset(x, y), alpha);
                                SetPixel(backdropPixels, options.Width, x, y, source,
                                    alpha * parentState.FillAlpha, parentState.BlendMode,
                                    parentState.GraphicsSoftMask, parentState.Knockout);
                            }
                        }
                    }
                    finally
                    {
                        pixels = backdropPixels;
                        blendedGroupPixels.Return();
                    }
                    return;
                }
                if (transparencyGroup && !isolated && !knockout
                    && parentState.Knockout is null
                    && (parentState.GraphicsSoftMask is not null || parentState.FillAlpha != 1)
                    && parentState.BlendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible)
                {
                    // Outer opacity and masks apply to the completed group, not its individual objects.
                    // Keep the backdrop for internal blends, then interpolate premultiplied
                    // results once. This also preserves partially transparent backdrops.
                    RasterSurface backdropPixels = pixels;
                    RasterSurface maskedGroupPixels = RasterSurface.Rent(GetRasterBounds(
                        formState.Clips, formBounds, options.Width, options.Height, scaleX, scaleY),
                        pixels.Ink is not null, pixels.BlendProfile);
                    try
                    {
                        maskedGroupPixels.CopyFrom(backdropPixels);
                        pixels = maskedGroupPixels;
                        Process(instructions, formResources, formState with
                        {
                            FillAlpha = 1,
                            StrokeAlpha = 1,
                            BlendMode = RendererBlendMode.Normal,
                            GraphicsSoftMask = null
                        }, depth + 1);
                        pixels = backdropPixels;
                        (int left, int top, int right, int bottom) = GetRasterBounds(
                            formState.Clips, formBounds, options.Width, options.Height,
                            scaleX, scaleY);
                        if (backdropPixels.Ink is null && maskedGroupPixels.Ink is null)
                        {
                            // Same interpolation as the general loop below, reading the RGB
                            // surfaces and the soft mask by row instead of per-pixel lookups.
                            byte[] backdropData = backdropPixels.Data;
                            byte[] groupData = maskedGroupPixels.Data;
                            GraphicsSoftMask? softMask = parentState.GraphicsSoftMask;
                            double fillAlpha = parentState.FillAlpha;
                            for (int y = top; y < bottom; y++)
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                int backdropRow = backdropPixels.Offset(left, y);
                                int groupRow = maskedGroupPixels.Offset(left, y);
                                bool maskRowInside = softMask is not null
                                    && (uint)(y - softMask.Top) < (uint)softMask.Height;
                                int maskRow = maskRowInside ? (y - softMask!.Top) * softMask.Width - softMask.Left : 0;
                                for (int x = left; x < right; x++)
                                {
                                    int maskSample = softMask is null ? 255
                                        : !maskRowInside || (uint)(x - softMask.Left) >= (uint)softMask.Width
                                            ? softMask.Outside
                                            : softMask.Samples is null ? softMask.Constant : softMask.Samples[maskRow + x];
                                    double weight = fillAlpha * maskSample / 255d;
                                    if (weight <= 0) continue;
                                    int offset = backdropRow + (x - left) * 4;
                                    int groupOffset = groupRow + (x - left) * 4;
                                    double backdropAlpha = backdropData[offset + 3] * (1 - weight);
                                    double groupAlpha = groupData[groupOffset + 3] * weight;
                                    double alpha = backdropAlpha + groupAlpha;
                                    if (alpha <= 0) continue;
                                    for (int channel = 0; channel < 3; channel++)
                                        backdropData[offset + channel] = (byte)Math.Round(
                                            (backdropData[offset + channel] * backdropAlpha
                                                + groupData[groupOffset + channel] * groupAlpha) / alpha);
                                    backdropData[offset + 3] = (byte)Math.Round(alpha);
                                }
                            }
                        }
                        else
                        for (int y = top; y < bottom; y++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            for (int x = left; x < right; x++)
                            {
                                double weight = parentState.FillAlpha
                                    * (parentState.GraphicsSoftMask?.At(x, y) ?? 255) / 255d;
                                if (weight <= 0) continue;
                                int offset = backdropPixels.Offset(x, y);
                                int groupOffset = maskedGroupPixels.Offset(x, y);
                                double backdropAlpha = backdropPixels.Alpha(offset) * (1 - weight);
                                double groupAlpha = maskedGroupPixels.Alpha(groupOffset) * weight;
                                double alpha = backdropAlpha + groupAlpha;
                                if (alpha <= 0) continue;
                                if (backdropPixels.Ink is not null)
                                {
                                    for (int channel = 0; channel < 4; channel++)
                                        backdropPixels.Ink[offset + channel] = (byte)Math.Round(
                                            (backdropPixels.Ink[offset + channel] * backdropAlpha
                                                + maskedGroupPixels.Ink![groupOffset + channel] * groupAlpha) / alpha);
                                    backdropPixels.SetAlpha(offset, (byte)Math.Round(alpha));
                                    continue;
                                }
                                for (int channel = 0; channel < 3; channel++)
                                    backdropPixels[offset + channel] = (byte)Math.Round(
                                        (backdropPixels[offset + channel] * backdropAlpha
                                            + maskedGroupPixels[groupOffset + channel] * groupAlpha) / alpha);
                                backdropPixels[offset + 3] = (byte)Math.Round(alpha);
                            }
                        }
                    }
                    finally
                    {
                        pixels = backdropPixels;
                        maskedGroupPixels.Return();
                    }
                    return;
                }
                if (!isolated)
                {
                    Process(instructions, formResources, formState, depth + 1,
                        beginKnockoutObjects: parentState.Knockout is null);
                    return;
                }

                RasterSurface pagePixels = pixels;
                RasterSurface groupPixels = RasterSurface.Rent(GetRasterBounds(
                    formState.Clips, formBounds, options.Width, options.Height, scaleX, scaleY),
                    CmykGroup(form.Dictionary, formResources, pixels.Ink is not null),
                    ReadGroupProfile(form.Dictionary, formResources, diagnostics, pixels.BlendProfile));
                try
                {
                    (int left, int top, int right, int bottom) = GetRasterBounds(
                        formState.Clips, formBounds, options.Width, options.Height,
                        scaleX, scaleY);
                    Array.Clear(groupPixels.Data, 0, groupPixels.Length);
                    pixels = groupPixels;
                    Process(instructions, formResources,
                        formState with
                        {
                            FillAlpha = 1,
                            StrokeAlpha = 1,
                            BlendMode = RendererBlendMode.Normal,
                            GraphicsSoftMask = null,
                            Knockout = knockout
                                ? new KnockoutState(options.Width, (left, top, right, bottom)) : null
                        }, depth + 1);
                    pixels = pagePixels;
                    bool plainComposite = pagePixels.Ink is null && pagePixels.RgbProfile is null
                        && pagePixels.GroupAlpha is null && groupPixels.Ink is null
                        && groupPixels.RgbProfile is null && parentState.GraphicsSoftMask is null
                        && parentState.Knockout is null
                        && parentState.BlendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
                    for (int y = top; y < bottom; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (plainComposite)
                        {
                            // Same compositing as SetPixel for a plain RGB page: an opaque group
                            // pixel at full outer opacity replaces the page pixel outright, and
                            // every other pixel uses the ordinary RGB compositor directly.
                            byte[] groupData = groupPixels.Data;
                            byte[] pageData = pagePixels.Data;
                            int groupRow = groupPixels.Offset(left, y);
                            for (int x = left; x < right; x++)
                            {
                                int offset = groupRow + (x - left) * 4;
                                byte alpha = groupData[offset + 3];
                                if (alpha == 0 || !pagePixels.Contains(x, y)) continue;
                                var color = new Color(groupData[offset + 2], groupData[offset + 1], groupData[offset]);
                                if (color.DoesNotPaint) continue;
                                int pageOffset = pagePixels.Offset(x, y);
                                if (alpha == 255 && parentState.FillAlpha >= 1)
                                {
                                    pageData[pageOffset] = groupData[offset];
                                    pageData[pageOffset + 1] = groupData[offset + 1];
                                    pageData[pageOffset + 2] = groupData[offset + 2];
                                    pageData[pageOffset + 3] = 255;
                                    continue;
                                }
                                SetRgbPixel(pagePixels, pageOffset, color,
                                    Math.Clamp(alpha / 255d * parentState.FillAlpha, 0, 1),
                                    parentState.BlendMode);
                            }
                            continue;
                        }
                        for (int x = left; x < right; x++)
                        {
                            int offset = groupPixels.Offset(x, y);
                            byte alpha = groupPixels.Alpha(offset);
                            if (alpha == 0) continue;
                            SetPixel(pagePixels, options.Width, x, y,
                                groupPixels.ReadColor(offset, pagePixels),
                                alpha / 255d * parentState.FillAlpha, parentState.BlendMode,
                                parentState.GraphicsSoftMask, parentState.Knockout);
                        }
                    }
                }
                finally
                {
                    pixels = pagePixels;
                    groupPixels.Return();
                }
            }
            finally
            {
                if (addedForm) activeForms.Remove(form);
            }
        }

        static bool IsPaintingOperation(PdfContentInstruction instruction) =>
            instruction.Operator is "S" or "s" or "f" or "F" or "f*"
                or "B" or "B*" or "b" or "b*" or "Do" or "sh" or "BI"
                or "Tj" or "TJ" or "'" or "\"";

        void RenderAppearances()
        {
            PdfPageTreeEntry pageEntry = _tree.Pages[pageIndex];
            if (!pageEntry.Dictionary.TryGetValue(Name("Annots"), out PdfObject? annotationsValue)
                || Resolve(annotationsValue) is not PdfArray annotations)
                return;
            foreach (PdfObject annotationValue in annotations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Resolve(annotationValue) is not PdfDictionary annotation) continue;
                if (annotation.TryGetValue(Name("OC"), out PdfObject? optionalContent)
                    && !EvaluateOptionalContent(optionalContent,
                        hiddenOptionalContentGroups, 0)) continue;
                bool widget = IsName(annotation, "Subtype", "Widget");
                if (widget ? !options.IncludeFormFields : !options.IncludeAnnotations) continue;
                TryGetAppearance(annotation, out PdfStream? appearance);
                if (widget)
                {
                    try
                    {
                        appearance = RequestedFieldAppearance(annotation, appearance,
                            pageResources, diagnostics, cancellationToken);
                    }
                    catch (Exception error) when (_document.UsesCompatibilityRecovery
                        && error is FormatException or InvalidOperationException or NotSupportedException)
                    {
                        diagnostics.Add("Invalid form-field regeneration data retained the saved appearance.");
                    }
                }
                if (appearance is null) continue;
                if (!annotation.TryGetValue(Name("Rect"), out PdfObject? rectangleValue)) continue;
                PdfArray rectangle = ResolveArray(rectangleValue, 4, "Annotation rectangle");
                double rectangleLeft = Number(Resolve(rectangle[0]));
                double rectangleBottom = Number(Resolve(rectangle[1]));
                double rectangleRight = Number(Resolve(rectangle[2]));
                double rectangleTop = Number(Resolve(rectangle[3]));
                if (!appearance.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boundsValue))
                    continue;
                PdfArray bounds = ResolveArray(boundsValue, 4, "Appearance bounding box");
                double boundsLeft = Number(Resolve(bounds[0]));
                double boundsBottom = Number(Resolve(bounds[1]));
                double boundsRight = Number(Resolve(bounds[2]));
                double boundsTop = Number(Resolve(bounds[3]));
                Matrix appearanceMatrix = appearance.Dictionary.TryGetValue(
                    Name("Matrix"), out PdfObject? matrixValue)
                    ? Matrix.From(ResolveArray(matrixValue, 6, "Appearance matrix"))
                    : Matrix.Identity;
                Point[] transformed =
                [
                    appearanceMatrix.Apply(boundsLeft, boundsBottom),
                    appearanceMatrix.Apply(boundsRight, boundsBottom),
                    appearanceMatrix.Apply(boundsRight, boundsTop),
                    appearanceMatrix.Apply(boundsLeft, boundsTop)
                ];
                double left = transformed.Min(point => point.X);
                double bottom = transformed.Min(point => point.Y);
                double width = transformed.Max(point => point.X) - left;
                double height = transformed.Max(point => point.Y) - bottom;
                if (width <= 0 || height <= 0) continue;
                double scaleWidth = (rectangleRight - rectangleLeft) / width;
                double scaleHeight = (rectangleTop - rectangleBottom) / height;
                var placement = new Matrix(scaleWidth, 0, 0, scaleHeight,
                    rectangleLeft - left * scaleWidth,
                    rectangleBottom - bottom * scaleHeight);
                RenderForm(appearance, pageResources,
                    initialState with { Transform = placement.Then(initialState.Transform) }, 0);
            }
        }

        bool TryGetAppearance(PdfDictionary annotation, out PdfStream? appearance)
        {
            appearance = null;
            if (!annotation.TryGetValue(Name("AP"), out PdfObject? appearancesValue)
                || Resolve(appearancesValue) is not PdfDictionary appearances
                || !appearances.TryGetValue(Name("N"), out PdfObject? normalValue))
                return false;
            PdfObject normal = Resolve(normalValue);
            if (normal is PdfStream stream)
            {
                appearance = stream;
                return true;
            }
            if (normal is not PdfDictionary states) return false;
            if (annotation.TryGetValue(Name("AS"), out PdfObject? stateValue)
                && Resolve(stateValue) is PdfName stateName
                && states.TryGetValue(stateName, out PdfObject? selected)
                && Resolve(selected) is PdfStream selectedStream)
                appearance = selectedStream;
            return appearance is not null;
        }
    }

    private IReadOnlyList<PdfContentInstruction> ReadInstructions(
        int pageIndex, CancellationToken cancellationToken, ISet<string> diagnostics)
    {
        var parsed = _instructionCache.GetOrAdd(pageIndex, index =>
        {
            var recovered = new HashSet<string>();
            var instructions = _content.ReadInstructions(index, cancellationToken, recovered);
            return (instructions, recovered);
        });
        diagnostics.UnionWith(parsed.Diagnostics);
        return parsed.Instructions;
    }

    private PdfExtractionFont ReadFont(PdfDictionary font) =>
        _fontCache.GetOrAdd(font, value =>
            PdfFontResourceReader.Read(_document, value, _fontResolver));

    private IReadOnlyList<PdfContentInstruction> ReadStreamInstructions(
        PdfStream stream, CancellationToken cancellationToken) =>
        _streamInstructionCache.GetOrAdd(stream, value =>
        {
            byte[] bytes = _document.DecodeStream(value,
                PdfContentStreamReader.MaximumSourceBytes);
            return new ParsedStream(PdfContentStreamReader.Read(bytes,
                cancellationToken: cancellationToken,
                compatibilityRecovery: _document.UsesCompatibilityRecovery), bytes.Length);
        }).Instructions;

    private bool EvaluateOptionalContent(PdfObject value,
        IReadOnlySet<int> hiddenOptionalContentGroups, int expressionDepth,
        HashSet<(int ObjectNumber, int Generation)>? activeReferences = null)
    {
        if (expressionDepth > 32)
            throw new FormatException(
                "An optional-content visibility expression is too deeply nested.");
        PdfIndirectReference? reference = value as PdfIndirectReference;
        activeReferences ??= [];
        if (reference is not null
            && !activeReferences.Add((reference.ObjectNumber, reference.Generation)))
        {
            if (_document.UsesCompatibilityRecovery) return true;
            throw new FormatException(
                "An optional-content visibility expression contains a reference cycle.");
        }
        try
        {
            PdfObject resolved = Resolve(value);
            if (resolved is not PdfDictionary dictionary) return true;
            string type = NameValue(dictionary, "Type");
            if (type == "OCG")
                return reference is null
                    || !hiddenOptionalContentGroups.Contains(reference.ObjectNumber);
            if (type != "OCMD") return true;
            if (dictionary.TryGetValue(Name("VE"), out PdfObject? expression))
                return EvaluateVisibilityExpression(expression,
                    hiddenOptionalContentGroups, expressionDepth + 1, activeReferences);
            if (!dictionary.TryGetValue(Name("OCGs"), out PdfObject? groupsValue))
                throw new FormatException(
                    "An optional-content membership dictionary has no /OCGs or /VE value.");
            PdfObject groups = Resolve(groupsValue);
            bool[] states = groups is PdfArray array
                ? [.. array.Select(group => EvaluateOptionalContent(group,
                    hiddenOptionalContentGroups, expressionDepth + 1, activeReferences))]
                : [EvaluateOptionalContent(groupsValue,
                    hiddenOptionalContentGroups, expressionDepth + 1, activeReferences)];
            if (states.Length == 0)
                throw new FormatException(
                    "An optional-content membership dictionary has an empty /OCGs array.");
            string policy = dictionary.TryGetValue(Name("P"), out PdfObject? policyValue)
                && Resolve(policyValue) is PdfName policyName
                ? policyName.ValueAsLatin1() : "AnyOn";
            return policy switch
            {
                "AllOn" => states.All(visible => visible),
                "AnyOn" => states.Any(visible => visible),
                "AnyOff" => states.Any(visible => !visible),
                "AllOff" => states.All(visible => !visible),
                _ => throw new FormatException(
                    $"Optional-content membership policy /{policy} is not defined.")
            };
        }
        finally
        {
            if (reference is not null)
                activeReferences.Remove((reference.ObjectNumber, reference.Generation));
        }
    }

    private bool EvaluateVisibilityExpression(PdfObject value,
        IReadOnlySet<int> hiddenOptionalContentGroups, int expressionDepth,
        HashSet<(int ObjectNumber, int Generation)> activeReferences)
    {
        if (expressionDepth > 32)
            throw new FormatException(
                "An optional-content visibility expression is too deeply nested.");
        PdfArray expression = Resolve(value) as PdfArray
            ?? throw new FormatException(
                "An optional-content visibility expression is not an array.");
        if (expression.Count < 2 || Resolve(expression[0]) is not PdfName operation)
            throw new FormatException(
                "An optional-content visibility expression has no operator and operands.");
        bool[] states = [.. expression.Skip(1).Select(item =>
            Resolve(item) is PdfArray
                ? EvaluateVisibilityExpression(item,
                    hiddenOptionalContentGroups, expressionDepth + 1, activeReferences)
                : EvaluateOptionalContent(item,
                    hiddenOptionalContentGroups, expressionDepth + 1, activeReferences))];
        return operation.ValueAsLatin1() switch
        {
            "And" => states.All(visible => visible),
            "Or" => states.Any(visible => visible),
            "Not" when states.Length == 1 => !states[0],
            _ => throw new FormatException(
                "An optional-content visibility expression has an invalid operation.")
        };
    }

    private PdfDictionary PageResources(int pageIndex)
    {
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value))
            return new PdfDictionary([]);
        if (Resolve(value) is PdfDictionary resources) return resources;
        if (!_document.UsesCompatibilityRecovery)
            throw new FormatException("Page resources are not a dictionary.");
        _recoveredPageResources.Add(pageIndex);
        return new PdfDictionary([]);
    }

    private PdfDictionary? ResolveFont(PdfDictionary resources, PdfName resourceName) =>
        resources.TryGetValue(Name("Font"), out PdfObject? fontsValue)
        && Resolve(fontsValue) is PdfDictionary fonts
        && fonts.TryGetValue(resourceName, out PdfObject? fontValue)
        ? Resolve(fontValue) as PdfDictionary : null;

    private PdfDictionary? RecoverStandardFont(PdfName resourceName)
    {
        if (!_document.UsesCompatibilityRecovery
            || resourceName.ValueAsLatin1() is not ("Helvetica" or "Helvetica-Bold"
                or "Helvetica-Oblique" or "Helvetica-BoldOblique"
                or "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic"
                or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique"
                or "Symbol" or "ZapfDingbats"))
            return null;
        return _recoveredFontCache.GetOrAdd(resourceName, name => new PdfDictionary([
            new(Name("Type"), Name("Font")),
            new(Name("Subtype"), Name("Type1")),
            new(Name("BaseFont"), name)
        ]));
    }

    private string[] ReadType3Encoding(PdfDictionary font)
    {
        PdfObject? value = font.TryGetValue(Name("Encoding"), out PdfObject? encodingValue)
            ? Resolve(encodingValue) : null;
        string baseEncoding = value is PdfName encodingName
            ? encodingName.ValueAsLatin1()
            : value is PdfDictionary dictionary
                && dictionary.TryGetValue(Name("BaseEncoding"), out PdfObject? baseValue)
                && Resolve(baseValue) is PdfName baseName
                ? baseName.ValueAsLatin1() : "StandardEncoding";
        string[] names = PdfFontTables.EncodingNames(baseEncoding)
            ?? Enumerable.Repeat(string.Empty, 256).ToArray();
        if (value is PdfDictionary differencesDictionary
            && differencesDictionary.TryGetValue(Name("Differences"),
                out PdfObject? differencesValue)
            && Resolve(differencesValue) is PdfArray differences)
        {
            int code = -1;
            foreach (PdfObject item in differences)
            {
                PdfObject resolved = Resolve(item);
                if (resolved is PdfInteger number)
                    code = checked((int)number.Value);
                else if (resolved is PdfName name && code is >= 0 and < 256)
                    names[code++] = name.ValueAsLatin1();
                else throw new FormatException("A Type 3 font encoding is invalid.");
            }
        }
        return names;
    }

    private double Type3Width(PdfDictionary font, byte code)
    {
        int first = font.TryGetValue(Name("FirstChar"), out PdfObject? firstValue)
            && Resolve(firstValue) is PdfInteger firstInteger
            ? checked((int)firstInteger.Value) : 0;
        if (font.TryGetValue(Name("Widths"), out PdfObject? widthsValue)
            && Resolve(widthsValue) is PdfArray widths
            && code - first is int index && index >= 0 && index < widths.Count)
            return Number(Resolve(widths[index]));
        return 0;
    }

    private bool TryGetXObject(PdfDictionary resources, PdfName resourceName,
        out PdfStream? xObject)
    {
        xObject = resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            && Resolve(xObjectsValue) is PdfDictionary xObjects
            && xObjects.TryGetValue(resourceName, out PdfObject? value)
            && Resolve(value) is PdfStream stream ? stream : null;
        return xObject is not null;
    }

    private bool TryGetPattern(PdfDictionary resources, PdfName resourceName,
        out PatternPaint? pattern, out int paintType)
    {
        pattern = null;
        paintType = 0;
        if (!resources.TryGetValue(Name("Pattern"), out PdfObject? patternsValue)
            || Resolve(patternsValue) is not PdfDictionary patterns
            || !patterns.TryGetValue(resourceName, out PdfObject? value)) return false;
        PdfObject resolved = Resolve(value);
        PdfDictionary dictionary = resolved switch
        {
            PdfDictionary direct => direct,
            PdfStream stream => stream.Dictionary,
            _ => null!
        };
        if (dictionary is null
            || !dictionary.TryGetValue(Name("PatternType"), out PdfObject? typeValue)
            || Resolve(typeValue) is not PdfInteger type) return false;
        Matrix matrix = dictionary.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
            ? Matrix.From(ResolveArray(matrixValue, 6, "Pattern matrix"))
            : Matrix.Identity;
        if (type.Value == 2 && dictionary.TryGetValue(Name("Shading"),
                out PdfObject? shadingValue))
        {
            PdfObject shading = Resolve(shadingValue);
            if (shading is PdfDictionary or PdfStream)
            {
                pattern = new PatternPaint(null, shading, matrix, null);
                return true;
            }
            return false;
        }
        if (type.Value != 1 || resolved is not PdfStream tiling
            || !dictionary.TryGetValue(Name("PaintType"), out PdfObject? paintValue)
            || Resolve(paintValue) is not PdfInteger { Value: 1 or 2 } paint) return false;
        paintType = checked((int)paint.Value);
        pattern = new PatternPaint(tiling, null, matrix, null);
        return true;
    }

    private long AssertInteger(PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(value) is PdfInteger integer
            ? integer.Value : throw new FormatException($"A /{key} integer is missing.");

    private bool TryRenderImage(PdfStream stream, PdfDictionary resources, Matrix transform,
        IReadOnlyList<ClipRegion> clips, Color stencilColor, double stencilAlpha,
        RendererBlendMode blendMode, GraphicsSoftMask? graphicsSoftMask,
        KnockoutState? knockout, CancellationToken cancellationToken,
        RasterSurface target, int targetWidth, int targetHeight, double scaleX, double scaleY,
        out string? diagnostic, bool overprint = false)
    {
        diagnostic = null;
        if (_document.UsesCompatibilityRecovery
            && (!stream.Dictionary.TryGetValue(Name("Width"), out PdfObject? widthValue)
                || Resolve(widthValue) is not PdfInteger { Value: > 0 and <= int.MaxValue }
                || !stream.Dictionary.TryGetValue(Name("Height"), out PdfObject? heightValue)
                || Resolve(heightValue) is not PdfInteger { Value: > 0 and <= int.MaxValue }))
        {
            diagnostic = "An image with invalid pixel dimensions was skipped.";
            return false;
        }
        bool imageMask = stream.Dictionary.TryGetValue(Name("ImageMask"), out PdfObject? maskValue)
            && Resolve(maskValue) is PdfBoolean { Value: true };
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        bool jpeg2000 = IsSoleJpeg2000Filter(stream.Dictionary);
        bool jpeg = IsSoleDctFilter(stream.Dictionary);
        bool recoveredPng = _document.UsesCompatibilityRecovery
            && jpeg
            && PdfPngDecoder.HasSignature(stream.EncodedData.Span);
        jpeg &= !recoveredPng;
        int embeddedMaskMode = jpeg2000
            ? EmbeddedJpeg2000MaskMode(stream.Dictionary) : 0;
        Jpeg2000Shape? jpeg2000Shape = jpeg2000
            ? PdfJpeg2000Decoder.ReadShape(stream.EncodedData.Span) : null;
        int opacityChannel = jpeg2000Shape is Jpeg2000Shape channelShape
            ? PdfJpeg2000Decoder.ReadOpacityChannel(stream.EncodedData.Span, channelShape.Components) : -1;
        if (opacityChannel < 0 && embeddedMaskMode != 0)
            opacityChannel = jpeg2000Shape!.Value.Components - 1;
        int bits = imageMask ? 1
            : stream.Dictionary.ContainsKey(Name("BitsPerComponent"))
                ? PositiveInteger(stream.Dictionary, "BitsPerComponent")
                : jpeg2000Shape?.Bits
                    ?? PositiveInteger(stream.Dictionary, "BitsPerComponent");
        if (!imageMask && _document.UsesCompatibilityRecovery
            && jpeg2000Shape is Jpeg2000Shape sampleShape)
            bits = sampleShape.Bits;
        ImageColorSpace colorSpace;
        try
        {
            colorSpace = imageMask ? new ImageColorSpace(1, null)
                : stream.Dictionary.ContainsKey(Name("ColorSpace"))
                    ? ReadImageColorSpace(stream.Dictionary, resources)
                    : InferJpeg2000ColorSpace(jpeg2000Shape, opacityChannel);
        }
        catch (PdfFilterException error)
        {
            diagnostic = CompressionDiagnostic(error);
            return false;
        }
        catch (NotSupportedException)
        {
            diagnostic = "The image color space or sample depth is not implemented.";
            return false;
        }
        int components = colorSpace.Components;
        if (bits is not (1 or 2 or 4 or 8 or 16))
        {
            diagnostic = "The image color space or sample depth is not implemented.";
            return false;
        }
        byte[] samples;
        SoftMask? softMask = null;
        int sampleWidth = width, sampleHeight = height;
        double[]? preblendMatte = null;
        double[] decode;
        int[]? colorKeyMask = null;
        PdfStream? explicitMask = null;
        try
        {
            int encodedComponents = checked(components + (opacityChannel < 0 ? 0 : 1));
            if (jpeg2000Shape is Jpeg2000Shape shape
                && (shape.Width != width || shape.Height != height
                    || shape.Components != encodedComponents || shape.Bits != bits))
                throw new FormatException("JPEG 2000 image metadata does not match its codestream.");
            int resolutionLevel = jpeg2000Shape is Jpeg2000Shape jpxShape
                ? SelectJpeg2000ResolutionLevel(jpxShape, transform, scaleX, scaleY) : -1;
            int jpegReduction = jpeg
                ? SelectJpegReduction(width, height, transform, scaleX, scaleY) : 1;
            int cacheLevel = recoveredPng ? -2
                : jpeg ? -10 - jpegReduction
                : resolutionLevel;
            DecodedImage decodedImage = _imageCache.GetOrAdd(
                new ImageCacheKey(stream, cacheLevel), _ => DecodeImage());
            samples = decodedImage.Samples;
            sampleWidth = decodedImage.Width;
            sampleHeight = decodedImage.Height;
            softMask = decodedImage.Alpha is null
                ? null : new SoftMask(decodedImage.Alpha, sampleWidth, sampleHeight);

            DecodedImage DecodeImage()
            {
                if (recoveredPng)
                {
                    PngDecodedImage decoded = PdfPngDecoder.Decode(
                        stream.EncodedData, PdfStreamDecoder.DefaultMaximumDecodedBytes);
                    if (decoded.Width != width || decoded.Height != height
                        || decoded.Components != components || decoded.Bits != bits)
                        throw new FormatException("PNG image metadata does not match its PDF dictionary.");
                    return new DecodedImage(decoded.Samples, decoded.Width,
                        decoded.Height, decoded.Alpha);
                }
                if (jpeg2000Shape is not null)
                {
                    Jpeg2000DecodedImage decoded = PdfJpeg2000Decoder.DecodeImage(
                        stream.EncodedData, PdfStreamDecoder.DefaultMaximumDecodedBytes,
                        resolutionLevel);
                    byte[] colors = decoded.Samples;
                    SoftMask? alpha = opacityChannel >= 0
                        ? SeparateEmbeddedJpeg2000Alpha(ref colors, decoded.Width,
                            decoded.Height, components, bits, opacityChannel, embeddedMaskMode != 0)
                        : null;
                    return new DecodedImage(colors, decoded.Width, decoded.Height, alpha?.Samples);
                }
                if (jpeg)
                {
                    JpegDecodedImage decoded = _document.DecodeJpegImage(
                        stream, PdfStreamDecoder.DefaultMaximumDecodedBytes, jpegReduction);
                    int expectedWidth = checked((width + jpegReduction - 1) / jpegReduction);
                    int expectedHeight = checked((height + jpegReduction - 1) / jpegReduction);
                    bool sizeMismatch = decoded.SourceWidth != width || decoded.SourceHeight != height
                        || decoded.Width != expectedWidth || decoded.Height != expectedHeight;
                    if (sizeMismatch && _document.UsesCompatibilityRecovery
                        && decoded.Components == encodedComponents && bits == 8)
                    {
                        // Common viewers trust the JPEG frame header over the image dictionary.
                        return new DecodedImage(decoded.Samples, decoded.Width, decoded.Height);
                    }
                    if (sizeMismatch || decoded.Components != encodedComponents || bits != 8)
                        throw new FormatException(
                            "JPEG image metadata does not match its PDF dictionary.");
                    return new DecodedImage(decoded.Samples, decoded.Width, decoded.Height);
                }
                int rowBytes = checked((width * encodedComponents * bits + 7) / 8);
                int expected = checked(rowBytes * height);
                int limit = _document.UsesCompatibilityRecovery
                    ? Math.Max(expected, PdfStreamDecoder.DefaultMaximumDecodedBytes)
                    : expected;
                byte[] decodedSamples = _document.DecodeStream(stream, limit);
                int decodedHeight = height;
                if (_document.UsesCompatibilityRecovery && decodedSamples.Length > expected)
                    decodedSamples = decodedSamples[..expected];
                else if (_document.UsesCompatibilityRecovery && decodedSamples.Length > 0
                    && decodedSamples.Length < expected && decodedSamples.Length % rowBytes == 0)
                    decodedHeight = decodedSamples.Length / rowBytes;
                else if (_document.UsesCompatibilityRecovery && decodedSamples.Length > 0
                    && decodedSamples.Length < expected)
                {
                    // Truncated sample data: keep the whole rows that arrived and zero the rest,
                    // the way common viewers show a partially decoded image.
                    var padded = new byte[expected];
                    decodedSamples.CopyTo(padded, 0);
                    decodedSamples = padded;
                }
                else if (decodedSamples.Length != expected)
                    throw new FormatException("Image sample data has an invalid length.");
                return new DecodedImage(decodedSamples, width, decodedHeight);
            }
            if (softMask is null) softMask = ReadSoftMask(stream.Dictionary,
                transform, scaleX, scaleY, cancellationToken);
            if (softMask is null && stream.Dictionary.TryGetValue(Name("Mask"), out PdfObject? colorKeyValue))
            {
                PdfObject resolvedMask = Resolve(colorKeyValue);
                if (resolvedMask is PdfStream maskStream && !imageMask)
                    explicitMask = maskStream;
                else if (resolvedMask is PdfArray colorKey
                    && colorKey.Count == components * 2 && !imageMask)
                    colorKeyMask = colorKey.Select(item => Resolve(item) is PdfInteger integer
                            && integer.Value >= 0 && integer.Value <= (1 << bits) - 1
                            ? (int)integer.Value
                            : throw new FormatException("An image color-key mask range is invalid."))
                        .ToArray();
                else
                {
                    diagnostic = "Masked-image rendering is not implemented.";
                    return false;
                }
            }
            if (softMask is null && explicitMask is not null)
                softMask = ReadExplicitImageMask(explicitMask);
            decode = ReadImageDecode(stream.Dictionary, colorSpace, imageMask);
            if (embeddedMaskMode == 2)
                preblendMatte = ReadPreblendMatte(stream.Dictionary, colorSpace);
            else if (softMask is not null && stream.Dictionary.TryGetValue(Name("SMask"), out PdfObject? matteMaskValue)
                && Resolve(matteMaskValue) is PdfStream matteMask && matteMask.Dictionary.ContainsKey(Name("Matte")))
                preblendMatte = ReadPreblendMatte(matteMask.Dictionary, colorSpace);
        }
        catch (PdfFilterException error)
        {
            diagnostic = CompressionDiagnostic(error);
            return false;
        }
        catch (NotSupportedException)
        {
            diagnostic = explicitMask is null
                ? "The image soft mask is not implemented."
                : "Masked-image rendering is not implemented.";
            return false;
        }
        PaintImage(target, targetWidth, targetHeight, scaleX, scaleY,
            transform, samples, sampleWidth, sampleHeight,
            components, bits, clips,
            imageMask, imageMask && StencilPaintsOne(stream.Dictionary), softMask, decode,
            colorKeyMask, colorSpace, stencilColor, stencilAlpha, blendMode,
            cancellationToken, preblendMatte,
            softMask is not null || colorKeyMask is not null ? null : graphicsSoftMask, knockout, overprint);
        return true;
    }

    private static string CompressionDiagnostic(PdfFilterException error) =>
        $"The image compression filter is not implemented. {error.Message}";

    private static int SelectJpeg2000ResolutionLevel(
        Jpeg2000Shape shape, Matrix transform, double scaleX, double scaleY)
    {
        (int desiredWidth, int desiredHeight) = DesiredImageDimensions(
            transform, scaleX, scaleY);
        for (int level = 0; level < shape.ResolutionLevels; level++)
        {
            int reduction = shape.ResolutionLevels - 1 - level;
            int width = PdfJpeg2000Decoder.ReducedDimension(
                shape.XSize, shape.XOrigin, reduction);
            int height = PdfJpeg2000Decoder.ReducedDimension(
                shape.YSize, shape.YOrigin, reduction);
            if (width >= desiredWidth && height >= desiredHeight) return level;
        }
        return shape.ResolutionLevels - 1;
    }

    private static int SelectJpegReduction(
        int width, int height, Matrix transform, double scaleX, double scaleY)
    {
        (int desiredWidth, int desiredHeight) = DesiredImageDimensions(
            transform, scaleX, scaleY);
        ReadOnlySpan<int> reductions = [8, 4, 2];
        foreach (int reduction in reductions)
            if (checked((width + reduction - 1) / reduction) >= desiredWidth
                && checked((height + reduction - 1) / reduction) >= desiredHeight)
                return reduction;
        return 1;
    }

    private static (int Width, int Height) DesiredImageDimensions(
        Matrix transform, double scaleX, double scaleY)
    {
        Point[] corners =
        [
            transform.Apply(0, 0), transform.Apply(1, 0),
            transform.Apply(0, 1), transform.Apply(1, 1)
        ];
        return (
            Math.Max(1, (int)Math.Ceiling(
                (corners.Max(point => point.X) - corners.Min(point => point.X)) * scaleX)),
            Math.Max(1, (int)Math.Ceiling(
                (corners.Max(point => point.Y) - corners.Min(point => point.Y)) * scaleY)));
    }

    private int EmbeddedJpeg2000MaskMode(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("SMaskInData"), out PdfObject? value)) return 0;
        if (Resolve(value) is not PdfInteger { Value: 0 or 1 or 2 } mode)
            throw new FormatException("An image /SMaskInData value is invalid.");
        return checked((int)mode.Value);
    }

    private bool IsSoleJpeg2000Filter(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? value)) return false;
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray filters && filters.Count == 1) resolved = Resolve(filters[0]);
        return resolved is PdfName name && name.ValueAsLatin1() is "JPXDecode" or "JPX";
    }

    private bool IsSoleDctFilter(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("Filter"), out PdfObject? value)) return false;
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray filters && filters.Count == 1) resolved = Resolve(filters[0]);
        return resolved is PdfName name && name.ValueAsLatin1() is "DCTDecode" or "DCT";
    }

    private static ImageColorSpace InferJpeg2000ColorSpace(
        Jpeg2000Shape? shape, int opacityChannel)
    {
        int components = shape?.Components - (opacityChannel < 0 ? 0 : 1) ?? 0;
        return components switch
        {
            1 => new ImageColorSpace(1, null),
            3 => new ImageColorSpace(3, null),
            4 => new ImageColorSpace(4, null),
            _ => throw new NotSupportedException()
        };
    }

    private static SoftMask? SeparateEmbeddedJpeg2000Alpha(
        ref byte[] samples, int width, int height, int components, int bits,
        int opacityChannel, bool useOpacity)
    {
        int encodedComponents = checked(components + 1);
        int sourceRowBytes = checked((width * encodedComponents * bits + 7) / 8);
        int colorRowBytes = checked((width * components * bits + 7) / 8);
        var colors = new byte[checked(colorRowBytes * height)];
        byte[]? alpha = useOpacity ? new byte[checked(width * height)] : null;
        uint maximum = (1u << bits) - 1;
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                int sourcePixel = checked(y * sourceRowBytes * 8
                    + x * encodedComponents * bits);
                int colorPixel = checked(y * colorRowBytes * 8 + x * components * bits);
                for (int component = 0; component < components; component++)
                    WritePackedSample(colors, colorPixel + component * bits,
                        bits, ReadPackedSample(samples, sourcePixel
                            + (component < opacityChannel ? component : component + 1) * bits, bits));
                if (alpha is not null)
                {
                    uint opacity = ReadPackedSample(
                        samples, sourcePixel + opacityChannel * bits, bits);
                    alpha[y * width + x] = (byte)Math.Round(opacity / (double)maximum * 255);
                }
            }
        samples = colors;
        return alpha is null ? null : new SoftMask(alpha, width, height);
    }

    private double[] ReadPreblendMatte(PdfDictionary dictionary, ImageColorSpace colorSpace)
    {
        int components = (colorSpace.PaletteBase ?? colorSpace).Components;
        if (!dictionary.TryGetValue(Name("Matte"), out PdfObject? value)) return new double[components];
        PdfArray matte = ResolveArray(value, components, "Image matte array");
        return matte.Select(item => Number(Resolve(item))).ToArray();
    }

    private ImageColorSpace ReadImageColorSpace(
        PdfDictionary dictionary, PdfDictionary resources)
    {
        if (!dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value))
            throw new NotSupportedException();
        return ReadColorSpace(value, resources, 0);
    }

    private bool TryReadPatternColorSpace(PdfObject value, PdfDictionary resources,
        out ImageColorSpace? baseSpace, int depth = 0)
    {
        if (depth > 16) throw new FormatException("A pattern color-space reference is cyclic.");
        PdfObject resolved = Resolve(value);
        if (resolved is PdfName name)
        {
            if (name.ValueAsLatin1() == "Pattern")
            {
                baseSpace = null;
                return true;
            }
            if (resources.TryGetValue(Name("ColorSpace"), out PdfObject? spacesValue)
                && Resolve(spacesValue) is PdfDictionary spaces
                && spaces.TryGetValue(name, out PdfObject? namedValue))
                return TryReadPatternColorSpace(namedValue, resources, out baseSpace, depth + 1);
        }
        else if (resolved is PdfArray { Count: 1 or 2 } array
            && Resolve(array[0]) is PdfName kind
            && kind.ValueAsLatin1() == "Pattern")
        {
            baseSpace = array.Count == 2 ? ReadColorSpace(array[1], resources, depth + 1) : null;
            return true;
        }
        baseSpace = null;
        return false;
    }

    private ImageColorSpace ReadColorSpace(
        PdfObject value, PdfDictionary resources, int depth)
    {
        if (depth > 16) throw new FormatException("An image color-space reference is cyclic.");
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray { Count: 1 } deviceArray
            && Resolve(deviceArray[0]) is PdfName deviceName
            && deviceName.ValueAsLatin1() is "DeviceGray" or "DeviceRGB" or "DeviceCMYK")
            resolved = deviceName;
        if (resolved is PdfName name)
        {
            ImageColorSpace? standard = name.ValueAsLatin1() switch
            {
                "DeviceGray" or "G" => new ImageColorSpace(1, null),
                "DeviceRGB" or "RGB" => new ImageColorSpace(3, null),
                "DeviceCMYK" or "CMYK" => new ImageColorSpace(4, null, Profile: _outputProfile.Value,
                    Initial: InitialColor.BlackInk),
                _ => null
            };
            if (standard is not null) return standard;
            if (!resources.TryGetValue(Name("ColorSpace"), out PdfObject? spacesValue)
                || Resolve(spacesValue) is not PdfDictionary spaces
                || !spaces.TryGetValue(name, out PdfObject? namedValue))
                throw new NotSupportedException();
            return ReadColorSpace(namedValue, resources, depth + 1);
        }
        if (resolved is not PdfArray array || array.Count < 2
            || Resolve(array[0]) is not PdfName kind)
            throw new NotSupportedException();
        if (kind.ValueAsLatin1() == "ICCBased")
        {
            if (array.Count != 2 || Resolve(array[1]) is not PdfStream profile
                || !profile.Dictionary.TryGetValue(Name("N"), out PdfObject? countValue)
                || Resolve(countValue) is not PdfInteger count || count.Value is not (1 or 3 or 4))
                throw new FormatException("An ICCBased image color space is invalid.");
            PdfColorTransform? transform = ReadIccProfile(profile);
            double[]? componentRange = profile.Dictionary.TryGetValue(Name("Range"), out _)
                ? ReadCieArray(profile.Dictionary, "Range", required: true, defaultValues: [], count: (int)count.Value * 2)
                : null;
            if (componentRange is not null)
            {
                bool unitRange = true;
                for (int index = 0; index < componentRange.Length; index += 2)
                {
                    double minimum = componentRange[index], maximum = componentRange[index + 1];
                    if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                        throw new FormatException("An ICCBased component range is invalid.");
                    if (minimum != 0 || maximum != 1) unitRange = false;
                }
                if (unitRange) componentRange = null;
            }
            if (transform is not null && transform.Components == count.Value)
            {
                if (componentRange is null && transform is PdfIccProfileTransform { IsLabInput: true })
                    componentRange = [0, 1, 0, 1, 0, 1];
                return new ImageColorSpace((int)count.Value, null, DefaultDecode: componentRange,
                    Profile: transform, IsIccBased: true, ComponentRange: componentRange);
            }
            ImageColorSpace alternate = profile.Dictionary.TryGetValue(
                Name("Alternate"), out PdfObject? alternateValue)
                ? ReadColorSpace(alternateValue, resources, depth + 1)
                : count.Value switch
                {
                    1 => new ImageColorSpace(1, null),
                    3 => new ImageColorSpace(3, null),
                    _ => new ImageColorSpace(4, null, Profile: _outputProfile.Value)
                };
            if (alternate.Components != count.Value || alternate.Palette is not null)
                throw new FormatException("An ICCBased image alternate has the wrong component count.");
            double[]? effectiveRange = componentRange;
            if (alternate.ComponentRange is { } alternateRange)
            {
                effectiveRange = new double[count.Value * 2];
                for (int index = 0; index < effectiveRange.Length; index++)
                    effectiveRange[index] = Math.Clamp(componentRange?[index] ?? index % 2,
                        alternateRange[index / 2 * 2], alternateRange[index / 2 * 2 + 1]);
            }
            else if (effectiveRange is null && (alternate.Converter is not null || alternate.MultiConverter is not null))
                effectiveRange = Enumerable.Range(0, (int)count.Value * 2).Select(index => (double)(index % 2)).ToArray();
            return alternate with { DefaultDecode = componentRange, ComponentRange = effectiveRange,
                Initial = InitialColor.Zero };
        }
        if (kind.ValueAsLatin1() == "CalGray")
        {
            if (array.Count != 2 || Resolve(array[1]) is not PdfDictionary parameters)
                throw new FormatException("A CalGray image color space is invalid.");
            double[] whitePoint = ReadCieArray(parameters, "WhitePoint", required: true,
                defaultValues: []);
            ValidateWhitePoint(whitePoint, "CalGray");
            ReadAndValidateBlackPoint(parameters, "CalGray");
            double gamma = parameters.TryGetValue(Name("Gamma"), out PdfObject? gammaValue)
                ? Number(Resolve(gammaValue)) : 1;
            if (!double.IsFinite(gamma) || gamma <= 0)
                throw new FormatException("A CalGray image gamma value is invalid.");
            Func<double, double, double, Color> convertXyz = CreateXyzConverter(whitePoint);
            return new ImageColorSpace(1, null, (gray, _, _, _) =>
            {
                double adjusted = Math.Pow(gray, gamma);
                return convertXyz(whitePoint[0] * adjusted,
                    whitePoint[1] * adjusted, whitePoint[2] * adjusted);
            }, Profile: ReadCalibratedProfile(array, whitePoint, [gamma]));
        }
        if (kind.ValueAsLatin1() == "CalRGB")
        {
            if (array.Count != 2 || Resolve(array[1]) is not PdfDictionary parameters)
                throw new FormatException("A CalRGB image color space is invalid.");
            double[] whitePoint = ReadCieArray(parameters, "WhitePoint", required: true,
                defaultValues: []);
            ValidateWhitePoint(whitePoint, "CalRGB");
            ReadAndValidateBlackPoint(parameters, "CalRGB");
            double[] gamma = ReadCieArray(parameters, "Gamma", required: false,
                defaultValues: [1, 1, 1]);
            if (gamma.Any(value => !double.IsFinite(value) || value <= 0))
                throw new FormatException("A CalRGB image gamma array is invalid.");
            double[] matrix = ReadCieArray(parameters, "Matrix", required: false,
                defaultValues: [1, 0, 0, 0, 1, 0, 0, 0, 1], count: 9);
            if (matrix.Any(value => !double.IsFinite(value)))
                throw new FormatException("A CalRGB image matrix is invalid.");
            Func<double, double, double, Color> convertXyz = CreateXyzConverter(whitePoint);
            return new ImageColorSpace(3, null, (red, green, blue, _) =>
            {
                double a = Math.Pow(red, gamma[0]);
                double b = Math.Pow(green, gamma[1]);
                double c = Math.Pow(blue, gamma[2]);
                return convertXyz(matrix[0] * a + matrix[3] * b + matrix[6] * c,
                    matrix[1] * a + matrix[4] * b + matrix[7] * c,
                    matrix[2] * a + matrix[5] * b + matrix[8] * c);
            }, Profile: ReadCalibratedProfile(array, whitePoint, gamma, matrix));
        }
        if (kind.ValueAsLatin1() == "Lab")
        {
            if (array.Count != 2 || Resolve(array[1]) is not PdfDictionary parameters)
                throw new FormatException("A Lab image color space is invalid.");
            double[] whitePoint = ReadCieArray(parameters, "WhitePoint", required: true,
                defaultValues: []);
            ValidateWhitePoint(whitePoint, "Lab");
            ReadAndValidateBlackPoint(parameters, "Lab");
            double[] range = ReadCieArray(parameters, "Range", required: false,
                defaultValues: [-100, 100, -100, 100], count: 4);
            if (range.Any(value => !double.IsFinite(value))
                || range[0] >= range[1] || range[2] >= range[3])
                throw new FormatException("A Lab image range is invalid.");
            Func<double, double, double, Color> convertXyz = CreateXyzConverter(whitePoint);
            double[] componentRange = [0, 100, range[0], range[1], range[2], range[3]];
            return new ImageColorSpace(3, null, (lightness, a, b, _) =>
            {
                double fy = (lightness + 16) / 116;
                double fx = fy + a / 500;
                double fz = fy - b / 200;
                return convertXyz(whitePoint[0] * LabInverse(fx),
                    whitePoint[1] * LabInverse(fy), whitePoint[2] * LabInverse(fz));
            }, componentRange, ComponentRange: componentRange);
        }
        if (kind.ValueAsLatin1() == "Separation")
        {
            if (array.Count != 4 || Resolve(array[1]) is not PdfName colorant
                || ReadColorSpace(array[2], resources, depth + 1) is not { Palette: null } alternate)
                throw new FormatException("A Separation image color space is invalid.");
            Func<double, Color> tintTransform = ReadColorFunction(
                array[3], alternate, "Separation tint transform");
            int channel = ProcessChannel(colorant.ValueAsLatin1());
            if (colorant.ValueAsLatin1() == "All")
                return new ImageColorSpace(1, null, (tint, _, _, _) => Color.Gray(1 - tint),
                    RegistrationColor: true, Initial: InitialColor.FullTint);
            return new ImageColorSpace(1, null,
                (tint, _, _, _) => tintTransform(tint),
                ProcessChannels: channel >= 0 ? [channel] : null, SuppressPainting: channel == -1,
                Initial: InitialColor.FullTint);
        }
        if (kind.ValueAsLatin1() == "DeviceN")
        {
            if (array.Count is not (4 or 5) || Resolve(array[1]) is not PdfArray names
                || names.Count is < 1 or > 32
                || names.Any(item => Resolve(item) is not PdfName))
                throw new FormatException("A DeviceN image color space is invalid.");
            ImageColorSpace alternate = ReadColorSpace(array[2], resources, depth + 1);
            if (alternate.Palette is not null)
                throw new FormatException("A DeviceN image alternate color space is invalid.");
            Func<double[], Color> tintTransform = ReadMultidimensionalColorFunction(
                array[3], names.Count, alternate, "DeviceN tint transform");
            int[] channels = names.Select(item => ProcessChannel(((PdfName)Resolve(item)).ValueAsLatin1())).ToArray();
            int activeChannels = channels.Count(channel => channel >= 0);
            bool supported = activeChannels > 0 && channels.All(channel => channel >= -1)
                && channels.Where(channel => channel >= 0).Distinct().Count() == activeChannels;
            return new ImageColorSpace(names.Count, null, MultiConverter: tintTransform,
                ProcessChannels: supported ? channels : null, SuppressPainting: channels.All(channel => channel == -1),
                Initial: InitialColor.FullTint);
        }
        if (kind.ValueAsLatin1() != "Indexed" || array.Count != 4
            || Resolve(array[2]) is not PdfInteger highValue
            || highValue.Value is < 0 or > 255)
            throw new NotSupportedException();
        ImageColorSpace baseSpace = ReadColorSpace(array[1], resources, depth + 1);
        if (baseSpace.Palette is not null) throw new NotSupportedException();
        int baseComponents = baseSpace.Components;
        int entryCount = (int)highValue.Value + 1;
        int expected = checked(entryCount * baseComponents);
        byte[] lookup = Resolve(array[3]) switch
        {
            PdfString text => text.Bytes.ToArray(),
            PdfStream stream => _document.DecodeStream(stream, checked(expected + 1)),
            _ => throw new NotSupportedException()
        };
        if (lookup.Length < expected)
            throw new FormatException("An Indexed image color lookup is truncated.");
        Color[] palette = ImageColorSpace.BuildPalette(baseSpace, lookup, entryCount);
        return new ImageColorSpace(1, palette, PaletteBase: baseSpace, PaletteSamples: lookup);
    }

    private double[] ReadCieArray(PdfDictionary dictionary, string key, bool required,
        double[] defaultValues, int count = 3)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value))
        {
            if (required) throw new FormatException($"A calibrated image /{key} array is missing.");
            return defaultValues;
        }
        PdfArray array = ResolveArray(value, count, $"Calibrated image /{key} array");
        return array.Select(item => Number(Resolve(item))).ToArray();
    }

    private void ReadAndValidateBlackPoint(PdfDictionary dictionary, string colorSpace)
    {
        double[] blackPoint = ReadCieArray(dictionary, "BlackPoint", required: false,
            defaultValues: [0, 0, 0]);
        if (blackPoint.Any(value => !double.IsFinite(value) || value < 0))
            throw new FormatException($"A {colorSpace} image black point is invalid.");
    }

    private static void ValidateWhitePoint(double[] whitePoint, string colorSpace)
    {
        if (whitePoint.Any(value => !double.IsFinite(value))
            || whitePoint[0] <= 0 || Math.Abs(whitePoint[1] - 1) > 1e-9
            || whitePoint[2] <= 0)
            throw new FormatException($"A {colorSpace} image white point is invalid.");
    }

    private static Func<double, double, double, Color> CreateXyzConverter(double[] whitePoint)
    {
        const double d65X = 0.95047, d65Y = 1, d65Z = 1.08883;
        (double sourceL, double sourceM, double sourceS) = Bradford(
            whitePoint[0], whitePoint[1], whitePoint[2]);
        (double targetL, double targetM, double targetS) = Bradford(d65X, d65Y, d65Z);
        double scaleL = targetL / sourceL;
        double scaleM = targetM / sourceM;
        double scaleS = targetS / sourceS;
        (double d50L, double d50M, double d50S) = Bradford(0.9642, 1, 0.8249);
        double connectionL = d50L / sourceL, connectionM = d50M / sourceM, connectionS = d50S / sourceS;
        return (x, y, z) =>
        {
            (double l, double m, double s) = Bradford(x, y, z);
            double l50 = l * connectionL, m50 = m * connectionM, s50 = s * connectionS;
            l *= scaleL;
            m *= scaleM;
            s *= scaleS;
            double adaptedX = 0.9869929 * l - 0.1470543 * m + 0.1599627 * s;
            double adaptedY = 0.4323053 * l + 0.5183603 * m + 0.0492912 * s;
            double adaptedZ = -0.0085287 * l + 0.0400428 * m + 0.9684867 * s;
            return Color.LinearRgb(
                3.2404542 * adaptedX - 1.5371385 * adaptedY - 0.4985314 * adaptedZ,
                -0.969266 * adaptedX + 1.8760108 * adaptedY + 0.041556 * adaptedZ,
                0.0556434 * adaptedX - 0.2040259 * adaptedY + 1.0572252 * adaptedZ) with
            {
                Connection = (0.9869929 * l50 - 0.1470543 * m50 + 0.1599627 * s50,
                    0.4323053 * l50 + 0.5183603 * m50 + 0.0492912 * s50,
                    -0.0085287 * l50 + 0.0400428 * m50 + 0.9684867 * s50)
            };
        };

        static (double L, double M, double S) Bradford(double x, double y, double z) => (
            0.8951 * x + 0.2664 * y - 0.1614 * z,
            -0.7502 * x + 1.7135 * y + 0.0367 * z,
            0.0389 * x - 0.0685 * y + 1.0296 * z);
    }

    private static double LabInverse(double value)
    {
        const double threshold = 6d / 29;
        return value >= threshold ? value * value * value
            : 3 * threshold * threshold * (value - 4d / 29);
    }

    private Func<double, Color> ReadExponentialFunction(
        PdfObject value, ImageColorSpace alternate, string description)
    {
        int outputCount = alternate.Components;
        PdfObject resolved = Resolve(value);
        PdfDictionary dictionary = resolved switch
        {
            PdfDictionary direct => direct,
            PdfStream stream => stream.Dictionary,
            _ => throw new FormatException($"A {description} is invalid.")
        };
        if (!dictionary.TryGetValue(Name("FunctionType"), out PdfObject? typeValue)
            || Resolve(typeValue) is not PdfInteger { Value: 2 })
            throw new NotSupportedException();
        double[] domain = ReadFunctionArray(dictionary, "Domain", 2, required: true,
            defaultValues: []);
        if (!double.IsFinite(domain[0]) || !double.IsFinite(domain[1])
            || domain[0] >= domain[1])
            throw new FormatException($"A {description} domain is invalid.");
        double[] c0 = ReadFunctionArray(dictionary, "C0", outputCount, required: false,
            defaultValues: Enumerable.Repeat(0d, outputCount).ToArray());
        double[] c1 = ReadFunctionArray(dictionary, "C1", outputCount, required: false,
            defaultValues: Enumerable.Repeat(1d, outputCount).ToArray());
        if (!dictionary.TryGetValue(Name("N"), out PdfObject? exponentValue))
            throw new FormatException($"A {description} exponent is missing.");
        double exponent = Number(Resolve(exponentValue));
        if (!double.IsFinite(exponent) || exponent <= 0
            || c0.Any(component => !double.IsFinite(component))
            || c1.Any(component => !double.IsFinite(component)))
            throw new FormatException($"A {description} is invalid.");
        double[]? range = dictionary.TryGetValue(Name("Range"), out _)
            ? ReadFunctionArray(dictionary, "Range", outputCount * 2, required: true,
                defaultValues: []) : null;
        if (range is not null && (range.Any(component => !double.IsFinite(component))
            || Enumerable.Range(0, outputCount).Any(index =>
                range[index * 2] > range[index * 2 + 1])))
            throw new FormatException($"A {description} range is invalid.");
        return input =>
        {
            double factor = Math.Pow(Math.Clamp(input, domain[0], domain[1]), exponent);
            double Component(int index)
            {
                double component = c0[index] + factor * (c1[index] - c0[index]);
                return range is null ? component
                    : Math.Clamp(component, range[index * 2], range[index * 2 + 1]);
            }
            if (alternate.MultiConverter is not null)
            {
                var components = new double[outputCount];
                for (int index = 0; index < components.Length; index++)
                    components[index] = Component(index);
                return alternate.Convert(components);
            }
            return alternate.Convert(Component(0), outputCount > 1 ? Component(1) : 0,
                outputCount > 2 ? Component(2) : 0, outputCount > 3 ? Component(3) : 0);
        };
    }

    private Func<double, Color> ReadColorFunction(
        PdfObject value, ImageColorSpace colorSpace, string description)
    {
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray functions)
        {
            CalculatorColorFunction components = ReadCalculatorComponentFunctions(
                functions, 1, colorSpace, description);
            return input => components(stackalloc double[] { input });
        }
        PdfDictionary dictionary = resolved switch
        {
            PdfDictionary direct => direct,
            PdfStream stream => stream.Dictionary,
            _ => throw new FormatException($"A {description} is invalid.")
        };
        if (!dictionary.TryGetValue(Name("FunctionType"), out PdfObject? typeValue)
            || Resolve(typeValue) is not PdfInteger type)
            throw new FormatException($"A {description} type is missing.");
        return type.Value switch
        {
            0 => ReadSampledFunction(resolved, dictionary, colorSpace, description),
            2 => ReadExponentialFunction(value, colorSpace, description),
            3 => ReadStitchingFunction(dictionary, colorSpace, description),
            4 when resolved is PdfStream calculator =>
                ReadSingleInputCalculatorFunction(calculator, colorSpace, description),
            _ => throw new NotSupportedException()
        };
    }

    private Func<double, Color> ReadSingleInputCalculatorFunction(
        PdfStream stream, ImageColorSpace colorSpace, string description)
    {
        CalculatorColorFunction function = ReadCalculatorColorFunction(
            stream, 1, colorSpace, description);
        return input => function(stackalloc double[] { input });
    }

    private Func<double, Color> ReadSampledFunction(PdfObject resolved,
        PdfDictionary dictionary, ImageColorSpace colorSpace, string description)
    {
        if (resolved is not PdfStream stream) throw new FormatException(
            $"A sampled {description} must be a stream.");
        double[] domain = ReadFunctionArray(dictionary, "Domain", 2, required: true,
            defaultValues: []);
        if (!double.IsFinite(domain[0]) || !double.IsFinite(domain[1])
            || domain[0] >= domain[1])
            throw new FormatException($"A sampled {description} domain is invalid.");
        if (!dictionary.TryGetValue(Name("Size"), out PdfObject? sizeValue)
            || Resolve(sizeValue) is not PdfArray { Count: 1 } sizeArray
            || Resolve(sizeArray[0]) is not PdfInteger sizeInteger
            || sizeInteger.Value is < 1 or > 1_000_000)
            throw new FormatException($"A sampled {description} size is invalid.");
        int size = (int)sizeInteger.Value;
        if (!dictionary.TryGetValue(Name("BitsPerSample"), out PdfObject? bitsValue)
            || Resolve(bitsValue) is not PdfInteger bitsInteger
            || bitsInteger.Value is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            throw new NotSupportedException();
        int bits = (int)bitsInteger.Value;
        if (dictionary.TryGetValue(Name("Order"), out PdfObject? orderValue)
            && Resolve(orderValue) is not PdfInteger { Value: 1 or 3 })
            throw new NotSupportedException();
        double[] range = ReadFunctionArray(dictionary, "Range",
            colorSpace.Components * 2, required: true, defaultValues: []);
        if (range.Any(value => !double.IsFinite(value))
            || Enumerable.Range(0, colorSpace.Components).Any(index =>
                range[index * 2] > range[index * 2 + 1]))
            throw new FormatException($"A sampled {description} range is invalid.");
        double[] encode = dictionary.TryGetValue(Name("Encode"), out _)
            ? ReadFunctionArray(dictionary, "Encode", 2, required: true, defaultValues: [])
            : [0, size - 1];
        if (encode.Any(value => !double.IsFinite(value)))
            throw new FormatException($"A sampled {description} encoding array is invalid.");
        double[] decode = dictionary.TryGetValue(Name("Decode"), out _)
            ? ReadFunctionArray(dictionary, "Decode", colorSpace.Components * 2,
                required: true, defaultValues: []) : range;
        if (decode.Any(value => !double.IsFinite(value)))
            throw new FormatException($"A sampled {description} decoding array is invalid.");
        int sampleCount = checked(size * colorSpace.Components);
        int expectedBytes = checked((sampleCount * bits + 7) / 8);
        byte[] samples = _document.DecodeStream(stream, expectedBytes);
        if (samples.Length != expectedBytes)
            throw new FormatException($"A sampled {description} stream is truncated.");
        uint maximum = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        return input =>
        {
            double normalized = (Math.Clamp(input, domain[0], domain[1]) - domain[0])
                / (domain[1] - domain[0]);
            double encoded = Math.Clamp(encode[0] + normalized * (encode[1] - encode[0]),
                0, size - 1);
            int lower = (int)Math.Floor(encoded);
            int upper = Math.Min(lower + 1, size - 1);
            double fraction = encoded - lower;
            double Component(int component)
            {
                uint first = ReadPackedSample(samples,
                    (lower * colorSpace.Components + component) * bits, bits);
                uint second = ReadPackedSample(samples,
                    (upper * colorSpace.Components + component) * bits, bits);
                double value = (first + fraction * (second - (double)first)) / maximum;
                double decoded = decode[component * 2] + value
                    * (decode[component * 2 + 1] - decode[component * 2]);
                return Math.Clamp(decoded, range[component * 2], range[component * 2 + 1]);
            }
            return colorSpace.Convert(Component(0),
                colorSpace.Components > 1 ? Component(1) : 0,
                colorSpace.Components > 2 ? Component(2) : 0,
                colorSpace.Components > 3 ? Component(3) : 0);
        };
    }

    private Func<double[], Color> ReadMultidimensionalColorFunction(PdfObject value,
        int inputCount, ImageColorSpace colorSpace, string description)
    {
        PdfObject resolved = Resolve(value);
        if (resolved is PdfArray functions)
        {
            CalculatorColorFunction components = ReadCalculatorComponentFunctions(
                functions, inputCount, colorSpace, description);
            return inputs => components(inputs);
        }
        PdfDictionary dictionary = resolved switch
        {
            PdfDictionary direct => direct,
            PdfStream streamValue => streamValue.Dictionary,
            _ => throw new FormatException($"A {description} is invalid.")
        };
        if (!dictionary.TryGetValue(Name("FunctionType"), out PdfObject? typeValue)
            || Resolve(typeValue) is not PdfInteger type)
            throw new FormatException($"A {description} type is missing.");
        if (type.Value == 4)
            return resolved is PdfStream calculator
                ? ReadCalculatorFunction(calculator, inputCount, colorSpace, description)
                : throw new FormatException($"A {description} calculator must be a stream.");
        if (inputCount == 1 && type.Value != 0)
        {
            Func<double, Color> single = ReadColorFunction(value, colorSpace, description);
            return inputs => single(inputs[0]);
        }
        if (type.Value != 0)
            throw new NotSupportedException();
        if (resolved is not PdfStream stream)
            throw new FormatException($"A sampled {description} must be a stream.");
        double[] domain = ReadFunctionArray(dictionary, "Domain", inputCount * 2,
            required: true, defaultValues: []);
        if (domain.Any(component => !double.IsFinite(component))
            || Enumerable.Range(0, inputCount).Any(index =>
                domain[index * 2] >= domain[index * 2 + 1]))
            throw new FormatException($"A {description} domain is invalid.");
        if (!dictionary.TryGetValue(Name("Size"), out PdfObject? sizeValue)
            || Resolve(sizeValue) is not PdfArray sizeArray || sizeArray.Count != inputCount)
            throw new FormatException($"A {description} size array is invalid.");
        int[] sizes = sizeArray.Select(item => Resolve(item) is PdfInteger size
                && size.Value is >= 1 and <= 1_000_000 ? (int)size.Value
                : throw new FormatException($"A {description} size is invalid."))
            .ToArray();
        long points = 1;
        foreach (int size in sizes)
        {
            points = checked(points * size);
            if (points > 1_000_000) throw new FormatException(
                $"A {description} sample table exceeds the supported bound.");
        }
        if (!dictionary.TryGetValue(Name("BitsPerSample"), out PdfObject? bitsValue)
            || Resolve(bitsValue) is not PdfInteger bitsInteger
            || bitsInteger.Value is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32))
            throw new NotSupportedException();
        int bits = (int)bitsInteger.Value;
        if (dictionary.TryGetValue(Name("Order"), out PdfObject? orderValue)
            && Resolve(orderValue) is not PdfInteger { Value: 1 or 3 })
            throw new NotSupportedException();
        double[] range = ReadFunctionArray(dictionary, "Range",
            colorSpace.Components * 2, required: true, defaultValues: []);
        if (range.Any(component => !double.IsFinite(component))
            || Enumerable.Range(0, colorSpace.Components).Any(index =>
                range[index * 2] > range[index * 2 + 1]))
            throw new FormatException($"A {description} range is invalid.");
        double[] encode = dictionary.TryGetValue(Name("Encode"), out _)
            ? ReadFunctionArray(dictionary, "Encode", inputCount * 2,
                required: true, defaultValues: [])
            : sizes.SelectMany(size => new[] { 0d, size - 1d }).ToArray();
        double[] decode = dictionary.TryGetValue(Name("Decode"), out _)
            ? ReadFunctionArray(dictionary, "Decode", colorSpace.Components * 2,
                required: true, defaultValues: []) : range;
        if (encode.Any(component => !double.IsFinite(component))
            || decode.Any(component => !double.IsFinite(component)))
            throw new FormatException($"A {description} mapping array is invalid.");
        int sampleCount = checked((int)points * colorSpace.Components);
        int expectedBytes = checked((sampleCount * bits + 7) / 8);
        byte[] samples = _document.DecodeStream(stream, expectedBytes);
        if (samples.Length != expectedBytes)
            throw new FormatException($"A {description} stream is truncated.");
        uint maximum = bits == 32 ? uint.MaxValue : (1u << bits) - 1;
        int cornerCount = 1 << inputCount;
        return inputs =>
        {
            Span<int> lower = stackalloc int[inputCount];
            Span<int> upper = stackalloc int[inputCount];
            Span<double> fractions = stackalloc double[inputCount];
            for (int input = 0; input < inputCount; input++)
            {
                double normalized = (Math.Clamp(inputs[input], domain[input * 2],
                    domain[input * 2 + 1]) - domain[input * 2])
                    / (domain[input * 2 + 1] - domain[input * 2]);
                double mapped = Math.Clamp(encode[input * 2] + normalized
                    * (encode[input * 2 + 1] - encode[input * 2]), 0, sizes[input] - 1);
                lower[input] = (int)Math.Floor(mapped);
                upper[input] = Math.Min(lower[input] + 1, sizes[input] - 1);
                fractions[input] = mapped - lower[input];
            }
            Span<double> outputs = stackalloc double[colorSpace.Components];
            for (int corner = 0; corner < cornerCount; corner++)
            {
                double weight = 1;
                int point = 0, stride = 1;
                for (int input = 0; input < inputCount; input++)
                {
                    bool high = (corner & (1 << input)) != 0;
                    point += (high ? upper[input] : lower[input]) * stride;
                    stride *= sizes[input];
                    weight *= high ? fractions[input] : 1 - fractions[input];
                }
                if (weight == 0) continue;
                for (int output = 0; output < colorSpace.Components; output++)
                    outputs[output] += weight * ReadPackedSample(samples,
                        (point * colorSpace.Components + output) * bits, bits) / maximum;
            }
            for (int output = 0; output < outputs.Length; output++)
            {
                double decoded = decode[output * 2] + outputs[output]
                    * (decode[output * 2 + 1] - decode[output * 2]);
                outputs[output] = Math.Clamp(decoded,
                    range[output * 2], range[output * 2 + 1]);
            }
            return colorSpace.Convert(outputs);
        };
    }

    private Func<double[], Color> ReadCalculatorFunction(PdfStream stream,
        int inputCount, ImageColorSpace colorSpace, string description)
    {
        CalculatorColorFunction function = ReadCalculatorColorFunction(
            stream, inputCount, colorSpace, description);
        return inputs => function(inputs);
    }

    private delegate Color CalculatorColorFunction(ReadOnlySpan<double> inputs);
    private delegate void CalculatorValueFunction(ReadOnlySpan<double> inputs, Span<double> outputs);

    private CalculatorColorFunction ReadCalculatorColorFunction(PdfStream stream,
        int inputCount, ImageColorSpace colorSpace, string description)
    {
        CalculatorValueFunction values = ReadCalculatorValueFunction(
            stream, inputCount, colorSpace.Components, description);
        if (colorSpace.MultiConverter is not null)
            return inputs =>
            {
                var outputs = new double[colorSpace.Components];
                values(inputs, outputs);
                return colorSpace.Convert(outputs);
            };
        return inputs =>
        {
            Span<double> buffer = stackalloc double[4];
            Span<double> outputs = buffer[..colorSpace.Components];
            values(inputs, outputs);
            return colorSpace.Convert(outputs);
        };
    }

    private CalculatorColorFunction ReadCalculatorComponentFunctions(PdfArray functions,
        int inputCount, ImageColorSpace colorSpace, string description)
    {
        if (functions.Count != colorSpace.Components)
            throw new FormatException($"A {description} array has the wrong component count.");
        CalculatorValueFunction[] components = functions.Select((item, index) =>
        {
            PdfObject component = Resolve(item);
            if (component is not PdfStream calculator
                || !calculator.Dictionary.TryGetValue(Name("FunctionType"),
                    out PdfObject? componentType)
                || Resolve(componentType) is not PdfInteger { Value: 4 })
                throw new NotSupportedException();
            return ReadCalculatorValueFunction(calculator, inputCount, 1,
                $"{description} component {index + 1}");
        }).ToArray();
        if (colorSpace.MultiConverter is not null)
            return inputs =>
            {
                var outputs = new double[components.Length];
                for (int component = 0; component < components.Length; component++)
                    components[component](inputs, outputs.AsSpan(component, 1));
                return colorSpace.Convert(outputs);
            };
        return inputs =>
        {
            Span<double> buffer = stackalloc double[4];
            Span<double> outputs = buffer[..components.Length];
            for (int component = 0; component < components.Length; component++)
                components[component](inputs, outputs.Slice(component, 1));
            return colorSpace.Convert(outputs);
        };
    }

    private CalculatorValueFunction ReadCalculatorValueFunction(PdfStream stream,
        int inputCount, int outputCount, string description)
    {
        PdfDictionary dictionary = stream.Dictionary;
        double[] domain = ReadFunctionArray(dictionary, "Domain", inputCount * 2,
            required: true, defaultValues: []);
        double[] range = ReadFunctionArray(dictionary, "Range", outputCount * 2,
            required: true, defaultValues: []);
        if (domain.Any(value => !double.IsFinite(value))
            || Enumerable.Range(0, inputCount).Any(index =>
                domain[index * 2] >= domain[index * 2 + 1])
            || range.Any(value => !double.IsFinite(value))
            || Enumerable.Range(0, outputCount).Any(index =>
                range[index * 2] > range[index * 2 + 1]))
            throw new FormatException($"A {description} domain or range is invalid.");
        byte[] program = _document.DecodeStream(stream, 1024 * 1024);
        var tokenizer = new PdfTokenizer(program);
        if (tokenizer.Read().Kind != PdfTokenKind.BraceStart)
            throw new FormatException($"A {description} program is invalid.");
        CalculatorProgram compiled = CalculatorProgram.Compile(tokenizer, description);
        if (tokenizer.Read().Kind != PdfTokenKind.EndOfInput)
            throw new FormatException($"A {description} program has trailing content.");

        return (inputs, outputs) =>
        {
            if (inputs.Length != inputCount)
                throw new InvalidOperationException("A calculator function received the wrong input count.");
            compiled.Evaluate(inputs, domain, range, outputs);
        };
    }

    private static uint ReadPackedSample(byte[] source, int bitOffset, int bits)
    {
        uint value = 0;
        for (int bit = 0; bit < bits; bit++)
        {
            int offset = bitOffset + bit;
            value = (value << 1) | (uint)((source[offset / 8] >> (7 - offset % 8)) & 1);
        }
        return value;
    }

    private Func<double, Color> ReadStitchingFunction(
        PdfDictionary dictionary, ImageColorSpace colorSpace, string description)
    {
        double[] domain = ReadFunctionArray(dictionary, "Domain", 2, required: true,
            defaultValues: []);
        if (!double.IsFinite(domain[0]) || !double.IsFinite(domain[1])
            || domain[0] >= domain[1])
            throw new FormatException($"A {description} domain is invalid.");
        if (dictionary.TryGetValue(Name("Range"), out _))
        {
            double[] range = ReadFunctionArray(dictionary, "Range",
                colorSpace.Components * 2, required: true, defaultValues: []);
            if (range.Any(component => !double.IsFinite(component))
                || Enumerable.Range(0, colorSpace.Components).Any(index =>
                    range[index * 2] != 0 || range[index * 2 + 1] != 1))
                throw new NotSupportedException();
        }
        if (!dictionary.TryGetValue(Name("Functions"), out PdfObject? functionsValue)
            || Resolve(functionsValue) is not PdfArray functionValues
            || functionValues.Count == 0)
            throw new FormatException($"A {description} function array is invalid.");
        Func<double, Color>[] functions = functionValues.Select((function, index) =>
            ReadColorFunction(function, colorSpace, $"{description} segment {index + 1}"))
            .ToArray();
        double[] bounds = functions.Length == 1 ? []
            : ReadFunctionArray(dictionary, "Bounds", functions.Length - 1, required: true,
                defaultValues: []);
        double previous = domain[0];
        foreach (double bound in bounds)
        {
            if (!double.IsFinite(bound) || bound < previous || bound > domain[1])
                throw new FormatException($"A {description} boundary array is invalid.");
            previous = bound;
        }
        double[] encode = ReadFunctionArray(dictionary, "Encode", functions.Length * 2,
            required: true, defaultValues: []);
        if (encode.Any(value => !double.IsFinite(value)))
            throw new FormatException($"A {description} encoding array is invalid.");
        return input =>
        {
            double clipped = Math.Clamp(input, domain[0], domain[1]);
            int segment = 0;
            while (segment < bounds.Length && clipped >= bounds[segment]) segment++;
            double start = segment == 0 ? domain[0] : bounds[segment - 1];
            double end = segment == bounds.Length ? domain[1] : bounds[segment];
            double fraction = end == start ? 0 : (clipped - start) / (end - start);
            double mapped = encode[segment * 2] + fraction
                * (encode[segment * 2 + 1] - encode[segment * 2]);
            return functions[segment](mapped);
        };
    }

    private bool TryRenderShading(PdfDictionary resources, PdfName resourceName,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken,
        out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            if (!resources.TryGetValue(Name("Shading"), out PdfObject? shadingsValue)
                || Resolve(shadingsValue) is not PdfDictionary shadings
                || !shadings.TryGetValue(resourceName, out PdfObject? shadingValue))
                throw new FormatException("A shading resource could not be resolved.");
            return TryRenderResolvedShading(Resolve(shadingValue), resources, state, target,
                targetWidth, targetHeight, scaleX, scaleY, cancellationToken, out diagnostic);
        }
        catch (NotSupportedException)
        {
            diagnostic = "The shading type or function is not implemented.";
            return false;
        }
    }

    private bool TryRenderResolvedShading(PdfObject resolved, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken,
        out string? diagnostic)
    {
        diagnostic = null;
        try
        {
            PdfDictionary shading = resolved switch
            {
                PdfDictionary dictionary => dictionary,
                PdfStream stream => stream.Dictionary,
                _ => throw new FormatException("An axial shading dictionary is invalid.")
            };
            if (!shading.TryGetValue(Name("ShadingType"), out PdfObject? typeValue)
                || Resolve(typeValue) is not PdfInteger shadingType)
                throw new NotSupportedException();
            if (shadingType.Value == 1)
                return RenderFunctionShading(shading, resources, state, target,
                    targetWidth, targetHeight, scaleX, scaleY, cancellationToken);
            if (shadingType.Value == 4)
                return resolved is PdfStream freeForm
                    ? RenderFreeFormMeshShading(freeForm, resources, state, target,
                        targetWidth, targetHeight, scaleX, scaleY, cancellationToken)
                    : throw new FormatException("A free-form mesh shading must be a stream.");
            if (shadingType.Value == 5)
                return resolved is PdfStream lattice
                    ? RenderLatticeMeshShading(lattice, resources, state, target,
                        targetWidth, targetHeight, scaleX, scaleY, cancellationToken)
                    : throw new FormatException("A lattice mesh shading must be a stream.");
            if (shadingType.Value is 6 or 7)
                return resolved is PdfStream patch
                    ? RenderPatchMeshShading(patch, resources, state, target,
                        targetWidth, targetHeight, scaleX, scaleY,
                        tensorProduct: shadingType.Value == 7, cancellationToken)
                    : throw new FormatException("A patch mesh shading must be a stream.");
            if (shadingType.Value == 3)
                return RenderRadialShading(shading, resources, state, target,
                    targetWidth, targetHeight, scaleX, scaleY, cancellationToken);
            if (shadingType.Value != 2) throw new NotSupportedException();
            if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
                throw new FormatException("An axial shading color space is missing.");
            ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0).ForDestination(target);
            if (!shading.TryGetValue(Name("Coords"), out PdfObject? coordinatesValue))
                throw new FormatException("An axial shading coordinate array is missing.");
            PdfArray coordinates = ResolveArray(coordinatesValue, 4,
                "Axial shading coordinate array");
            double x0 = Number(Resolve(coordinates[0]));
            double y0 = Number(Resolve(coordinates[1]));
            double x1 = Number(Resolve(coordinates[2]));
            double y1 = Number(Resolve(coordinates[3]));
            double axisX = x1 - x0, axisY = y1 - y0;
            double axisLengthSquared = axisX * axisX + axisY * axisY;
            if (!double.IsFinite(axisLengthSquared) || axisLengthSquared <= 0)
                throw new FormatException("An axial shading axis is invalid.");
            if (!shading.TryGetValue(Name("Function"), out PdfObject? functionValue))
                throw new FormatException("An axial shading function is missing.");
            Func<double, Color> function = ReadColorFunction(
                functionValue, colorSpace, "axial shading function");
            double[] domain = shading.TryGetValue(Name("Domain"), out PdfObject? domainValue)
                ? ResolveArray(domainValue, 2, "Axial shading domain")
                    .Select(item => Number(Resolve(item))).ToArray()
                : [0, 1];
            if (domain.Any(value => !double.IsFinite(value)) || domain[0] >= domain[1])
                throw new FormatException("An axial shading domain is invalid.");
            bool extendStart = false, extendEnd = false;
            if (shading.TryGetValue(Name("Extend"), out PdfObject? extendValue))
            {
                PdfArray extend = ResolveArray(extendValue, 2, "Axial shading extension array");
                extendStart = Resolve(extend[0]) is PdfBoolean { Value: true };
                extendEnd = Resolve(extend[1]) is PdfBoolean { Value: true };
            }
            Point[]? bounds = ReadShadingBounds(shading, state, "Axial");
            if (!state.Transform.TryInverse(out Matrix inverse)) return true;
            (int left, int top, int right, int bottom) = GetRasterBounds(
                state.Clips, bounds, targetWidth, targetHeight, scaleX, scaleY);
            bool cacheColumns = (axisX == 0 || inverse.C == 0) && (axisY == 0 || inverse.D == 0);
            bool cacheRow = (axisX == 0 || inverse.A == 0) && (axisY == 0 || inverse.B == 0);
            // Only exact repeated inputs are reused; no gradient quantization is applied.
            (long Bits, Color Color, bool Valid)[]? colors = cacheColumns
                ? new (long, Color, bool)[Math.Max(0, right - left)]
                : cacheRow ? new (long, Color, bool)[1] : null;
            for (int y = top; y < bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = left; x < right; x++)
                {
                    double pageX = (x + 0.5) / scaleX;
                    double pageY = (targetHeight - y - 0.5) / scaleY;
                    double clipAlpha = ClipAlpha(state.Clips, x, y);
                    if (clipAlpha <= 0) continue;
                    if (bounds is not null && !Contains([bounds], false, pageX, pageY)) continue;
                    Point point = inverse.Apply(pageX, pageY);
                    double unit = ((point.X - x0) * axisX + (point.Y - y0) * axisY)
                        / axisLengthSquared;
                    if (unit < 0 && !extendStart || unit > 1 && !extendEnd) continue;
                    unit = Math.Clamp(unit, 0, 1);
                    double input = domain[0] + unit * (domain[1] - domain[0]);
                    Color color;
                    if (colors is null) color = function(input);
                    else
                    {
                        ref var sample = ref colors[cacheColumns ? x - left : 0];
                        long bits = BitConverter.DoubleToInt64Bits(input);
                        if (!sample.Valid || sample.Bits != bits)
                            sample = (bits, function(input), true);
                        color = sample.Color;
                    }
                    color = OverprintColor(color, colorSpace, state.FillOverprint, state.OverprintMode);
                    SetPixel(target, targetWidth, x, y, color, state.FillAlpha * clipAlpha,
                        state.BlendMode, state.GraphicsSoftMask, state.Knockout);
                }
            }
            return true;
        }
        catch (NotSupportedException)
        {
            diagnostic = "The shading type or function is not implemented.";
            return false;
        }
    }

    private bool RenderFunctionShading(PdfDictionary shading, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A function shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0).ForDestination(target);
        if (!shading.TryGetValue(Name("Function"), out PdfObject? functionValue))
            throw new FormatException("A function shading function is missing.");
        Func<double[], Color> function = ReadMultidimensionalColorFunction(
            functionValue, 2, colorSpace, "function shading function");
        double[] domain = shading.TryGetValue(Name("Domain"), out _)
            ? ReadFunctionArray(shading, "Domain", 4, required: true, defaultValues: [])
            : [0, 1, 0, 1];
        if (domain.Any(value => !double.IsFinite(value))
            || domain[0] >= domain[1] || domain[2] >= domain[3])
            throw new FormatException("A function shading domain is invalid.");
        Matrix matrix = shading.TryGetValue(Name("Matrix"), out PdfObject? matrixValue)
            ? Matrix.From(ResolveArray(matrixValue, 6, "Function shading matrix"))
            : Matrix.Identity;
        Matrix shadingToPage = matrix.Then(state.Transform);
        if (!shadingToPage.TryInverse(out Matrix pageToShading)) return true;
        Point[]? bounds = ReadShadingBounds(shading,
            state with { Transform = shadingToPage }, "Function");
        (int left, int top, int right, int bottom) = GetRasterBounds(
            state.Clips, bounds, targetWidth, targetHeight, scaleX, scaleY);
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                double pageX = (x + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                double clipAlpha = ClipAlpha(state.Clips, x, y);
                if (clipAlpha <= 0) continue;
                if (bounds is not null && !Contains([bounds], false, pageX, pageY)) continue;
                Point point = pageToShading.Apply(pageX, pageY);
                if (point.X < domain[0] || point.X > domain[1]
                    || point.Y < domain[2] || point.Y > domain[3]) continue;
                Color color = OverprintColor(function([point.X, point.Y]), colorSpace,
                    state.FillOverprint, state.OverprintMode);
                SetPixel(target, targetWidth, x, y, color,
                    state.FillAlpha * clipAlpha, state.BlendMode, state.GraphicsSoftMask,
                    state.Knockout);
            }
        }
        return true;
    }

    private bool RenderFreeFormMeshShading(PdfStream stream, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        MeshDecoder mesh = ReadMeshDecoder(
            stream, resources, hasFlags: true, state.Transform, target);
        var previous = new MeshVertex[3];
        bool rendered = false;
        while (mesh.HasData)
        {
            uint flag = mesh.ReadFlag();
            if (flag == 0)
            {
                previous[0] = mesh.ReadVertex();
                mesh.ReadFlag();
                previous[1] = mesh.ReadVertex();
                mesh.ReadFlag();
                previous[2] = mesh.ReadVertex();
            }
            else if (flag == 1)
            {
                previous[0] = previous[1];
                previous[1] = previous[2];
                previous[2] = mesh.ReadVertex();
            }
            else if (flag == 2)
            {
                previous[1] = previous[2];
                previous[2] = mesh.ReadVertex();
            }
            else throw new FormatException("A free-form mesh shading has an invalid edge flag.");
            PaintMeshTriangle(previous[0], previous[1], previous[2], mesh, state, target,
                targetWidth, targetHeight, scaleX, scaleY, cancellationToken);
            rendered = true;
        }
        return rendered;
    }

    private bool RenderLatticeMeshShading(PdfStream stream, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        int verticesPerRow = checked((int)AssertInteger(stream.Dictionary, "VerticesPerRow"));
        if (verticesPerRow is < 2 or > MaximumMeshVerticesPerRow)
            throw new FormatException("A lattice mesh shading has an invalid row width.");
        MeshDecoder mesh = ReadMeshDecoder(
            stream, resources, hasFlags: false, state.Transform, target);
        MeshVertex[]? previous = null;
        int rowCount = 0;
        while (mesh.HasData)
        {
            var current = new MeshVertex[verticesPerRow];
            for (int column = 0; column < current.Length; column++)
            {
                if ((column & 0x3FFF) == 0)
                    cancellationToken.ThrowIfCancellationRequested();
                if (!mesh.HasData)
                    throw new FormatException("A lattice mesh shading has an incomplete row.");
                current[column] = mesh.ReadVertex();
            }
            if (previous is not null)
                for (int column = 1; column < verticesPerRow; column++)
                {
                    PaintMeshTriangle(previous[column - 1], previous[column],
                        current[column], mesh, state, target, targetWidth, targetHeight,
                        scaleX, scaleY, cancellationToken);
                    PaintMeshTriangle(previous[column - 1], current[column],
                        current[column - 1], mesh, state, target, targetWidth, targetHeight,
                        scaleX, scaleY, cancellationToken);
                }
            previous = current;
            rowCount++;
        }
        return rowCount >= 2;
    }

    private MeshDecoder ReadMeshDecoder(
        PdfStream stream, PdfDictionary resources, bool hasFlags, Matrix transform, RasterSurface target)
    {
        PdfDictionary shading = stream.Dictionary;
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A mesh shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0).ForDestination(target);
        int coordinateBits = PositiveInteger(shading, "BitsPerCoordinate");
        int componentBits = PositiveInteger(shading, "BitsPerComponent");
        int flagBits = hasFlags ? PositiveInteger(shading, "BitsPerFlag") : 0;
        if (coordinateBits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)
            || componentBits is not (1 or 2 or 4 or 8 or 12 or 16)
            || hasFlags && flagBits is not (2 or 4 or 8))
            throw new NotSupportedException();
        Func<double, Color>? function = shading.TryGetValue(Name("Function"),
            out PdfObject? functionValue)
            ? ReadColorFunction(functionValue, colorSpace, "mesh shading function") : null;
        int dataComponents = function is null ? colorSpace.Components : 1;
        double[] decode = ReadFunctionArray(shading, "Decode",
            4 + dataComponents * 2, required: true, defaultValues: []);
        if (decode.Any(value => !double.IsFinite(value)))
            throw new FormatException("A mesh shading decode array is invalid.");
        return new MeshDecoder(_document.DecodeStream(stream),
            coordinateBits, componentBits, flagBits, decode, dataComponents,
            colorSpace, function, transform);
    }

    private static void PaintMeshTriangle(MeshVertex first, MeshVertex second,
        MeshVertex third, MeshDecoder mesh, GraphicsState state, RasterSurface target, int targetWidth,
        int targetHeight, double scaleX, double scaleY,
        CancellationToken cancellationToken)
    {
        double area = Edge(first.Point, second.Point, third.Point.X, third.Point.Y);
        if (Math.Abs(area) < 1e-12) return;
        double minimumX = Math.Min(first.Point.X, Math.Min(second.Point.X, third.Point.X));
        double maximumX = Math.Max(first.Point.X, Math.Max(second.Point.X, third.Point.X));
        double minimumY = Math.Min(first.Point.Y, Math.Min(second.Point.Y, third.Point.Y));
        double maximumY = Math.Max(first.Point.Y, Math.Max(second.Point.Y, third.Point.Y));
        if (maximumX <= 0 || minimumX >= targetWidth / scaleX
            || maximumY <= 0 || minimumY >= targetHeight / scaleY) return;
        int left = Math.Clamp((int)Math.Floor(minimumX * scaleX), 0, targetWidth - 1);
        int right = Math.Clamp((int)Math.Ceiling(maximumX * scaleX), 0, targetWidth - 1);
        int top = Math.Clamp(targetHeight - (int)Math.Ceiling(maximumY * scaleY),
            0, targetHeight - 1);
        int bottom = Math.Clamp(targetHeight - (int)Math.Floor(minimumY * scaleY),
            0, targetHeight - 1);
        var values = new double[first.Values.Length];
        for (int y = top; y <= bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x <= right; x++)
            {
                double pageX = (x + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                double clipAlpha = ClipAlpha(state.Clips, x, y);
                if (clipAlpha <= 0) continue;
                double a = Edge(second.Point, third.Point, pageX, pageY) / area;
                double b = Edge(third.Point, first.Point, pageX, pageY) / area;
                double c = 1 - a - b;
                if (a < -1e-9 || b < -1e-9 || c < -1e-9) continue;
                for (int component = 0; component < values.Length; component++)
                    values[component] = first.Values[component] * a
                        + second.Values[component] * b + third.Values[component] * c;
                Color color = OverprintColor(mesh.Convert(values), mesh.ColorSpace,
                    state.FillOverprint, state.OverprintMode);
                SetPixel(target, targetWidth, x, y, color, state.FillAlpha * clipAlpha,
                    state.BlendMode, state.GraphicsSoftMask, state.Knockout);
            }
        }

        static double Edge(Point from, Point to, double x, double y) =>
            (to.X - from.X) * (y - from.Y) - (to.Y - from.Y) * (x - from.X);
    }

    private bool RenderPatchMeshShading(PdfStream stream, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, bool tensorProduct,
        CancellationToken cancellationToken)
    {
        PdfDictionary shading = stream.Dictionary;
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A patch mesh shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0).ForDestination(target);
        int coordinateBits = PositiveInteger(shading, "BitsPerCoordinate");
        int componentBits = PositiveInteger(shading, "BitsPerComponent");
        int flagBits = PositiveInteger(shading, "BitsPerFlag");
        if (coordinateBits is not (1 or 2 or 4 or 8 or 12 or 16 or 24 or 32)
            || componentBits is not (1 or 2 or 4 or 8 or 12 or 16)
            || flagBits is not (2 or 4 or 8))
            throw new NotSupportedException();
        Func<double, Color>? function = shading.TryGetValue(Name("Function"),
            out PdfObject? functionValue)
            ? ReadColorFunction(functionValue, colorSpace, "patch mesh shading function") : null;
        int dataComponents = function is null ? colorSpace.Components : 1;
        double[] decode = ReadFunctionArray(shading, "Decode",
            4 + dataComponents * 2, required: true, defaultValues: []);
        if (decode.Any(value => !double.IsFinite(value)))
            throw new FormatException("A patch mesh shading decode array is invalid.");
        byte[] bytes = _document.DecodeStream(stream);
        int bitOffset = 0;
        bool rendered = false;
        int pointCount = tensorProduct ? 16 : 12;
        int minimumRecordBits = checked(flagBits
            + (pointCount - 4) * coordinateBits * 2
            + 2 * dataComponents * componentBits);
        var points = new Point[16];
        var values = new double[4][];
        (int clipLeft, int clipTop, int clipRight, int clipBottom) = GetRasterBounds(
            state.Clips, null, targetWidth, targetHeight, scaleX, scaleY);
        bool[]? clipMask = state.Clips.Count == 0 ? null : BuildClipMask();
        while (bytes.Length * 8 - bitOffset >= minimumRecordBits)
        {
            uint flag = Read(flagBits);
            if (flag > 3 || !rendered && flag != 0)
                throw new FormatException("A patch mesh shading has an invalid edge flag.");
            int firstPoint = 0;
            int firstColor = 0;
            if (flag != 0)
            {
                var shared = new Point[4];
                for (int index = 0; index < shared.Length; index++)
                    shared[index] = points[(flag * 3 + index) % 12];
                Array.Copy(shared, points, shared.Length);
                double[][] sharedValues = [values[flag], values[(flag + 1) % 4]];
                values[0] = sharedValues[0];
                values[1] = sharedValues[1];
                firstPoint = 4;
                firstColor = 2;
            }
            for (int index = firstPoint; index < pointCount; index++)
            {
                double x = Decode(Read(coordinateBits), coordinateBits, decode[0], decode[1]);
                double y = Decode(Read(coordinateBits), coordinateBits, decode[2], decode[3]);
                points[index] = state.Transform.Apply(x, y);
            }
            for (int corner = firstColor; corner < values.Length; corner++)
            {
                var components = new double[dataComponents];
                for (int component = 0; component < components.Length; component++)
                    components[component] = Decode(Read(componentBits), componentBits,
                        decode[4 + component * 2], decode[5 + component * 2]);
                values[corner] = components;
            }
            if (!tensorProduct) AddCoonsInteriorPoints(points);
            RenderPatch(points, values);
            rendered = true;
        }
        return rendered;

        uint Read(int bits)
        {
            if (bitOffset + bits > bytes.Length * 8)
                throw new FormatException("A patch mesh shading stream is truncated.");
            uint value = ReadPackedSample(bytes, bitOffset, bits);
            bitOffset += bits;
            return value;
        }
        static double Decode(uint value, int bits, double minimum, double maximum)
        {
            double limit = bits == 32 ? uint.MaxValue : (1u << bits) - 1u;
            return minimum + value / limit * (maximum - minimum);
        }
        Color Convert(double[] components) =>
            function is null ? colorSpace.Convert(components) : function(components[0]);
        bool[] BuildClipMask()
        {
            var mask = new bool[checked(targetWidth * targetHeight)];
            for (int y = clipTop; y < clipBottom; y++)
                for (int x = clipLeft; x < clipRight; x++)
                    mask[y * targetWidth + x] = ClipCoverage(state.Clips, x, y) >= 128;
            return mask;
        }
        static void AddCoonsInteriorPoints(Point[] source)
        {
            source[12] = Combine(source[0], -4, source[1], 6, source[11], 6,
                source[3], -2, source[9], -2, source[8], 3, source[4], 3, source[6], -1);
            source[13] = Combine(source[3], -4, source[2], 6, source[4], 6,
                source[0], -2, source[6], -2, source[7], 3, source[11], 3, source[9], -1);
            source[15] = Combine(source[9], -4, source[8], 6, source[10], 6,
                source[6], -2, source[0], -2, source[1], 3, source[5], 3, source[3], -1);
            source[14] = Combine(source[6], -4, source[7], 6, source[5], 6,
                source[9], -2, source[3], -2, source[2], 3, source[10], 3, source[0], -1);

            static Point Combine(Point a, double aw, Point b, double bw,
                Point c, double cw, Point d, double dw, Point e, double ew,
                Point f, double fw, Point g, double gw, Point h, double hw) =>
                new((a.X * aw + b.X * bw + c.X * cw + d.X * dw + e.X * ew
                    + f.X * fw + g.X * gw + h.X * hw) / 9,
                    (a.Y * aw + b.Y * bw + c.Y * cw + d.Y * dw + e.Y * ew
                    + f.Y * fw + g.Y * gw + h.Y * hw) / 9);
        }
        void RenderPatch(Point[] source, double[][] colors)
        {
            Point[,] grid =
            {
                { source[0], source[1], source[2], source[3] },
                { source[11], source[12], source[13], source[4] },
                { source[10], source[15], source[14], source[5] },
                { source[9], source[8], source[7], source[6] }
            };
            const int divisions = 4;
            var sampledPoints = new Point[divisions + 1, divisions + 1];
            var sampledValues = new double[divisions + 1, divisions + 1][];
            for (int row = 0; row <= divisions; row++)
                for (int column = 0; column <= divisions; column++)
                {
                    double u = column / (double)divisions;
                    double v = row / (double)divisions;
                    sampledPoints[row, column] = BezierSurface(grid, u, v);
                    sampledValues[row, column] = Bilinear(colors, u, v);
                }
            for (int row = 0; row < divisions; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int column = 0; column < divisions; column++)
                {
                    PaintTriangle(sampledPoints[row, column], sampledValues[row, column],
                        sampledPoints[row, column + 1], sampledValues[row, column + 1],
                        sampledPoints[row + 1, column + 1], sampledValues[row + 1, column + 1]);
                    PaintTriangle(sampledPoints[row, column], sampledValues[row, column],
                        sampledPoints[row + 1, column + 1], sampledValues[row + 1, column + 1],
                        sampledPoints[row + 1, column], sampledValues[row + 1, column]);
                }
            }
        }
        void PaintTriangle(Point first, double[] firstValues, Point second,
            double[] secondValues, Point third, double[] thirdValues)
        {
            double area = Edge(first, second, third.X, third.Y);
            if (Math.Abs(area) < 1e-12) return;
            double minimumX = Math.Min(first.X, Math.Min(second.X, third.X));
            double maximumX = Math.Max(first.X, Math.Max(second.X, third.X));
            double minimumY = Math.Min(first.Y, Math.Min(second.Y, third.Y));
            double maximumY = Math.Max(first.Y, Math.Max(second.Y, third.Y));
            if (maximumX <= 0 || minimumX >= targetWidth / scaleX
                || maximumY <= 0 || minimumY >= targetHeight / scaleY) return;
            int left = Math.Clamp((int)Math.Floor(minimumX * scaleX),
                clipLeft, Math.Max(clipLeft, clipRight - 1));
            int right = Math.Clamp((int)Math.Ceiling(maximumX * scaleX),
                clipLeft, Math.Max(clipLeft, clipRight - 1));
            int top = Math.Clamp(targetHeight - (int)Math.Ceiling(maximumY * scaleY),
                clipTop, Math.Max(clipTop, clipBottom - 1));
            int bottom = Math.Clamp(targetHeight - (int)Math.Floor(minimumY * scaleY),
                clipTop, Math.Max(clipTop, clipBottom - 1));
            if (clipLeft >= clipRight || clipTop >= clipBottom) return;
            var components = new double[firstValues.Length];
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                {
                    double pageX = (x + 0.5) / scaleX;
                    double pageY = (targetHeight - y - 0.5) / scaleY;
                    if (clipMask is not null && !clipMask[y * targetWidth + x]) continue;
                    double a = Edge(second, third, pageX, pageY) / area;
                    double b = Edge(third, first, pageX, pageY) / area;
                    double c = 1 - a - b;
                    if (a < -1e-9 || b < -1e-9 || c < -1e-9) continue;
                    for (int component = 0; component < components.Length; component++)
                        components[component] = firstValues[component] * a
                            + secondValues[component] * b + thirdValues[component] * c;
                    Color color = OverprintColor(Convert(components), colorSpace,
                        state.FillOverprint, state.OverprintMode);
                    SetPixel(target, targetWidth, x, y, color,
                        state.FillAlpha, state.BlendMode, state.GraphicsSoftMask,
                        state.Knockout);
                }
        }
        static double Edge(Point first, Point second, double x, double y) =>
            (second.X - first.X) * (y - first.Y) - (second.Y - first.Y) * (x - first.X);
        static Point BezierSurface(Point[,] points, double u, double v)
        {
            double[] bu = Bernstein(u), bv = Bernstein(v);
            double x = 0, y = 0;
            for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                {
                    double weight = bu[column] * bv[row];
                    x += points[row, column].X * weight;
                    y += points[row, column].Y * weight;
                }
            return new Point(x, y);
        }
        static double[] Bernstein(double value)
        {
            double inverse = 1 - value;
            return [inverse * inverse * inverse, 3 * value * inverse * inverse,
                3 * value * value * inverse, value * value * value];
        }
        static double[] Bilinear(double[][] values, double u, double v)
        {
            var result = new double[values[0].Length];
            for (int component = 0; component < result.Length; component++)
                result[component] = values[0][component] * (1 - u) * (1 - v)
                    + values[1][component] * u * (1 - v)
                    + values[2][component] * u * v
                    + values[3][component] * (1 - u) * v;
            return result;
        }
    }

    private bool RenderRadialShading(PdfDictionary shading, PdfDictionary resources,
        GraphicsState state, RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A radial shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0).ForDestination(target);
        if (!shading.TryGetValue(Name("Coords"), out PdfObject? coordinatesValue))
            throw new FormatException("A radial shading coordinate array is missing.");
        PdfArray coordinates = ResolveArray(coordinatesValue, 6,
            "Radial shading coordinate array");
        double x0 = Number(Resolve(coordinates[0]));
        double y0 = Number(Resolve(coordinates[1]));
        double r0 = Number(Resolve(coordinates[2]));
        double x1 = Number(Resolve(coordinates[3]));
        double y1 = Number(Resolve(coordinates[4]));
        double r1 = Number(Resolve(coordinates[5]));
        if (new[] { x0, y0, r0, x1, y1, r1 }.Any(value => !double.IsFinite(value))
            || r0 < 0 || r1 < 0 || x0 == x1 && y0 == y1 && r0 == r1)
            throw new FormatException("A radial shading geometry is invalid.");
        if (!shading.TryGetValue(Name("Function"), out PdfObject? functionValue))
            throw new FormatException("A radial shading function is missing.");
        Func<double, Color> function = ReadColorFunction(
            functionValue, colorSpace, "radial shading function");
        double[] domain = shading.TryGetValue(Name("Domain"), out PdfObject? domainValue)
            ? ResolveArray(domainValue, 2, "Radial shading domain")
                .Select(item => Number(Resolve(item))).ToArray()
            : [0, 1];
        if (domain.Any(value => !double.IsFinite(value)) || domain[0] >= domain[1])
            throw new FormatException("A radial shading domain is invalid.");
        bool extendStart = false, extendEnd = false;
        if (shading.TryGetValue(Name("Extend"), out PdfObject? extendValue))
        {
            PdfArray extend = ResolveArray(extendValue, 2, "Radial shading extension array");
            extendStart = Resolve(extend[0]) is PdfBoolean { Value: true };
            extendEnd = Resolve(extend[1]) is PdfBoolean { Value: true };
        }
        Point[]? bounds = ReadShadingBounds(shading, state, "Radial");
        if (!state.Transform.TryInverse(out Matrix inverse)) return true;
        double centerX = x1 - x0, centerY = y1 - y0, radius = r1 - r0;
        (int left, int top, int right, int bottom) = GetRasterBounds(
            state.Clips, bounds, targetWidth, targetHeight, scaleX, scaleY);
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                double pageX = (x + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                double clipAlpha = ClipAlpha(state.Clips, x, y);
                if (clipAlpha <= 0) continue;
                if (bounds is not null && !Contains([bounds], false, pageX, pageY)) continue;
                Point point = inverse.Apply(pageX, pageY);
                double relativeX = x0 - point.X, relativeY = y0 - point.Y;
                double a = centerX * centerX + centerY * centerY - radius * radius;
                double b = 2 * (relativeX * centerX + relativeY * centerY - r0 * radius);
                double c = relativeX * relativeX + relativeY * relativeY - r0 * r0;
                if (!TryRadialParameter(a, b, c, r0, radius,
                    extendStart, extendEnd, out double unit)) continue;
                unit = Math.Clamp(unit, 0, 1);
                double input = domain[0] + unit * (domain[1] - domain[0]);
                Color color = OverprintColor(function(input), colorSpace,
                    state.FillOverprint, state.OverprintMode);
                SetPixel(target, targetWidth, x, y, color, state.FillAlpha * clipAlpha,
                    state.BlendMode, state.GraphicsSoftMask, state.Knockout);
            }
        }
        return true;
    }

    private static bool TryRadialParameter(double a, double b, double c,
        double startRadius, double radiusChange, bool extendStart, bool extendEnd,
        out double parameter)
    {
        Span<double> roots = stackalloc double[2];
        int count;
        if (Math.Abs(a) < 1e-12)
        {
            if (Math.Abs(b) < 1e-12)
            {
                parameter = 0;
                return false;
            }
            roots[0] = -c / b;
            count = 1;
        }
        else
        {
            double discriminant = b * b - 4 * a * c;
            if (discriminant < 0)
            {
                parameter = 0;
                return false;
            }
            double squareRoot = Math.Sqrt(discriminant);
            roots[0] = (-b - squareRoot) / (2 * a);
            roots[1] = (-b + squareRoot) / (2 * a);
            count = 2;
        }
        parameter = double.NaN;
        for (int index = 0; index < count; index++)
        {
            double candidate = roots[index];
            if (startRadius + candidate * radiusChange < 0) continue;
            bool paintable = candidate is >= 0 and <= 1
                || candidate < 0 && extendStart || candidate > 1 && extendEnd;
            if (!paintable) continue;
            if (double.IsNaN(parameter)
                || candidate is >= 0 and <= 1 && parameter is not (>= 0 and <= 1)
                || Math.Abs(candidate - 0.5) < Math.Abs(parameter - 0.5))
                parameter = candidate;
        }
        return !double.IsNaN(parameter);
    }

    private Point[]? ReadShadingBounds(
        PdfDictionary shading, GraphicsState state, string description)
    {
        if (!shading.TryGetValue(Name("BBox"), out PdfObject? boundsValue)) return null;
        PdfArray box = ResolveArray(boundsValue, 4, $"{description} shading bounding box");
        double left = Number(Resolve(box[0])), bottom = Number(Resolve(box[1]));
        double right = Number(Resolve(box[2])), top = Number(Resolve(box[3]));
        if (!double.IsFinite(left) || !double.IsFinite(bottom)
            || !double.IsFinite(right) || !double.IsFinite(top)
            || right <= left || top <= bottom)
            throw new FormatException($"A {description.ToLowerInvariant()} shading bounding box is invalid.");
        return
        [
            state.Transform.Apply(left, bottom), state.Transform.Apply(right, bottom),
            state.Transform.Apply(right, top), state.Transform.Apply(left, top)
        ];
    }

    private double[] ReadFunctionArray(PdfDictionary dictionary, string key, int count,
        bool required, double[] defaultValues)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value))
        {
            if (required) throw new FormatException($"A Separation tint-transform /{key} array is missing.");
            return defaultValues;
        }
        PdfArray array = ResolveArray(value, count, $"Separation tint-transform /{key} array");
        return array.Select(item => Number(Resolve(item))).ToArray();
    }

    private SoftMask? ReadSoftMask(PdfDictionary dictionary,
        Matrix transform, double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        if (!dictionary.TryGetValue(Name("SMask"), out PdfObject? value)) return null;
        PdfObject resolved = Resolve(value);
        if (resolved is PdfName name && name.ValueAsLatin1() == "None") return null;
        if (resolved is not PdfStream stream
            || NameValue(stream.Dictionary, "Subtype") != "Image"
            || NameValue(stream.Dictionary, "ColorSpace") != "DeviceGray"
            || stream.Dictionary.ContainsKey(Name("Mask"))
            || stream.Dictionary.ContainsKey(Name("SMask")))
            throw new NotSupportedException();
        int bits = PositiveInteger(stream.Dictionary, "BitsPerComponent");
        if (bits is not (1 or 2 or 4 or 8 or 16)) throw new NotSupportedException();
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        DecodedImage decodedImage = _imageCache.GetOrAdd(
            new ImageCacheKey(stream, -3), _ => DecodeMask());
        var mask = new SoftMask(decodedImage.Samples, decodedImage.Width, decodedImage.Height,
            decodedImage.MaskBits, decodedImage.MaskDecodeStart, decodedImage.MaskDecodeEnd);
        double deviceWidth = Math.Sqrt(Math.Pow(transform.A * scaleX, 2)
            + Math.Pow(transform.B * scaleY, 2));
        double deviceHeight = Math.Sqrt(Math.Pow(transform.C * scaleX, 2)
            + Math.Pow(transform.D * scaleY, 2));
        int reducedWidth = (int)Math.Clamp(Math.Ceiling(deviceWidth * 2), 1, width);
        int reducedHeight = (int)Math.Clamp(Math.Ceiling(deviceHeight * 2), 1, height);
        if (reducedWidth == width && reducedHeight == height) return mask;
        while ((long)reducedWidth * reducedHeight > 4_000_000)
        {
            reducedWidth = Math.Max(1, reducedWidth / 2);
            reducedHeight = Math.Max(1, reducedHeight / 2);
        }
        DecodedImage reduced = _imageCache.GetOrAdd(
            new ImageCacheKey(stream, -5, reducedWidth, reducedHeight), _ => ReduceMask());
        return new SoftMask(reduced.Samples, reduced.Width, reduced.Height);

        DecodedImage ReduceMask()
        {
            var samples = new byte[checked(reducedWidth * reducedHeight)];
            for (int y = 0; y < reducedHeight; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int startY = (int)((long)y * height / reducedHeight);
                int endY = (int)((long)(y + 1) * height / reducedHeight);
                for (int x = 0; x < reducedWidth; x++)
                {
                    int startX = (int)((long)x * width / reducedWidth);
                    int endX = (int)((long)(x + 1) * width / reducedWidth);
                    long sum = 0;
                    for (int sourceY = startY; sourceY < endY; sourceY++)
                        sum += mask.SumRow(startX, endX, sourceY);
                    long count = (long)(endX - startX) * (endY - startY);
                    samples[y * reducedWidth + x] = (byte)((sum + count / 2) / count);
                }
            }
            return new DecodedImage(samples, reducedWidth, reducedHeight);
        }

        DecodedImage DecodeMask()
        {
            int rowBytes = checked((width * bits + 7) / 8);
            int expected = checked(rowBytes * height);
            byte[] packed = _document.DecodeStream(stream, expected);
            if (packed.Length != expected)
                throw new FormatException("Image soft-mask sample data has an invalid length.");
            double decodeStart = 0, decodeEnd = 1;
            if (stream.Dictionary.TryGetValue(Name("Decode"), out PdfObject? decodeValue))
            {
                PdfArray decode = ResolveArray(decodeValue, 2, "Image soft-mask decode array");
                decodeStart = Number(Resolve(decode[0]));
                decodeEnd = Number(Resolve(decode[1]));
            }
            return new DecodedImage(packed, width, height, MaskBits: bits,
                MaskDecodeStart: decodeStart, MaskDecodeEnd: decodeEnd);
        }
    }

    private SoftMask ReadExplicitImageMask(PdfStream stream)
    {
        if (NameValue(stream.Dictionary, "Subtype") != "Image"
            || !stream.Dictionary.TryGetValue(Name("ImageMask"), out PdfObject? maskValue)
            || Resolve(maskValue) is not PdfBoolean { Value: true }
            || stream.Dictionary.ContainsKey(Name("Mask"))
            || stream.Dictionary.ContainsKey(Name("SMask")))
            throw new NotSupportedException();
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        DecodedImage decodedImage = _imageCache.GetOrAdd(
            new ImageCacheKey(stream, -4), _ => DecodeMask());
        return new SoftMask(decodedImage.Samples, decodedImage.Width, decodedImage.Height,
            decodedImage.MaskBits, decodedImage.MaskDecodeStart, decodedImage.MaskDecodeEnd);

        DecodedImage DecodeMask()
        {
            int rowBytes = checked((width + 7) / 8);
            int expected = checked(rowBytes * height);
            byte[] packed = _document.DecodeStream(stream, expected);
            if (packed.Length != expected)
                throw new FormatException("Image mask sample data has an invalid length.");
            bool paintsOne = StencilPaintsOne(stream.Dictionary);
            return new DecodedImage(packed, width, height, MaskBits: 1,
                MaskDecodeStart: paintsOne ? 0 : 1, MaskDecodeEnd: paintsOne ? 1 : 0);
        }
    }

    private bool StencilPaintsOne(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("Decode"), out PdfObject? value)) return false;
        PdfArray decode = ResolveArray(value, 2, "Image-mask decode array");
        return Number(Resolve(decode[0])) > Number(Resolve(decode[1]));
    }

    private double[] ReadImageDecode(
        PdfDictionary dictionary, ImageColorSpace colorSpace, bool imageMask)
    {
        if (imageMask) return [];
        if (!dictionary.TryGetValue(Name("Decode"), out PdfObject? value))
            return colorSpace.DefaultDecode ?? Enumerable.Repeat(new[] { 0d, 1d },
                colorSpace.Components).SelectMany(pair => pair).ToArray();
        PdfArray array = ResolveArray(value, colorSpace.Components * 2, "Image decode array");
        return array.Select(item => Number(Resolve(item))).ToArray();
    }

    private static void PaintImage(RasterSurface target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix transform, byte[] samples,
        int sourceWidth, int sourceHeight, int components, int bits,
        IReadOnlyList<ClipRegion> clips, bool imageMask, bool stencilPaintsOne,
        SoftMask? softMask, double[] decode, int[]? colorKeyMask,
        ImageColorSpace colorSpace, Color stencilColor, double stencilAlpha,
        RendererBlendMode blendMode, CancellationToken cancellationToken,
        double[]? preblendMatte, GraphicsSoftMask? graphicsSoftMask,
        KnockoutState? knockout, bool overprint)
    {
        if (imageMask ? stencilColor.DoesNotPaint : colorSpace.DoesNotPaint) return;
        // Zero opacity on a plain RGB surface changes nothing outside a knockout group.
        if (stencilAlpha <= 0 && knockout is null && target.Ink is null && target.RgbProfile is null) return;
        colorSpace = colorSpace.ForDestination(target);
        Point[] corners =
        [
            transform.Apply(0, 0), transform.Apply(1, 0),
            transform.Apply(0, 1), transform.Apply(1, 1)
        ];
        int left = Math.Clamp((int)Math.Floor(corners.Min(p => p.X) * scaleX), 0, targetWidth);
        int right = Math.Clamp((int)Math.Ceiling(corners.Max(p => p.X) * scaleX), 0, targetWidth);
        int top = Math.Clamp(targetHeight - (int)Math.Ceiling(corners.Max(p => p.Y) * scaleY), 0, targetHeight);
        int bottom = Math.Clamp(targetHeight - (int)Math.Floor(corners.Min(p => p.Y) * scaleY), 0, targetHeight);
        if (!transform.TryInverse(out Matrix inverse)) return;
        // A rectangular clip is fully applied by shrinking the painted bounds, so it does not
        // disqualify the direct paths; only antialiased clip masks need per-pixel coverage.
        // The plane resolution below still derives from the unclipped bounds so clipping
        // never changes which source samples are chosen.
        bool rectangularClips = true;
        int paintLeft = left, paintTop = top, paintRight = right, paintBottom = bottom;
        foreach (ClipRegion clip in clips)
        {
            if (clip.Mask.Coverage is not null)
            {
                rectangularClips = false;
                break;
            }
            paintLeft = Math.Max(paintLeft, clip.Mask.Left);
            paintTop = Math.Max(paintTop, clip.Mask.Top);
            paintRight = Math.Min(paintRight, clip.Mask.Right);
            paintBottom = Math.Min(paintBottom, clip.Mask.Bottom);
        }
        if (rectangularClips && (paintRight <= paintLeft || paintBottom <= paintTop)) return;
        if (!rectangularClips)
        {
            paintLeft = left;
            paintTop = top;
            paintRight = right;
            paintBottom = bottom;
        }
        int rowBytes = (sourceWidth * components * bits + 7) / 8;
        bool directRgb = target.RgbProfile is null && !imageMask && bits == 8 && components == 3
            && softMask is null && colorKeyMask is null && rectangularClips
            && graphicsSoftMask is null && knockout is null
            && colorSpace.Palette is null && colorSpace.Converter is null && colorSpace.Profile is null && colorSpace.ComponentRange is null
            && colorSpace.MultiConverter is null
            && decode is [0, 1, 0, 1, 0, 1]
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        bool directGray = target.RgbProfile is null && !imageMask && bits == 8 && components == 1
            && softMask is null && colorKeyMask is null && rectangularClips
            && graphicsSoftMask is null && knockout is null
            && colorSpace.Palette is null && colorSpace.Converter is null && colorSpace.Profile is null && colorSpace.ComponentRange is null
            && colorSpace.MultiConverter is null
            && decode is [0, 1]
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        if (directRgb || directGray)
        {
            double pageStepX = 1 / scaleX;
            double unitStepX = inverse.A * pageStepX;
            double unitStepY = inverse.B * pageStepX;
            byte[] directData = target.Data;
            for (int y = paintTop; y < paintBottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double pageX = (left + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                Point first = inverse.Apply(pageX, pageY);
                double unitX = first.X;
                double unitY = first.Y;
                int directRowOffset = target.Offset(left, y);
                // Step from the unclipped left edge so clipped rows sample identically.
                for (int x = left; x < paintRight; x++, unitX += unitStepX, unitY += unitStepY)
                {
                    if (x < paintLeft) continue;
                    if (unitX < 0 || unitX >= 1 || unitY < 0 || unitY >= 1) continue;
                    int sourceX = Math.Min((int)(unitX * sourceWidth), sourceWidth - 1);
                    int sourceY = Math.Min((int)((1 - unitY) * sourceHeight), sourceHeight - 1);
                    int sourceOffset = sourceY * rowBytes + sourceX * components;
                    if (stencilAlpha != 1 || target.Ink is not null || target.GroupAlpha is not null)
                    {
                        byte firstSample = samples[sourceOffset];
                        Color color = directGray ? Color.Gray(firstSample / 255d)
                            : new(firstSample, samples[sourceOffset + 1], samples[sourceOffset + 2]);
                        SetPixel(target, targetWidth, x, y, color, stencilAlpha, blendMode);
                        continue;
                    }
                    int targetOffset = directRowOffset + (x - left) * 4;
                    if (directGray)
                    {
                        byte gray = samples[sourceOffset];
                        directData[targetOffset] = gray;
                        directData[targetOffset + 1] = gray;
                        directData[targetOffset + 2] = gray;
                    }
                    else
                    {
                        directData[targetOffset] = samples[sourceOffset + 2];
                        directData[targetOffset + 1] = samples[sourceOffset + 1];
                        directData[targetOffset + 2] = samples[sourceOffset];
                    }
                    directData[targetOffset + 3] = 255;
                }
            }
            return;
        }
        int destinationWidth = right - left, destinationHeight = bottom - top;
        if (destinationWidth <= 0 || destinationHeight <= 0) return;

        // Convert the image once into a device-ready BGRA plane at no more than about the
        // destination resolution, so color conversion runs per source sample instead of per
        // painted pixel, then paint by nearest lookup.
        int samplingWidth = sourceWidth;
        int samplingHeight = sourceHeight;
        int factor = Math.Max(1, (int)Math.Floor(Math.Min(
            samplingWidth / (double)destinationWidth, samplingHeight / (double)destinationHeight)));
        while ((long)((samplingWidth + factor - 1) / factor) * ((samplingHeight + factor - 1) / factor)
            > 4_000_000L) factor++;
        int planeWidth = (samplingWidth + factor - 1) / factor;
        int planeHeight = (samplingHeight + factor - 1) / factor;
        byte[]? plane = imageMask || preblendMatte is not null ? null : RasterBuffers.Rent(checked(planeWidth * planeHeight * 4));
        var matteConverter = preblendMatte is not null && !imageMask
            ? new ImageSampleConverter(samples, sourceWidth, rowBytes, components,
                bits, decode, colorSpace, target.Ink is not null, target.BlendProfile, matte: true) : null;
        byte[]? alphaPlane = null;
        try
        {
            byte stencilAlphaByte = (byte)Math.Round(Math.Clamp(stencilAlpha, 0, 1) * 255);
            Color paintedStencil = imageMask && target.Ink is not null
                ? InkColor(target.GetInk(stencilColor)) with
                { OverprintComponents = stencilColor.OverprintComponents } : stencilColor;
            if (plane is not null)
            {
                if (target.Ink is not null && colorKeyMask is not null)
                    alphaPlane = RasterBuffers.Rent(checked(planeWidth * planeHeight));
                byte[] planeData = plane;
                byte[]? alphaPlaneData = alphaPlane;
                bool targetInk = target.Ink is not null;
                PdfColorTransform? blendProfile = target.BlendProfile;
                // Every plane sample is a pure function of its source sample, so row ranges can
                // convert independently. Each range owns its converter because the converter
                // keeps a small color cache.
                ForEachRow(0, planeHeight, (long)planeWidth * planeHeight, cancellationToken, (rowStart, rowEnd) =>
                {
                    var converter = new ImageSampleConverter(samples, sourceWidth, rowBytes, components,
                        bits, decode, colorSpace, targetInk, blendProfile);
                    for (int py = rowStart; py < rowEnd; py++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        int sy = Math.Min((int)((long)py * factor * sourceHeight / samplingHeight), sourceHeight - 1);
                        for (int px = 0; px < planeWidth; px++)
                        {
                            int sx = Math.Min((int)((long)px * factor * sourceWidth / samplingWidth), sourceWidth - 1);
                            int offset = (py * planeWidth + px) * 4;
                            uint color = converter.Convert(sx, sy);
                            int alpha = colorKeyMask is not null && converter.MatchesColorKey(sx, sy, colorKeyMask)
                                ? 0 : 255;
                            if (targetInk)
                            {
                                WriteInk(planeData, offset, color);
                                if (alphaPlaneData is not null) alphaPlaneData[offset / 4] = (byte)alpha;
                            }
                            else
                            {
                                WriteInk(planeData, offset, (color & 0xFFFFFF) | (uint)alpha << 24);
                            }
                        }
                    }
                });
            }

            // Stencil opacity uses its existing byte rounding. Ordinary images apply
            // nonstroking opacity after their image mask, without another byte rounding.
            double imageOpacity = imageMask ? 1 : Math.Clamp(stencilAlpha, 0, 1);
            bool direct = target.Ink is null && target.RgbProfile is null && target.GroupAlpha is null && imageOpacity == 1 && rectangularClips && graphicsSoftMask is null && knockout is null
                && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
            double pageStepX = 1 / scaleX;
            double unitStepX = inverse.A * pageStepX;
            double unitStepY = inverse.B * pageStepX;
            if (direct && plane is not null && alphaPlane is null && matteConverter is null
                && !colorSpace.NativeProcessMask.HasValue)
            {
                // Plain RGB destination with an opaque or color-keyed plane: opaque samples copy
                // straight across, and the rare partial alpha uses the ordinary compositor.
                // An image soft mask scales the alpha the same way the general loop does.
                byte[] data = target.Data;
                byte[] planeData = plane;
                ForEachRow(paintTop, paintBottom, (long)(paintRight - paintLeft) * (paintBottom - paintTop),
                    cancellationToken, (rowStart, rowEnd) =>
                {
                    for (int y = rowStart; y < rowEnd; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Point first = inverse.Apply((left + 0.5) / scaleX, (targetHeight - y - 0.5) / scaleY);
                        double unitX = first.X, unitY = first.Y;
                        int rowOffset = target.Offset(left, y);
                        for (int x = left; x < paintRight; x++, unitX += unitStepX, unitY += unitStepY)
                        {
                            if (x < paintLeft) continue;
                            if (unitX < 0 || unitX >= 1 || unitY < 0 || unitY >= 1) continue;
                            int px = Math.Min((int)(unitX * planeWidth), planeWidth - 1);
                            int py = Math.Min((int)((1 - unitY) * planeHeight), planeHeight - 1);
                            int planeOffset = (py * planeWidth + px) * 4;
                            int alpha = planeData[planeOffset + 3];
                            if (alpha == 0) continue;
                            if (softMask is not null)
                            {
                                int maskX = Math.Min((int)(unitX * softMask.Width), softMask.Width - 1);
                                int maskY = Math.Min((int)((1 - unitY) * softMask.Height), softMask.Height - 1);
                                alpha = (alpha * softMask.Sample(maskX, maskY) + 127) / 255;
                                if (alpha == 0) continue;
                            }
                            if (alpha == 255)
                            {
                                int targetOffset = rowOffset + (x - left) * 4;
                                data[targetOffset] = planeData[planeOffset];
                                data[targetOffset + 1] = planeData[planeOffset + 1];
                                data[targetOffset + 2] = planeData[planeOffset + 2];
                                data[targetOffset + 3] = 255;
                                continue;
                            }
                            SetPixel(target, targetWidth, x, y,
                                new Color(planeData[planeOffset + 2], planeData[planeOffset + 1], planeData[planeOffset]),
                                alpha / 255d, blendMode, graphicsSoftMask, knockout);
                        }
                    }
                });
                return;
            }
            for (int y = paintTop; y < paintBottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Point first = inverse.Apply((left + 0.5) / scaleX, (targetHeight - y - 0.5) / scaleY);
                double unitX = first.X, unitY = first.Y;
                // Step from the unclipped left edge so clipped rows sample identically.
                for (int x = left; x < paintRight; x++, unitX += unitStepX, unitY += unitStepY)
                {
                    if (x < paintLeft) continue;
                    if (unitX < 0 || unitX >= 1 || unitY < 0 || unitY >= 1) continue;
                    int px = Math.Min((int)(unitX * planeWidth), planeWidth - 1);
                    int py = Math.Min((int)((1 - unitY) * planeHeight), planeHeight - 1);
                    int alpha;
                    Color color;
                    if (matteConverter is not null)
                    {
                        int sx = Math.Min((int)(unitX * sourceWidth), sourceWidth - 1);
                        int sy = Math.Min((int)((1 - unitY) * sourceHeight), sourceHeight - 1);
                        byte maskSample = softMask is null ? (byte)255 : softMask.Sample(
                            Math.Min((int)(unitX * softMask.Width), softMask.Width - 1),
                            Math.Min((int)((1 - unitY) * softMask.Height), softMask.Height - 1));
                        if (maskSample == 0) continue;
                        uint packed = matteConverter.ConvertMatte(sx, sy, preblendMatte!, maskSample);
                        color = target.Ink is not null ? InkColor(packed)
                            : target.ColorFromRgb(new Color((byte)(packed >> 16), (byte)(packed >> 8), (byte)packed));
                        alpha = 255;
                    }
                    else if (plane is null)
                    {
                        int sx = Math.Min(px * factor, sourceWidth - 1);
                        int sy = Math.Min(py * factor, sourceHeight - 1);
                        bool one = (samples[sy * rowBytes + sx / 8] & (0x80 >> (sx & 7))) != 0;
                        if (one != stencilPaintsOne) continue;
                        alpha = stencilAlphaByte;
                        color = paintedStencil;
                    }
                    else
                    {
                        int planeOffset = (py * planeWidth + px) * 4;
                        alpha = target.Ink is not null
                            ? alphaPlane is not null ? alphaPlane[planeOffset / 4] : 255
                            : plane[planeOffset + 3];
                        color = target.Ink is not null ? InkColor(ReadInk(plane, planeOffset))
                            : target.ColorFromRgb(new(plane[planeOffset + 2], plane[planeOffset + 1], plane[planeOffset]));
                    }
                    if (alpha == 0) continue;
                    if (softMask is not null && alpha != 0)
                    {
                        int maskX = Math.Min((int)(unitX * softMask.Width), softMask.Width - 1);
                        int maskY = Math.Min((int)((1 - unitY) * softMask.Height), softMask.Height - 1);
                        byte maskSample = softMask.Sample(maskX, maskY);
                        alpha = (alpha * maskSample + 127) / 255;
                    }
                    if (alpha == 0) continue;
                    if (direct && alpha == 255)
                    {
                        int targetOffset = target.Offset(x, y);
                        target[targetOffset] = color.Blue;
                        target[targetOffset + 1] = color.Green;
                        target[targetOffset + 2] = color.Red;
                        target[targetOffset + 3] = 255;
                        continue;
                    }
                    double clipAlpha = rectangularClips ? 1 : ClipAlpha(clips, x, y);
                    if (clipAlpha <= 0) continue;
                    if (!imageMask && colorSpace.NativeProcessMask.HasValue)
                        color = OverprintColor(color, colorSpace, overprint, 0);
                    SetPixel(target, targetWidth, x, y,
                        color,
                        alpha / 255d * imageOpacity * clipAlpha, blendMode, graphicsSoftMask, knockout);
                }
            }
        }
        finally
        {
            if (alphaPlane is not null) RasterBuffers.Return(alphaPlane);
            if (plane is not null) RasterBuffers.Return(plane);
        }
    }

    /// <summary>Reads packed image samples and converts them to device colors with a small cache.</summary>
    private sealed class ImageSampleConverter
    {
        private const int CacheSize = 4096;
        private readonly byte[] _samples;
        private readonly int _rowBytes;
        private readonly int _components;
        private readonly int _bits;
        private readonly double[] _decode;
        private readonly ImageColorSpace _colorSpace;
        private readonly double[] _values;
        private readonly uint[]? _lookup;
        private readonly bool[]? _lookupSet;
        private readonly uint[]? _cacheKeys;
        private readonly uint[]? _cacheValues;
        private readonly bool[]? _cacheValid;
        private readonly byte[]? _matteAlpha;
        private readonly int _maximum;
        private readonly bool _targetInk;
        private readonly PdfColorTransform? _targetProfile;
        private readonly bool _directRgb;
        private readonly bool _directGray;
        private readonly bool _directCmyk;

        // Display value of one unprofiled CMYK channel for every (ink, black) byte pair,
        // computed with Color.Cmyk's own arithmetic so the lookup is exact.
        private static readonly Lazy<byte[]> CmykChannelTable = new(() =>
        {
            var table = new byte[256 * 256];
            for (int ink = 0; ink < 256; ink++)
                for (int black = 0; black < 256; black++)
                    table[ink * 256 + black] = Color.Cmyk(ink / 255d, 0, 0, black / 255d).Red;
            return table;
        });

        internal ImageSampleConverter(byte[] samples, int sourceWidth, int rowBytes,
            int components, int bits, double[] decode, ImageColorSpace colorSpace,
            bool targetInk, PdfColorTransform? targetProfile, bool matte = false)
        {
            _samples = samples;
            _rowBytes = rowBytes;
            _components = components;
            _bits = bits;
            _decode = decode;
            _colorSpace = colorSpace;
            _targetInk = targetInk;
            _targetProfile = targetProfile;
            _values = new double[(colorSpace.PaletteBase ?? colorSpace).Components];
            _maximum = bits >= 31 ? int.MaxValue : (1 << bits) - 1;
            // Plain 8-bit device gray or RGB samples with the default decode convert to the
            // same byte they started as, so those images skip the double conversion chain.
            bool plainSpace = !targetInk && targetProfile is null && !colorSpace.DoesNotPaint
                && colorSpace.Palette is null && colorSpace.Converter is null
                && colorSpace.MultiConverter is null && colorSpace.Profile is null
                && colorSpace.ComponentRange is null && bits == 8 && colorSpace.Components == components;
            _directRgb = plainSpace && components == 3 && decode is [0, 1, 0, 1, 0, 1];
            _directGray = plainSpace && components == 1 && decode is [0, 1];
            _directCmyk = plainSpace && components == 4 && decode is [0, 1, 0, 1, 0, 1, 0, 1];
            if (components == 1 && bits <= 8)
            {
                _lookup = new uint[1 << bits];
                _lookupSet = new bool[1 << bits];
                if (matte) _matteAlpha = new byte[1 << bits];
            }
            else if (bits <= 8 && components <= 4)
            {
                _cacheKeys = new uint[CacheSize];
                _cacheValues = new uint[CacheSize];
                _cacheValid = new bool[CacheSize];
                if (matte) _matteAlpha = new byte[CacheSize];
            }
        }

        internal int Raw(int x, int y, int component)
        {
            if (_bits == 8) return _samples[y * _rowBytes + x * _components + component];
            long bitOffset = (long)y * _rowBytes * 8 + ((long)x * _components + component) * _bits;
            return checked((int)ReadPackedSample(_samples, checked((int)bitOffset), _bits));
        }

        internal bool MatchesColorKey(int x, int y, int[] colorKeyMask)
        {
            for (int component = 0; component < _components; component++)
            {
                int sample = Raw(x, y, component);
                if (sample < colorKeyMask[component * 2] || sample > colorKeyMask[component * 2 + 1])
                    return false;
            }
            return true;
        }

        internal uint Convert(int x, int y)
        {
            if (_directRgb)
            {
                int offset = y * _rowBytes + x * 3;
                return (uint)(_samples[offset + 2] | _samples[offset + 1] << 8 | _samples[offset] << 16)
                    | 0xFF000000;
            }
            if (_directGray)
            {
                uint gray = _samples[y * _rowBytes + x];
                return gray | gray << 8 | gray << 16 | 0xFF000000;
            }
            if (_directCmyk)
            {
                int offset = y * _rowBytes + x * 4;
                byte[] table = CmykChannelTable.Value;
                int black = _samples[offset + 3];
                return (uint)(table[_samples[offset + 2] * 256 + black]
                    | table[_samples[offset + 1] * 256 + black] << 8
                    | table[_samples[offset] * 256 + black] << 16) | 0xFF000000;
            }
            if (_lookup is not null)
            {
                int sample = Math.Min(Raw(x, y, 0), _lookup.Length - 1);
                if (!_lookupSet![sample])
                {
                    _lookup[sample] = ConvertRaw(sample, 0, 0, 0);
                    _lookupSet[sample] = true;
                }
                return _lookup[sample];
            }
            if (_cacheKeys is not null)
            {
                uint key = 0;
                for (int component = 0; component < _components; component++)
                    key = (key << 8) | (uint)Raw(x, y, component);
                int slot = (int)((key * 2654435761u) >> 20) & (CacheSize - 1);
                if (_cacheValid![slot] && _cacheKeys[slot] == key) return _cacheValues![slot];
                uint color = ConvertRaw((int)(key >> (8 * (_components - 1))) & 255,
                    _components > 1 ? (int)(key >> (8 * (_components - 2))) & 255 : 0,
                    _components > 2 ? (int)(key >> (8 * (_components - 3))) & 255 : 0,
                    _components > 3 ? (int)key & 255 : 0);
                _cacheKeys[slot] = key;
                _cacheValues![slot] = color;
                _cacheValid[slot] = true;
                return color;
            }
            if (_colorSpace.Palette is not null)
                return Pack(_colorSpace.Palette[Math.Min(Raw(x, y, 0), _colorSpace.Palette.Length - 1)]);
            for (int component = 0; component < _components; component++)
                _values[component] = Decode(component, Raw(x, y, component));
            return ConvertDecoded(_colorSpace);
        }

        private uint ConvertRaw(int first, int second, int third, int fourth)
        {
            if (_colorSpace.Palette is not null)
                return Pack(_colorSpace.Palette[Math.Min(first, _colorSpace.Palette.Length - 1)]);
            ReadOnlySpan<int> raw = [first, second, third, fourth];
            for (int component = 0; component < _components; component++)
                _values[component] = Decode(component, raw[component]);
            return ConvertDecoded(_colorSpace);
        }

        private uint ConvertDecoded(ImageColorSpace space) =>
            _targetInk && space.Components == 4 && space.Profile is not null && space.ComponentRange is null
                && ReferenceEquals(space.Profile, _targetProfile)
                ? Color.Cmyk(_values[0], _values[1], _values[2], _values[3]).Ink!.Value
                : Pack(space.Convert(_values));

        private uint Pack(Color color)
        {
            if (_targetInk) return ColorInk(color, _targetProfile);
            color = ColorRgb(color, _targetProfile);
            return (uint)(color.Blue | color.Green << 8 | color.Red << 16) | 0xFF000000;
        }

        internal uint ConvertMatte(int x, int y, double[] matte, byte alpha)
        {
            int slot = -1;
            uint key = 0;
            if (_lookup is not null)
            {
                slot = Math.Min(Raw(x, y, 0), _lookup.Length - 1);
                if (_lookupSet![slot] && _matteAlpha![slot] == alpha) return _lookup[slot];
            }
            else if (_cacheKeys is not null)
            {
                for (int component = 0; component < _components; component++)
                    key = (key << 8) | (uint)Raw(x, y, component);
                slot = (int)(((key ^ alpha) * 2654435761u) >> 20) & (CacheSize - 1);
                if (_cacheValid![slot] && _cacheKeys[slot] == key && _matteAlpha![slot] == alpha)
                    return _cacheValues![slot];
            }
            ImageColorSpace space = _colorSpace.PaletteBase ?? _colorSpace;
            int paletteOffset = _colorSpace.Palette is null ? 0
                : Math.Min(Raw(x, y, 0), _colorSpace.Palette.Length - 1) * space.Components;
            for (int component = 0; component < space.Components; component++)
            {
                double value = _colorSpace.PaletteSamples is byte[] palette
                    ? space.DefaultValue(component, palette[paletteOffset + component] / 255d)
                    : Decode(component, Raw(x, y, component));
                _values[component] = (value - matte[component]) * (255d / alpha) + matte[component];
            }
            uint color = ConvertDecoded(space);
            if (_lookup is not null)
            {
                _lookup[slot] = color;
                _lookupSet![slot] = true;
                _matteAlpha![slot] = alpha;
            }
            else if (_cacheKeys is not null)
            {
                _cacheKeys[slot] = key;
                _cacheValues![slot] = color;
                _cacheValid![slot] = true;
                _matteAlpha![slot] = alpha;
            }
            return color;
        }

        private double Decode(int component, int sample)
        {
            double normalized = sample / (double)_maximum;
            return _decode[component * 2] + normalized
                * (_decode[component * 2 + 1] - _decode[component * 2]);
        }
    }

    private static void WritePackedSample(
        byte[] target, int bitOffset, int bits, uint value)
    {
        for (int bit = bits - 1; bit >= 0; bit--, bitOffset++)
            if ((value & (1u << bit)) != 0)
                target[bitOffset / 8] |= (byte)(1 << (7 - bitOffset % 8));
    }

    private bool TryGetGraphicsState(PdfDictionary resources, PdfName resourceName,
        out double? fillAlpha, out double? strokeAlpha, out RendererBlendMode? blendMode,
        out bool unsupportedBlend, out PdfObject? softMaskValue, out PdfObject? fontValue,
        out PdfDictionary? strokeSettings)
    {
        fillAlpha = strokeAlpha = null;
        blendMode = null;
        unsupportedBlend = false;
        softMaskValue = null;
        fontValue = null;
        strokeSettings = null;
        if (!resources.TryGetValue(Name("ExtGState"), out PdfObject? statesValue)
            || Resolve(statesValue) is not PdfDictionary states
            || !states.TryGetValue(resourceName, out PdfObject? stateValue)
            || Resolve(stateValue) is not PdfDictionary dictionary)
            return false;
        strokeSettings = dictionary;
        fillAlpha = Alpha(dictionary, "ca");
        strokeAlpha = Alpha(dictionary, "CA");
        dictionary.TryGetValue(Name("SMask"), out softMaskValue);
        dictionary.TryGetValue(Name("Font"), out fontValue);
        if (dictionary.TryGetValue(Name("BM"), out PdfObject? blendValue))
        {
            PdfObject blend = Resolve(blendValue);
            if (blend is PdfName name)
            {
                unsupportedBlend = !TryReadBlendMode(name, out RendererBlendMode parsed);
                if (!unsupportedBlend) blendMode = parsed;
            }
            else if (blend is PdfArray array)
            {
                unsupportedBlend = true;
                foreach (PdfObject item in array)
                    if (Resolve(item) is PdfName candidate
                        && TryReadBlendMode(candidate, out RendererBlendMode parsed))
                    {
                        blendMode = parsed;
                        unsupportedBlend = false;
                        break;
                    }
            }
            else unsupportedBlend = true;
        }
        return true;

        double? Alpha(PdfDictionary source, string key)
        {
            if (!source.TryGetValue(Name(key), out PdfObject? value)) return null;
            double alpha = Number(Resolve(value));
            return double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1;
        }
    }

    private GraphicsState ApplyGraphicsStrokeSettings(GraphicsState state,
        PdfDictionary dictionary, ISet<string> diagnostics)
    {
        foreach (string key in new[] { "LW", "LC", "LJ", "ML", "D" })
        {
            if (!dictionary.TryGetValue(Name(key), out PdfObject? value)) continue;
            try
            {
                if (key == "D")
                {
                    PdfArray dash = ResolveArray(value, 2, "A graphics-state dash pattern");
                    if (Resolve(dash[0]) is not PdfArray array)
                        throw new FormatException("A graphics-state dash array is invalid.");
                    double[] pattern = array.Select(item => Number(Resolve(item))).ToArray();
                    double phase = Number(Resolve(dash[1]));
                    double cycle = pattern.Sum() * (pattern.Length % 2 == 0 ? 1 : 2);
                    if (pattern.Any(length => !double.IsFinite(length) || length < 0)
                        || pattern.Length > 0 && cycle == 0
                        || !double.IsFinite(cycle) || !double.IsFinite(phase))
                        throw new FormatException("A graphics-state dash pattern is invalid.");
                    if (phase < 0 && pattern.Length > 0)
                        phase = (phase % cycle + cycle) % cycle;
                    state = state with { DashPattern = Array.AsReadOnly(pattern), DashPhase = phase };
                    continue;
                }
                double number = Number(Resolve(value));
                if (!double.IsFinite(number))
                    throw new FormatException("A graphics-state stroke value is invalid.");
                switch (key)
                {
                    case "LW":
                        if (number < 0) throw new FormatException("A graphics-state line width is invalid.");
                        state = state with { LineWidth = number };
                        break;
                    case "LC":
                    case "LJ":
                        if (number != Math.Truncate(number) || number is < 0 or > 2)
                            throw new FormatException("A graphics-state line style is invalid.");
                        state = key == "LC"
                            ? state with { LineCap = (RendererLineCap)(int)number }
                            : state with { LineJoin = (RendererLineJoin)(int)number };
                        break;
                    case "ML":
                        if (number < 1)
                        {
                            if (!_document.UsesCompatibilityRecovery)
                                throw new FormatException("A graphics-state miter limit is invalid.");
                            diagnostics.Add("An invalid graphics-state miter limit was clamped to one.");
                            number = 1;
                        }
                        state = state with { MiterLimit = number };
                        break;
                }
            }
            catch (FormatException) when (_document.UsesCompatibilityRecovery)
            {
                diagnostics.Add($"An invalid graphics-state /{key} stroke setting was ignored.");
            }
        }
        return state;
    }

    private static bool TryReadBlendMode(PdfName name, out RendererBlendMode mode) =>
        Enum.TryParse(name.ValueAsLatin1(), ignoreCase: false, out mode);

    private int PositiveInteger(PdfDictionary dictionary, string key)
    {
        if (!dictionary.TryGetValue(Name(key), out PdfObject? value)
            || Resolve(value) is not PdfInteger integer || integer.Value <= 0 || integer.Value > int.MaxValue)
            throw new FormatException($"An image /{key} value is invalid.");
        return (int)integer.Value;
    }

    private string NameValue(PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value) && Resolve(value) is PdfName name
            ? name.ValueAsLatin1() : string.Empty;

    private bool IsName(PdfDictionary dictionary, string key, string expected) =>
        NameValue(dictionary, key) == expected;

    private PdfArray ResolveArray(PdfObject value, int count, string description)
    {
        if (Resolve(value) is not PdfArray array || array.Count != count)
            throw new FormatException($"{description} is invalid.");
        return array;
    }

    private PdfObject Resolve(PdfObject value)
    {
        if (value is not PdfIndirectReference) return value;
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)) || visited.Count > 32)
                throw new FormatException("An image resource contains an invalid reference chain.");
            value = _document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(System.Text.Encoding.ASCII.GetBytes(value));

    private static (int Left, int Top, int Right, int Bottom) GetRasterBounds(
        IReadOnlyList<ClipRegion> clips, Point[]? explicitBounds,
        int width, int height, double scaleX, double scaleY)
    {
        double minimumX = 0, minimumY = 0;
        double maximumX = width / scaleX, maximumY = height / scaleY;
        Intersect(explicitBounds);
        if (minimumX >= maximumX || minimumY >= maximumY)
            return (0, 0, 0, 0);
        int left = (int)Math.Clamp(Math.Floor(minimumX * scaleX), 0, width);
        int right = (int)Math.Clamp(Math.Ceiling(maximumX * scaleX), 0, width);
        int top = (int)Math.Clamp(height - Math.Ceiling(maximumY * scaleY), 0, height);
        int bottom = (int)Math.Clamp(height - Math.Floor(minimumY * scaleY), 0, height);
        foreach (ClipRegion clip in clips)
        {
            left = Math.Max(left, clip.Mask.Left);
            top = Math.Max(top, clip.Mask.Top);
            right = Math.Min(right, clip.Mask.Right);
            bottom = Math.Min(bottom, clip.Mask.Bottom);
        }
        if (right <= left || bottom <= top) return (0, 0, 0, 0);
        return (left, top, right, bottom);

        void Intersect(Point[]? polygon)
        {
            if (polygon is null || polygon.Length == 0) return;
            minimumX = Math.Max(minimumX, polygon.Min(point => point.X));
            minimumY = Math.Max(minimumY, polygon.Min(point => point.Y));
            maximumX = Math.Min(maximumX, polygon.Max(point => point.X));
            maximumY = Math.Min(maximumY, polygon.Max(point => point.Y));
        }
    }

    private static void FillPaths(RasterSurface pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha, bool evenOdd,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        GraphicsSoftMask? graphicsSoftMask, KnockoutState? knockout,
        CancellationToken cancellationToken)
    {
        if (color.DoesNotPaint) return;
        var frame = new RasterFrame(width, height, scaleX, scaleY);
        CoverageMask mask = RasterizeFill(paths, evenOdd, frame, rent: true);
        try
        {
            PaintCoverage(pixels, width, height, mask, color, alpha, blendMode, clips,
                graphicsSoftMask, knockout, cancellationToken);
        }
        finally
        {
            mask.Return();
        }
    }

    private static void StrokePaths(RasterSurface pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha,
        double lineWidth, RendererLineCap lineCap, RendererLineJoin lineJoin,
        double miterLimit, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips, GraphicsSoftMask? graphicsSoftMask,
        KnockoutState? knockout,
        CancellationToken cancellationToken)
    {
        if (color.DoesNotPaint) return;
        var frame = new RasterFrame(width, height, scaleX, scaleY);
        CoverageMask mask = RasterizeStroke(paths, lineWidth, lineCap, lineJoin, miterLimit, frame,
            rent: true);
        try
        {
            PaintCoverage(pixels, width, height, mask, color, alpha, blendMode, clips,
                graphicsSoftMask, knockout, cancellationToken);
        }
        finally
        {
            mask.Return();
        }
    }

    private static void AddCubic(List<Point> path, Point start, Point control1,
        Point control2, Point end)
    {
        for (int step = 1; step <= 16; step++)
        {
            double t = step / 16d, u = 1 - t;
            path.Add(new Point(u * u * u * start.X + 3 * u * u * t * control1.X
                + 3 * u * t * t * control2.X + t * t * t * end.X,
                u * u * u * start.Y + 3 * u * u * t * control1.Y
                + 3 * u * t * t * control2.Y + t * t * t * end.Y));
        }
    }

    private IReadOnlyList<List<Point>> FlattenGlyphOutline(
        PdfGlyphOutline outline, Matrix transform)
    {
        IReadOnlyList<Point[]> source = _glyphPathCache.GetOrAdd(
            outline, FlattenGlyphOutlineCore);
        var paths = new List<List<Point>>(source.Count);
        foreach (Point[] sourcePath in source)
        {
            var path = new List<Point>(sourcePath.Length);
            foreach (Point point in sourcePath)
                path.Add(transform.Apply(point.X, point.Y));
            paths.Add(path);
        }
        return paths;
    }

    private static IReadOnlyList<Point[]> FlattenGlyphOutlineCore(
        PdfGlyphOutline outline)
    {
        var paths = new List<Point[]>(outline.Contours.Count);
        foreach (PdfGlyphContour contour in outline.Contours)
        {
            IReadOnlyList<PdfGlyphPoint> points = contour.Points;
            if (points.Count == 0) continue;
            PdfGlyphPoint first = points[0], last = points[^1];
            if (points.Any(point => point.IsCubicControl))
            {
                if (!first.OnCurve) continue;
                var cubicPath = new List<Point> { new(first.X, first.Y) };
                bool valid = true;
                for (int cubicIndex = 1; cubicIndex < points.Count;)
                {
                    PdfGlyphPoint point = points[cubicIndex];
                    if (point.OnCurve)
                    {
                        cubicPath.Add(new Point(point.X, point.Y));
                        cubicIndex++;
                    }
                    else if (point.IsCubicControl && cubicIndex + 2 < points.Count
                        && points[cubicIndex + 1].IsCubicControl
                        && points[cubicIndex + 2].OnCurve)
                    {
                        AddCubic(cubicPath, cubicPath[^1],
                            new Point(point.X, point.Y),
                            new Point(points[cubicIndex + 1].X,
                                points[cubicIndex + 1].Y),
                            new Point(points[cubicIndex + 2].X,
                                points[cubicIndex + 2].Y));
                        cubicIndex += 3;
                    }
                    else
                    {
                        valid = false;
                        break;
                    }
                }
                if (!valid) continue;
                if (cubicPath[^1] != cubicPath[0]) cubicPath.Add(cubicPath[0]);
                paths.Add(cubicPath.ToArray());
                continue;
            }
            PdfGlyphPoint start;
            int index, consumed;
            if (first.OnCurve)
            {
                start = first;
                index = 1;
                consumed = 1;
            }
            else if (last.OnCurve)
            {
                start = last;
                index = 0;
                consumed = 1;
            }
            else
            {
                start = Midpoint(last, first);
                index = 0;
                consumed = 0;
            }
            var path = new List<Point> { new(start.X, start.Y) };
            PdfGlyphPoint current = start;
            while (consumed < points.Count)
            {
                PdfGlyphPoint point = points[index % points.Count];
                if (point.OnCurve)
                {
                    path.Add(new Point(point.X, point.Y));
                    current = point;
                    index++;
                    consumed++;
                    continue;
                }
                PdfGlyphPoint next = points[(index + 1) % points.Count];
                PdfGlyphPoint end = next.OnCurve ? next : Midpoint(point, next);
                for (int step = 1; step <= 12; step++)
                {
                    double t = step / 12d, u = 1 - t;
                    path.Add(new Point(
                        u * u * current.X + 2 * u * t * point.X + t * t * end.X,
                        u * u * current.Y + 2 * u * t * point.Y + t * t * end.Y));
                }
                current = end;
                index++;
                consumed++;
                if (next.OnCurve)
                {
                    index++;
                    consumed++;
                }
            }
            if (path[^1] != path[0]) path.Add(path[0]);
            paths.Add(path.ToArray());
        }
        return paths;

        static PdfGlyphPoint Midpoint(PdfGlyphPoint first, PdfGlyphPoint second) =>
            new((first.X + second.X) / 2, (first.Y + second.Y) / 2, true);
    }

    private static void SetPixel(RasterSurface pixels, int width, int x, int y,
        in Color color, double opacity, RendererBlendMode blendMode,
        GraphicsSoftMask? graphicsSoftMask = null, KnockoutState? knockout = null)
    {
        if (color.DoesNotPaint || !pixels.Contains(x, y)) return;
        int offset = pixels.Offset(x, y);
        knockout?.PreparePixel(pixels, x, y);
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        if (graphicsSoftMask is not null)
            sourceAlpha *= graphicsSoftMask.At(x, y) / 255d;
        if (pixels.GroupAlpha is not null)
        {
            int index = offset / 4;
            pixels.GroupAlpha[index] = (byte)Math.Round(sourceAlpha * 255
                + pixels.GroupAlpha[index] * (1 - sourceAlpha));
        }
        if (pixels.Ink is not null)
        {
            SetInkPixel(pixels, offset, color, sourceAlpha, blendMode);
            return;
        }
        if (pixels.RgbProfile is not null)
            SetRgbPixel(pixels, offset, pixels.GetRgb(color), sourceAlpha, blendMode);
        else SetRgbPixel(pixels, offset, color, sourceAlpha, blendMode);
    }

    private static void SetRgbPixel(RasterSurface pixels, int offset, in Color color,
        double sourceAlpha, RendererBlendMode blendMode)
    {
        double targetAlpha = pixels[offset + 3] / 255d;
        double outputAlpha = sourceAlpha + targetAlpha * (1 - sourceAlpha);
        if (outputAlpha <= 0) return;
        (double blendRed, double blendGreen, double blendBlue) = blendMode switch
        {
            RendererBlendMode.Hue or RendererBlendMode.Saturation
                or RendererBlendMode.Color or RendererBlendMode.Luminosity =>
                BlendNonSeparable(pixels[offset + 2] / 255d, pixels[offset + 1] / 255d,
                    pixels[offset] / 255d, color.Red / 255d, color.Green / 255d,
                    color.Blue / 255d, blendMode),
            _ => (BlendChannel(pixels[offset + 2] / 255d, color.Red / 255d, blendMode),
                BlendChannel(pixels[offset + 1] / 255d, color.Green / 255d, blendMode),
                BlendChannel(pixels[offset] / 255d, color.Blue / 255d, blendMode))
        };
        pixels[offset] = Composite(color.Blue, pixels[offset], blendBlue);
        pixels[offset + 1] = Composite(color.Green, pixels[offset + 1], blendGreen);
        pixels[offset + 2] = Composite(color.Red, pixels[offset + 2], blendRed);
        pixels[offset + 3] = (byte)Math.Round(outputAlpha * 255);

        byte Composite(byte sourceByte, byte targetByte, double blended)
        {
            double source = sourceByte / 255d, target = targetByte / 255d;
            double value = ((1 - targetAlpha) * sourceAlpha * source
                + (1 - sourceAlpha) * targetAlpha * target
                + sourceAlpha * targetAlpha * blended) / outputAlpha;
            return (byte)Math.Round(Math.Clamp(value, 0, 1) * 255);
        }
    }

    private static double BlendChannel(
        double backdrop, double source, RendererBlendMode mode) => mode switch
    {
        RendererBlendMode.Multiply => backdrop * source,
        RendererBlendMode.Screen => backdrop + source - backdrop * source,
        RendererBlendMode.Overlay => HardLight(source, backdrop),
        RendererBlendMode.Darken => Math.Min(backdrop, source),
        RendererBlendMode.Lighten => Math.Max(backdrop, source),
        RendererBlendMode.ColorDodge => source >= 1 ? 1 : Math.Min(1, backdrop / (1 - source)),
        RendererBlendMode.ColorBurn => source <= 0 ? 0 : 1 - Math.Min(1, (1 - backdrop) / source),
        RendererBlendMode.HardLight => HardLight(backdrop, source),
        RendererBlendMode.SoftLight => source <= 0.5
            ? backdrop - (1 - 2 * source) * backdrop * (1 - backdrop)
            : backdrop + (2 * source - 1) * (SoftLightD(backdrop) - backdrop),
        RendererBlendMode.Difference => Math.Abs(backdrop - source),
        RendererBlendMode.Exclusion => backdrop + source - 2 * backdrop * source,
        _ => source
    };

    private static double HardLight(double backdrop, double source) => source <= 0.5
        ? 2 * backdrop * source : 1 - 2 * (1 - backdrop) * (1 - source);

    private static double SoftLightD(double value) => value <= 0.25
        ? ((16 * value - 12) * value + 4) * value : Math.Sqrt(value);

    private static (double Red, double Green, double Blue) BlendNonSeparable(
        double backdropRed, double backdropGreen, double backdropBlue,
        double sourceRed, double sourceGreen, double sourceBlue, RendererBlendMode mode)
    {
        var backdrop = new ColorVector(backdropRed, backdropGreen, backdropBlue);
        var source = new ColorVector(sourceRed, sourceGreen, sourceBlue);
        ColorVector result = mode switch
        {
            RendererBlendMode.Hue => SetLum(SetSat(source, Sat(backdrop)), Lum(backdrop)),
            RendererBlendMode.Saturation => SetLum(SetSat(backdrop, Sat(source)), Lum(backdrop)),
            RendererBlendMode.Color => SetLum(source, Lum(backdrop)),
            _ => SetLum(backdrop, Lum(source))
        };
        return (result.Red, result.Green, result.Blue);

        static double Lum(ColorVector color) =>
            0.3 * color.Red + 0.59 * color.Green + 0.11 * color.Blue;

        static double Sat(ColorVector color) =>
            Math.Max(color.Red, Math.Max(color.Green, color.Blue))
            - Math.Min(color.Red, Math.Min(color.Green, color.Blue));

        static ColorVector SetLum(ColorVector color, double luminosity)
        {
            double difference = luminosity - Lum(color);
            return ClipColor(new ColorVector(color.Red + difference,
                color.Green + difference, color.Blue + difference));
        }

        static ColorVector SetSat(ColorVector color, double saturation)
        {
            double red = color.Red, green = color.Green, blue = color.Blue;
            int minimum = red <= green && red <= blue ? 0 : green <= blue ? 1 : 2;
            int maximum = red >= green && red >= blue ? 0 : green >= blue ? 1 : 2;
            double minimumValue = Value(minimum), maximumValue = Value(maximum);
            if (maximumValue == minimumValue) return new ColorVector(0, 0, 0);
            int middle = 3 - minimum - maximum;
            Set(middle, (Value(middle) - minimumValue) * saturation
                / (maximumValue - minimumValue));
            Set(maximum, saturation);
            Set(minimum, 0);
            return new ColorVector(red, green, blue);

            double Value(int index) => index switch { 0 => red, 1 => green, _ => blue };
            void Set(int index, double value)
            {
                if (index == 0) red = value;
                else if (index == 1) green = value;
                else blue = value;
            }
        }

        static ColorVector ClipColor(ColorVector color)
        {
            double luminosity = Lum(color);
            double red = color.Red, green = color.Green, blue = color.Blue;
            double minimum = Math.Min(red, Math.Min(green, blue));
            double maximum = Math.Max(red, Math.Max(green, blue));
            if (minimum < 0)
            {
                red = luminosity + (red - luminosity) * luminosity / (luminosity - minimum);
                green = luminosity + (green - luminosity) * luminosity / (luminosity - minimum);
                blue = luminosity + (blue - luminosity) * luminosity / (luminosity - minimum);
            }
            if (maximum > 1)
            {
                red = luminosity + (red - luminosity) * (1 - luminosity) / (maximum - luminosity);
                green = luminosity + (green - luminosity) * (1 - luminosity) / (maximum - luminosity);
                blue = luminosity + (blue - luminosity) * (1 - luminosity) / (maximum - luminosity);
            }
            return new ColorVector(red, green, blue);
        }
    }

    private static GraphicsState ApplyPendingClip(GraphicsState state,
        IReadOnlyList<List<Point>> path, ref bool? pendingClipEvenOdd, RasterFrame frame)
    {
        if (!pendingClipEvenOdd.HasValue) return state;
        CoverageMask mask = RasterizeClip(path, pendingClipEvenOdd.Value, frame);
        pendingClipEvenOdd = null;
        IReadOnlyList<ClipRegion> clips = AddClip(state.Clips, mask);
        CoverageMask combined = clips[0].Mask;
        if (!ReferenceEquals(mask, combined)) ClipScratchScope.Current?.ReleaseTemporary(mask);
        foreach (ClipRegion old in state.Clips)
            if (!ReferenceEquals(old.Mask, combined)) ClipScratchScope.Current?.ReleaseTemporary(old.Mask);
        return state with { Clips = clips };
    }

    private static IReadOnlyList<List<Point>> CreateDashedPaths(
        IReadOnlyList<List<Point>> paths, Matrix transform,
        IReadOnlyList<double> suppliedPattern, double suppliedPhase)
    {
        if (!transform.TryInverse(out Matrix inverse)) return paths;
        double[] pattern = suppliedPattern.Count % 2 == 0
            ? suppliedPattern.ToArray()
            : [.. suppliedPattern, .. suppliedPattern];
        double cycle = pattern.Sum();
        var result = new List<List<Point>>();
        foreach (List<Point> path in paths)
        {
            int patternIndex = 0;
            bool paints = true;
            double remaining = pattern[0];
            double phase = suppliedPhase % cycle;
            AdvancePastEmptyEntries();
            while (phase > 0)
            {
                if (phase < remaining)
                {
                    remaining -= phase;
                    phase = 0;
                }
                else
                {
                    phase -= remaining;
                    AdvancePattern();
                    AdvancePastEmptyEntries();
                }
            }

            List<Point>? painted = null;
            for (int segment = 1; segment < path.Count; segment++)
            {
                Point pageStart = path[segment - 1], pageEnd = path[segment];
                Point userStart = inverse.Apply(pageStart.X, pageStart.Y);
                Point userEnd = inverse.Apply(pageEnd.X, pageEnd.Y);
                double userX = userEnd.X - userStart.X;
                double userY = userEnd.Y - userStart.Y;
                double userLength = Math.Sqrt(userX * userX + userY * userY);
                if (userLength <= 1e-12) continue;
                double used = 0;
                while (used < userLength - 1e-12)
                {
                    AdvancePastEmptyEntries();
                    double length = Math.Min(remaining, userLength - used);
                    double startUnit = used / userLength;
                    double endUnit = (used + length) / userLength;
                    Point start = Lerp(pageStart, pageEnd, startUnit);
                    Point end = Lerp(pageStart, pageEnd, endUnit);
                    if (paints)
                    {
                        if (painted is null || painted[^1] != start)
                        {
                            painted = [start];
                            result.Add(painted);
                        }
                        painted.Add(end);
                    }
                    else painted = null;
                    used += length;
                    remaining -= length;
                    if (remaining <= 1e-12) AdvancePattern();
                }
            }

            void AdvancePattern()
            {
                patternIndex = (patternIndex + 1) % pattern.Length;
                paints = !paints;
                remaining = pattern[patternIndex];
            }

            void AdvancePastEmptyEntries()
            {
                while (remaining <= 1e-12) AdvancePattern();
            }
        }
        return result;

        static Point Lerp(Point from, Point to, double amount) => new(
            from.X + (to.X - from.X) * amount,
            from.Y + (to.Y - from.Y) * amount);
    }

    private static bool Contains(IReadOnlyList<Point[]> polygons,
        bool evenOdd, double x, double y)
    {
        int winding = 0;
        int crossings = 0;
        foreach (Point[] polygon in polygons)
        {
            for (int current = 0, previous = polygon.Length - 1;
                current < polygon.Length; previous = current++)
            {
                Point from = polygon[previous], to = polygon[current];
                if ((from.Y > y) == (to.Y > y)) continue;
                double intersection = (to.X - from.X) * (y - from.Y)
                    / (to.Y - from.Y) + from.X;
                if (x >= intersection) continue;
                crossings++;
                winding += to.Y > from.Y ? 1 : -1;
            }
        }
        return evenOdd ? crossings % 2 != 0 : winding != 0;
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("A rendering operand is not numeric.")
    };

    private Color ReadPaintColor(ImageColorSpace colorSpace,
        IReadOnlyList<PdfObject> operands, Color current, ICollection<string> diagnostics,
        double[]? currentComponents, out double[]? sourceComponents)
    {
        sourceComponents = currentComponents;
        if (operands.Count != colorSpace.Components)
        {
            if (!_document.UsesCompatibilityRecovery)
                throw new FormatException("A color operator has the wrong component count.");
            diagnostics.Add("A color operator with an invalid component count was recovered.");
            if (operands.Count < colorSpace.Components) return current;
        }
        var components = new double[colorSpace.Components];
        for (int index = 0; index < components.Length; index++)
            components[index] = Number(Resolve(operands[index]));
        Color result = colorSpace.Convert(components);
        sourceComponents = colorSpace.HasProcessColorants ? components : null;
        return result;
    }

    private readonly record struct GraphicsState(
        Matrix Transform, Color Fill, Color Stroke, double FillAlpha, double StrokeAlpha,
        double LineWidth, RendererLineCap LineCap, RendererLineJoin LineJoin,
        double MiterLimit,
        IReadOnlyList<double> DashPattern, double DashPhase,
        RendererBlendMode BlendMode,
        IReadOnlyList<ClipRegion> Clips, bool FillPatternSpace,
        ImageColorSpace? FillPatternBase, PatternPaint? FillPattern,
        bool StrokePatternSpace, ImageColorSpace? StrokePatternBase,
        PatternPaint? StrokePattern, ImageColorSpace? FillColorSpace,
        ImageColorSpace? StrokeColorSpace, GraphicsSoftMask? GraphicsSoftMask,
        KnockoutState? Knockout, bool FillOverprint = false, bool StrokeOverprint = false,
        int OverprintMode = 0, double[]? FillComponents = null, double[]? StrokeComponents = null)
    {
        internal Color PaintFill => OverprintColor(Fill, FillColorSpace, FillOverprint, OverprintMode);
        internal Color PaintStroke => OverprintColor(Stroke, StrokeColorSpace, StrokeOverprint, OverprintMode);
    }
    private enum RendererLineCap { Butt, Round, ProjectingSquare }
    private enum RendererLineJoin { Miter, Round, Bevel }
    private enum RendererBlendMode
    {
        Normal, Compatible, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge,
        ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color,
        Luminosity
    }
    private readonly record struct ColorVector(double Red, double Green, double Blue);
    /// <summary>A device-space clip held as an anti-aliased coverage mask.</summary>
    private sealed class ClipRegion
    {
        internal ClipRegion(CoverageMask mask)
        {
            Mask = mask;
        }

        internal CoverageMask Mask { get; }
    }

    /// <summary>Intersects a new clip mask with the active clips into one region.</summary>
    private static IReadOnlyList<ClipRegion> AddClip(IReadOnlyList<ClipRegion> clips,
        CoverageMask mask)
    {
        CoverageMask combined = mask;
        foreach (ClipRegion clip in clips)
            combined = CoverageMask.Intersect(combined, clip.Mask);
        return [new ClipRegion(combined)];
    }
    private sealed record SoftMask(byte[] Samples, int Width, int Height,
        int Bits = 8, double DecodeStart = 0, double DecodeEnd = 1)
    {
        internal long SumRow(int start, int end, int y)
        {
            long sum = 0;
            if (Bits == 8 && DecodeStart == 0 && DecodeEnd == 1)
            {
                ReadOnlySpan<byte> row = Samples.AsSpan(checked(y * Width + start), end - start);
                while (row.Length >= 8)
                {
                    ulong values = System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(row);
                    // Sum adjacent byte pairs in wider lanes so carries cannot cross samples.
                    values = (values & 0x00FF00FF00FF00FFUL)
                        + ((values >> 8) & 0x00FF00FF00FF00FFUL);
                    values = (values & 0x0000FFFF0000FFFFUL)
                        + ((values >> 16) & 0x0000FFFF0000FFFFUL);
                    sum += (long)((values & uint.MaxValue) + (values >> 32));
                    row = row[8..];
                }
                foreach (byte value in row) sum += value;
                return sum;
            }
            if (Bits == 1 && DecodeStart == 0 && DecodeEnd == 1)
            {
                int row = checked(y * ((Width + 7) / 8));
                while (start < end && (start & 7) != 0) sum += Sample(start++, y);
                while (start + 8 <= end)
                {
                    sum += System.Numerics.BitOperations.PopCount((uint)Samples[row + start / 8]) * 255L;
                    start += 8;
                }
            }
            while (start < end) sum += Sample(start++, y);
            return sum;
        }

        internal byte Sample(int x, int y)
        {
            int rowBytes = checked((Width * Bits + 7) / 8);
            int byteOffset = checked(y * rowBytes + x * Bits / 8);
            uint value = Bits switch
            {
                8 => Samples[byteOffset],
                16 => (uint)(Samples[byteOffset] * 256 + Samples[byteOffset + 1]),
                _ => (uint)(Samples[byteOffset] >> (8 - Bits - x * Bits % 8))
                    & ((1u << Bits) - 1)
            };
            if (Bits == 8 && DecodeStart == 0 && DecodeEnd == 1) return (byte)value;
            if (Bits == 1)
            {
                if (DecodeStart == 0 && DecodeEnd == 1) return value == 0 ? (byte)0 : (byte)255;
                if (DecodeStart == 1 && DecodeEnd == 0) return value == 0 ? (byte)255 : (byte)0;
            }
            double decoded = DecodeStart + value / (double)((1u << Bits) - 1)
                * (DecodeEnd - DecodeStart);
            return (byte)Math.Round(Math.Clamp(decoded, 0, 1) * 255);
        }
    }
    private sealed record GraphicsSoftMask(byte[]? Samples, int Left, int Top, int Width, int Height,
        byte Outside, byte Constant)
    {
        internal byte At(int x, int y) => (uint)(x - Left) < (uint)Width && (uint)(y - Top) < (uint)Height
            ? Samples is null ? Constant : Samples[(y - Top) * Width + x - Left] : Outside;
    }
    private sealed class KnockoutState
    {
        private readonly int[] _objects;
        private readonly byte[]? _backdrop;
        private readonly byte[]? _backdropInk;
        private readonly PdfColorTransform? _backdropInkProfile;
        private readonly PdfColorTransform? _backdropRgbProfile;
        private readonly byte[]? _backdropAlpha;
        private readonly byte[]? _backdropGroupAlpha;
        private readonly int _pageWidth;
        private readonly int _left;
        private readonly int _top;
        private readonly int _width;
        private int _currentObject;

        internal KnockoutState(int pageWidth, (int Left, int Top, int Right, int Bottom) bounds,
            RasterSurface? backdrop = null)
        {
            _pageWidth = pageWidth;
            _left = bounds.Left;
            _top = bounds.Top;
            _width = bounds.Right - bounds.Left;
            int height = bounds.Bottom - bounds.Top;
            _objects = new int[checked(_width * height)];
            if (backdrop is not null)
            {
                _backdropRgbProfile = backdrop.RgbProfile;
                _backdrop = new byte[checked(_objects.Length * 4)];
                if (backdrop.Ink is not null)
                {
                    _backdropInk = _backdrop;
                    _backdropInkProfile = backdrop.InkProfile;
                    _backdropAlpha = new byte[_objects.Length];
                }
                if (backdrop.GroupAlpha is not null) _backdropGroupAlpha = new byte[_objects.Length];
                for (int row = 0; row < height; row++)
                {
                    backdrop.Data.AsSpan(backdrop.Offset(_left, row + _top), _width * 4)
                        .CopyTo(_backdrop.AsSpan(row * _width * 4));
                    if (_backdropAlpha is not null)
                        backdrop.CopyInkAlphaTo(backdrop.Offset(_left, row + _top),
                            _backdropAlpha.AsSpan(row * _width, _width));
                    if (_backdropGroupAlpha is not null)
                        backdrop.GroupAlpha!.AsSpan(backdrop.Offset(_left, row + _top) / 4, _width)
                            .CopyTo(_backdropGroupAlpha.AsSpan(row * _width));
                }
            }
        }

        internal void BeginObject()
        {
            if (_currentObject == int.MaxValue)
            {
                Array.Clear(_objects);
                _currentObject = 0;
            }
            _currentObject++;
        }

        private int LocalPixel(int offset)
        {
            int pagePixel = offset / 4;
            int y = pagePixel / _pageWidth;
            int x = pagePixel - y * _pageWidth;
            return (y - _top) * _width + x - _left;
        }

        internal bool WasTouched(int offset) => _objects[LocalPixel(offset)] != 0;

        internal void PreparePixel(RasterSurface target, int x, int y)
        {
            int offset = target.Offset(x, y);
            int pixel = (y - _top) * _width + x - _left;
            if (_objects[pixel] == _currentObject) return;
            _objects[pixel] = _currentObject;
            if (target.GroupAlpha is not null)
                target.GroupAlpha[offset / 4] = _backdropGroupAlpha?[pixel] ?? 0;
            if (_backdrop is null)
            {
                target.Data.AsSpan(offset, 4).Clear();
                target.SetAlpha(offset, 0);
                return;
            }
            if ((target.Ink is null) == (_backdropInk is null)
                && ReferenceEquals(target.InkProfile, _backdropInkProfile)
                && ReferenceEquals(target.RgbProfile, _backdropRgbProfile))
            {
                _backdrop.AsSpan(pixel * 4, 4).CopyTo(target.Data.AsSpan(offset, 4));
                if (_backdropAlpha is not null) target.SetAlpha(offset, _backdropAlpha[pixel]);
                return;
            }
            Color color = _backdropInk is not null ? InkColor(ReadInk(_backdropInk, pixel * 4), _backdropInkProfile)
                : new(_backdrop[pixel * 4 + 2], _backdrop[pixel * 4 + 1], _backdrop[pixel * 4]);
            if (_backdropRgbProfile is not null) color = ProfileRgbToDisplay(color, _backdropRgbProfile);
            if (target.Ink is not null) WriteInk(target.Ink, offset, target.GetInk(color));
            else
            {
                color = ColorRgb(color, target.RgbProfile);
                target[offset] = color.Blue;
                target[offset + 1] = color.Green;
                target[offset + 2] = color.Red;
            }
            target.SetAlpha(offset, _backdropAlpha?[pixel] ?? _backdrop[pixel * 4 + 3]);
        }
    }
    private sealed record PatternPaint(PdfStream? Tiling, PdfObject? Shading,
        Matrix Matrix, Color? BaseColor);
    private readonly record struct MeshVertex(Point Point, double[] Values);
    private sealed class MeshDecoder(
        byte[] source, int coordinateBits, int componentBits, int flagBits,
        double[] decode, int dataComponents, ImageColorSpace colorSpace,
        Func<double, Color>? function, Matrix transform)
    {
        private int _bitOffset;
        internal ImageColorSpace ColorSpace => colorSpace;
        private int VertexBits => checked(coordinateBits * 2 + componentBits * dataComponents);
        internal bool HasData => source.Length * 8 - _bitOffset
            >= VertexBits + (flagBits == 0 ? 0 : flagBits);

        internal uint ReadFlag()
        {
            if (flagBits == 0) throw new InvalidOperationException();
            return Read(flagBits);
        }

        internal MeshVertex ReadVertex()
        {
            double x = Decode(Read(coordinateBits), coordinateBits, decode[0], decode[1]);
            double y = Decode(Read(coordinateBits), coordinateBits, decode[2], decode[3]);
            var values = new double[dataComponents];
            for (int component = 0; component < values.Length; component++)
                values[component] = Decode(Read(componentBits), componentBits,
                    decode[4 + component * 2], decode[5 + component * 2]);
            return new MeshVertex(transform.Apply(x, y), values);
        }

        internal Color Convert(double[] values) =>
            function is null ? colorSpace.Convert(values) : function(values[0]);

        private uint Read(int bits)
        {
            if (_bitOffset + bits > source.Length * 8)
                throw new FormatException("A mesh shading stream is truncated.");
            uint value = ReadPackedSample(source, _bitOffset, bits);
            _bitOffset += bits;
            return value;
        }

        private static double Decode(uint value, int bits, double minimum, double maximum)
        {
            double limit = bits == 32 ? uint.MaxValue : (1u << bits) - 1u;
            return minimum + value / limit * (maximum - minimum);
        }
    }
    private enum InitialColor { Zero, BlackInk, FullTint }

    private sealed record ImageColorSpace(int Components, Color[]? Palette,
        Func<double, double, double, double, Color>? Converter = null,
        double[]? DefaultDecode = null, Func<double[], Color>? MultiConverter = null,
        PdfColorTransform? Profile = null, ImageColorSpace? PaletteBase = null,
        byte[]? PaletteSamples = null, bool IsIccBased = false, double[]? ComponentRange = null,
        int[]? ProcessChannels = null, byte? NativeProcessMask = null, ImageColorSpace? SourceSpace = null,
        bool SuppressPainting = false, bool RegistrationColor = false, InitialColor Initial = InitialColor.Zero)
    {
        internal bool DoesNotPaint => SuppressPainting || PaletteBase?.SuppressPainting == true;
        internal bool HasProcessColorants => RegistrationColor || ProcessChannels is not null
            || PaletteBase?.HasProcessColorants == true;

        internal Color InitialPaint(out double[]? components)
        {
            components = null;
            double tint = Initial == InitialColor.FullTint ? 1 : 0;
            if (HasProcessColorants || MultiConverter is not null)
            {
                var values = new double[Components];
                if (tint != 0) Array.Fill(values, tint);
                if (HasProcessColorants) components = values;
                return Convert(values);
            }
            return Convert(tint, tint, tint, Initial == InitialColor.BlackInk ? 1 : tint);
        }

        internal ImageColorSpace ForDestination(RasterSurface destination) =>
            NativeProcessMask.HasValue && destination.Ink is not null ? this
            : SourceSpace is not null ? SourceSpace.ForDestination(destination)
            : PaletteBase is { HasProcessColorants: true } && destination.Ink is not null
                ? BindProcessPalette(destination)
            : RegistrationColor && destination.Ink is not null
                ? this with
                {
                    Converter = (tint, _, _, _) => Color.Cmyk(tint, tint, tint, tint),
                    NativeProcessMask = 15,
                    SourceSpace = this
                }
            : ProcessChannels is { } channels && destination.Ink is not null
                ? this with
                {
                    Converter = channels.Length <= 4
                        ? (first, second, third, fourth) => ProcessColor(channels, first, second, third, fourth) : null,
                    MultiConverter = channels.Length > 4 ? values => ProcessColor(channels, values) : null,
                    SourceSpace = this,
                    NativeProcessMask = (byte)channels.Aggregate(0,
                        (mask, channel) => channel >= 0 ? mask | (1 << channel) : mask)
                }
            : Components == 4 && Profile is not null && destination.Ink is not null
                && ReferenceEquals(Profile, destination.InkProfile)
                ? this with { Profile = null } : this;

        private ImageColorSpace BindProcessPalette(RasterSurface destination)
        {
            ImageColorSpace mapped = PaletteBase!.ForDestination(destination);
            return this with
            {
                Palette = BuildPalette(mapped, PaletteSamples!, Palette!.Length),
                PaletteBase = mapped,
                NativeProcessMask = mapped.NativeProcessMask,
                SourceSpace = this
            };
        }

        internal static Color[] BuildPalette(ImageColorSpace space, byte[] samples, int count)
        {
            var palette = new Color[count];
            var values = new double[space.Components];
            for (int entry = 0; entry < count; entry++)
            {
                int offset = entry * space.Components;
                for (int component = 0; component < values.Length; component++)
                    values[component] = space.DefaultValue(component, samples[offset + component] / 255d);
                palette[entry] = space.Convert(values);
            }
            return palette;
        }

        internal Color Convert(double first, double second, double third, double fourth)
        {
            if (DoesNotPaint) return Color.NonPainting;
            if (ComponentRange is not null)
            {
                first = Math.Clamp(first, ComponentRange[0], ComponentRange[1]);
                if (Components > 1) second = Math.Clamp(second, ComponentRange[2], ComponentRange[3]);
                if (Components > 2) third = Math.Clamp(third, ComponentRange[4], ComponentRange[5]);
                if (Components > 3) fourth = Math.Clamp(fourth, ComponentRange[6], ComponentRange[7]);
            }
            return Palette is not null
                ? Palette[Math.Clamp((int)Math.Round(first), 0, Palette.Length - 1)]
                : Profile is not null && Converter is null ? ConvertProfileColor(Profile, first, second, third, fourth)
                : Converter?.Invoke(first, second, third, fourth) ?? Components switch
            {
                1 => Color.Gray(first),
                3 => Color.Rgb(first, second, third),
                _ => Color.Cmyk(first, second, third, fourth)
            };
        }
        internal Color Convert(ReadOnlySpan<double> values)
        {
            if (DoesNotPaint) return Color.NonPainting;
            if (MultiConverter is not null) throw new NotSupportedException();
            return Convert(values[0], values.Length > 1 ? values[1] : 0,
                values.Length > 2 ? values[2] : 0, values.Length > 3 ? values[3] : 0);
        }
        internal Color Convert(double[] values)
        {
            if (DoesNotPaint) return Color.NonPainting;
            if (Palette is not null) return Palette[Math.Clamp((int)Math.Round(values[0]), 0, Palette.Length - 1)];
            if (MultiConverter is null) return Convert((ReadOnlySpan<double>)values);
            double[] constrained = values;
            if (ComponentRange is not null)
                for (int component = 0; component < Components; component++)
                {
                    double value = Math.Clamp(values[component], ComponentRange[component * 2], ComponentRange[component * 2 + 1]);
                    if (value == values[component]) continue;
                    if (ReferenceEquals(constrained, values)) constrained = (double[])values.Clone();
                    constrained[component] = value;
                }
            return MultiConverter(constrained);
        }
        internal double DefaultValue(int component, double normalized) => DefaultDecode is null
            ? normalized : DefaultDecode[component * 2] + normalized
                * (DefaultDecode[component * 2 + 1] - DefaultDecode[component * 2]);
    }
    private sealed class BoundedCache<TKey, TValue> where TKey : notnull
    {
        private readonly int _capacity;
        private readonly long _maximumWeight;
        private readonly Func<TValue, long> _weight;
        private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value, long Weight)>> _entries;
        private readonly LinkedList<(TKey Key, TValue Value, long Weight)> _usage = [];
        // System.Threading.Lock takes its uncontended fast path without the monitor slow path
        // that showed up under the per-glyph cache lookups.
        private readonly Lock _sync = new();
        private long _currentWeight;

        internal BoundedCache(int capacity, IEqualityComparer<TKey>? comparer = null,
            long maximumWeight = long.MaxValue, Func<TValue, long>? weight = null)
        {
            _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
            _maximumWeight = maximumWeight > 0 ? maximumWeight
                : throw new ArgumentOutOfRangeException(nameof(maximumWeight));
            _weight = weight ?? (_ => 1);
            _entries = new Dictionary<TKey,
                LinkedListNode<(TKey Key, TValue Value, long Weight)>>(comparer);
        }

        internal int Count
        {
            get { lock (_sync) return _entries.Count; }
        }

        internal TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
        {
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    _usage.Remove(existing);
                    _usage.AddFirst(existing);
                    return existing.Value.Value;
                }
            }

            TValue value = factory(key);
            lock (_sync)
            {
                if (_entries.TryGetValue(key, out var existing))
                {
                    _usage.Remove(existing);
                    _usage.AddFirst(existing);
                    return existing.Value.Value;
                }
                long valueWeight = _weight(value);
                if (valueWeight < 0) throw new InvalidOperationException("A cache entry has a negative weight.");
                if (valueWeight > _maximumWeight) return value;
                var node = _usage.AddFirst((key, value, valueWeight));
                _entries.Add(key, node);
                _currentWeight += valueWeight;
                while (_entries.Count > _capacity || _currentWeight > _maximumWeight)
                {
                    LinkedListNode<(TKey Key, TValue Value, long Weight)> oldest = _usage.Last!;
                    _usage.RemoveLast();
                    _entries.Remove(oldest.Value.Key);
                    _currentWeight -= oldest.Value.Weight;
                }
                return value;
            }
        }
    }
    private readonly record struct RenderCacheKey(
        int PageIndex, int Width, int Height, bool TransparentBackground,
        bool IncludeAnnotations, bool IncludeFormFields);
    private readonly record struct ImageCacheKey(PdfStream Stream, int ResolutionLevel,
        int MaskWidth = 0, int MaskHeight = 0);
    private sealed record DecodedImage(byte[] Samples, int Width, int Height, byte[]? Alpha = null,
        int MaskBits = 8, double MaskDecodeStart = 0, double MaskDecodeEnd = 1);
    private sealed record ParsedStream(
        IReadOnlyList<PdfContentInstruction> Instructions, int SourceBytes);
    private readonly record struct Point(double X, double Y);
    private readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        internal static Matrix Identity => new(1, 0, 0, 1, 0, 0);
        internal static Matrix From(IReadOnlyList<PdfObject> values) => new(
            Number(values[0]), Number(values[1]), Number(values[2]),
            Number(values[3]), Number(values[4]), Number(values[5]));
        internal Matrix Then(Matrix next) => new(
            A * next.A + B * next.C, A * next.B + B * next.D,
            C * next.A + D * next.C, C * next.B + D * next.D,
            E * next.A + F * next.C + next.E, E * next.B + F * next.D + next.F);
        internal Point Apply(double x, double y) =>
            new(x * A + y * C + E, x * B + y * D + F);
        internal double StrokeScale => Math.Sqrt(Math.Abs(A * D - B * C));
        internal bool TryInverse(out Matrix inverse)
        {
            double determinant = A * D - B * C;
            if (!double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12)
            {
                inverse = default;
                return false;
            }
            inverse = new Matrix(D / determinant, -B / determinant,
                -C / determinant, A / determinant,
                (C * F - D * E) / determinant, (B * E - A * F) / determinant);
            return true;
        }
    }

    private readonly record struct Color(byte Red, byte Green, byte Blue)
    {
        internal byte OverprintComponents { get; init; }
        internal bool DoesNotPaint => (OverprintComponents & 32) != 0;
        internal static Color NonPainting => new(0, 0, 0) { OverprintComponents = 32 };
        internal uint? Ink { get; init; }
        internal PdfColorTransform? InkProfile { get; init; }
        internal (double X, double Y, double Z)? Connection { get; init; }
        internal static Color Black => new(0, 0, 0) { Ink = 0xFF000000 };
        internal static Color White => new(255, 255, 255);
        internal static Color Gray(double gray)
        {
            byte value = Channel(gray);
            return new(value, value, value) { Ink = (uint)(255 - value) << 24 };
        }
        internal static Color Rgb(double red, double green, double blue) =>
            new(Channel(red), Channel(green), Channel(blue));
        internal static Color LinearRgb(double red, double green, double blue) =>
            Rgb(Compand(red), Compand(green), Compand(blue));
        internal static Color Cmyk(double cyan, double magenta, double yellow, double black)
        {
            double light = 1 - Math.Clamp(black, 0, 1);
            return Rgb((1 - Math.Clamp(cyan, 0, 1)) * light,
                (1 - Math.Clamp(magenta, 0, 1)) * light,
                (1 - Math.Clamp(yellow, 0, 1)) * light) with
            {
                Ink = (uint)(Channel(cyan) | Channel(magenta) << 8
                    | Channel(yellow) << 16 | Channel(black) << 24),
                OverprintComponents = ZeroInkComponents(cyan, magenta, yellow, black)
            };
        }
        internal static byte ZeroInkComponents(double cyan, double magenta, double yellow, double black) =>
            (byte)((cyan <= 0 ? 1 : 0) | (magenta <= 0 ? 2 : 0)
                | (yellow <= 0 ? 4 : 0) | (black <= 0 ? 8 : 0));
        private static byte Channel(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
        private static double Compand(double value) => value <= 0.0031308
            ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    }
}
