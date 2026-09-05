using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Rendering;

/// <summary>Renders PDF page content through the engine-owned CPU raster pipeline.</summary>
public sealed class PdfPageRenderer
{
    private readonly PdfDocument _document;
    private readonly PdfPageContentReader _content;
    private readonly IReadOnlyList<PdfPageInformation> _pages;
    private readonly IReadOnlyList<PdfPageBoxInformation> _boxes;
    private readonly PdfPageTree _tree;

    /// <summary>Creates a renderer for an immutable document.</summary>
    public PdfPageRenderer(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (!document.IsDecrypted)
            throw new InvalidOperationException("Authenticate the document before rendering pages.");
        _content = new PdfPageContentReader(document);
        _tree = PdfPageTree.Read(document);
        _pages = PdfPageInformation.Read(document);
        _boxes = PdfPageBoxInformation.Read(document);
    }

    /// <summary>Renders the currently supported page operators into BGRA32 pixels.</summary>
    public PdfRenderedPage Render(int pageIndex, PdfRenderOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (pageIndex < 0 || pageIndex >= _pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        cancellationToken.ThrowIfCancellationRequested();

        byte background = options.TransparentBackground ? (byte)0 : (byte)255;
        byte[] pixels = GC.AllocateUninitializedArray<byte>(
            checked(options.Width * options.Height * 4));
        for (int y = 0; y < options.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int rowEnd = checked((y + 1) * options.Width * 4);
            for (int offset = y * options.Width * 4; offset < rowEnd; offset += 4)
            {
                pixels[offset] = 255;
                pixels[offset + 1] = 255;
                pixels[offset + 2] = 255;
                pixels[offset + 3] = background;
            }
        }

        PdfPageInformation page = _pages[pageIndex];
        PdfPageBoxBounds crop = _boxes[pageIndex].CropBox;
        bool quarterTurn = page.Rotation is 90 or 270;
        double displayWidth = quarterTurn ? page.Height : page.Width;
        double displayHeight = quarterTurn ? page.Width : page.Height;
        double scaleX = options.Width / displayWidth;
        double scaleY = options.Height / displayHeight;
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
            new ImageColorSpace(1, null), new ImageColorSpace(1, null));
        var diagnostics = new HashSet<string>();
        var activeForms = new HashSet<PdfStream>();
        var fontCache = new Dictionary<PdfDictionary, PdfExtractionFont>(
            ReferenceEqualityComparer.Instance);
        HashSet<int> hiddenOptionalContentGroups = PdfOptionalContentReader.Read(_document).Groups
            .Where(group => !group.IsInitiallyVisible)
            .Select(group => group.ObjectNumber).ToHashSet();
        PdfDictionary pageResources = PageResources(pageIndex);
        Process(_content.ReadInstructions(pageIndex, cancellationToken),
            pageResources, initialState, 0);
        RenderAppearances();
        return new PdfRenderedPage(options.Width, options.Height, pixels, diagnostics);

        void Process(IEnumerable<PdfContentInstruction> instructions,
            PdfDictionary resources, GraphicsState initial, int depth)
        {
            if (depth > 32) throw new FormatException("Form XObject nesting limit exceeded.");
            GraphicsState state = initial;
            var stack = new Stack<GraphicsState>();
            var path = new List<List<Point>>();
            List<Point>? subpath = null;
            var visibilityStack = new Stack<bool>();
            bool contentVisible = true;
            int compatibilityDepth = 0;
            var textClipPaths = new List<List<Point>>();
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
                switch (instruction.Operator)
                {
                case "q":
                    stack.Push(state);
                    break;
                case "Q":
                    state = stack.Count == 0 ? state : stack.Pop();
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
                        Fill = Color.Cmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3])),
                        FillColorSpace = new ImageColorSpace(4, null),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "cs" when values.Count == 1 && values[0] is PdfName fillSpace
                    && fillSpace.ValueAsLatin1() == "Pattern":
                    state = state with
                    {
                        FillPatternSpace = true,
                        FillColorSpace = null,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "cs" when values.Count == 1 && values[0] is PdfArray patternSpace
                    && patternSpace.Count == 2
                    && Resolve(patternSpace[0]) is PdfName patternKind
                    && patternKind.ValueAsLatin1() == "Pattern":
                    state = state with
                    {
                        FillPatternSpace = true,
                        FillColorSpace = null,
                        FillPatternBase = ReadColorSpace(patternSpace[1], resources, 0),
                        FillPattern = null
                    };
                    break;
                case "scn" when state.FillPatternSpace && values.Count > 0
                    && values[^1] is PdfName fillPatternName:
                    if (!TryGetTilingPattern(resources, fillPatternName,
                        out PdfStream? fillPattern, out int paintType)
                        || paintType == 1 && values.Count != 1
                        || paintType == 2 && (state.FillPatternBase is null
                            || values.Count != state.FillPatternBase.Components + 1))
                    {
                        diagnostics.Add("Tiling-pattern rendering is not implemented.");
                        break;
                    }
                    Color? baseColor = paintType == 2
                        ? state.FillPatternBase!.Convert(values.Take(values.Count - 1)
                            .Select(value => Number(Resolve(value))).ToArray())
                        : null;
                    state = state with
                    {
                        FillPattern = new TilingPatternPaint(fillPattern!, baseColor)
                    };
                    break;
                case "cs" when values.Count == 1:
                    state = state with
                    {
                        FillColorSpace = ReadColorSpace(values[0], resources, 0),
                        FillPatternSpace = false,
                        FillPatternBase = null,
                        FillPattern = null
                    };
                    break;
                case "sc" or "scn" when !state.FillPatternSpace
                    && state.FillColorSpace is not null:
                    state = state with
                    {
                        Fill = ReadPaintColor(state.FillColorSpace, values)
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
                        Stroke = Color.Cmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3])),
                        StrokeColorSpace = new ImageColorSpace(4, null),
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "CS" when values.Count == 1 && values[0] is PdfName strokeSpace
                    && strokeSpace.ValueAsLatin1() == "Pattern":
                    state = state with
                    {
                        StrokePatternSpace = true,
                        StrokeColorSpace = null,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "CS" when values.Count == 1 && values[0] is PdfArray strokePatternSpace
                    && strokePatternSpace.Count == 2
                    && Resolve(strokePatternSpace[0]) is PdfName strokePatternKind
                    && strokePatternKind.ValueAsLatin1() == "Pattern":
                    state = state with
                    {
                        StrokePatternSpace = true,
                        StrokeColorSpace = null,
                        StrokePatternBase = ReadColorSpace(strokePatternSpace[1], resources, 0),
                        StrokePattern = null
                    };
                    break;
                case "CS" when values.Count == 1:
                    state = state with
                    {
                        StrokeColorSpace = ReadColorSpace(values[0], resources, 0),
                        StrokePatternSpace = false,
                        StrokePatternBase = null,
                        StrokePattern = null
                    };
                    break;
                case "SC" or "SCN" when !state.StrokePatternSpace
                    && state.StrokeColorSpace is not null:
                    state = state with
                    {
                        Stroke = ReadPaintColor(state.StrokeColorSpace, values)
                    };
                    break;
                case "SCN" when state.StrokePatternSpace && values.Count > 0
                    && values[^1] is PdfName strokePatternName:
                    if (!TryGetTilingPattern(resources, strokePatternName,
                        out PdfStream? strokePattern, out int strokePaintType)
                        || strokePaintType == 1 && values.Count != 1
                        || strokePaintType == 2 && (state.StrokePatternBase is null
                            || values.Count != state.StrokePatternBase.Components + 1))
                    {
                        diagnostics.Add("Tiling-pattern rendering is not implemented.");
                        break;
                    }
                    Color? strokeBaseColor = strokePaintType == 2
                        ? state.StrokePatternBase!.Convert(values.Take(values.Count - 1)
                            .Select(value => Number(Resolve(value))).ToArray())
                        : null;
                    state = state with
                    {
                        StrokePattern = new TilingPatternPaint(strokePattern!, strokeBaseColor)
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
                    double joinValue = Number(Resolve(values[0]));
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
                        || !double.IsFinite(dashPhase) || dashPhase < 0)
                        throw new FormatException("A line dash pattern is invalid.");
                    state = state with
                    {
                        DashPattern = Array.AsReadOnly(dashPattern),
                        DashPhase = dashPhase
                    };
                    break;
                case "gs" when values.Count == 1 && values[0] is PdfName stateName:
                    if (TryGetGraphicsState(resources, stateName, out double? fillAlpha,
                        out double? strokeAlpha, out RendererBlendMode? blendMode,
                        out bool unsupportedBlend))
                        state = state with
                        {
                            FillAlpha = fillAlpha ?? state.FillAlpha,
                            StrokeAlpha = strokeAlpha ?? state.StrokeAlpha,
                            BlendMode = blendMode ?? state.BlendMode
                        };
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
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "S" or "s" when path.Count > 0:
                    if (instruction.Operator == "s" && subpath is { Count: > 1 }) subpath.Add(subpath[0]);
                    PaintStroke(path);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "B" or "B*" or "b" or "b*" when path.Count > 0:
                    if (instruction.Operator[0] == 'b' && subpath is { Count: > 1 })
                        subpath.Add(subpath[0]);
                    PaintFill(path, instruction.Operator.EndsWith('*'));
                    PaintStroke(path);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "n":
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "BT":
                    textMatrix = textLineMatrix = Matrix.Identity;
                    textClipPaths.Clear();
                    break;
                case "ET":
                    if (textClipPaths.Count > 0)
                    {
                        Point[][] polygons = [.. textClipPaths.Select(item => item.ToArray())];
                        ClipRegion[] clips = [.. state.Clips,
                            new ClipRegion(polygons, false)];
                        state = state with { Clips = Array.AsReadOnly(clips) };
                        textClipPaths.Clear();
                    }
                    break;
                case "Tf" when values.Count == 2 && values[0] is PdfName fontName:
                    textFont = ResolveFont(resources, fontName);
                    extractionFont = null;
                    textSize = Number(values[1]);
                    break;
                case "Tm" when values.Count == 6:
                    textMatrix = textLineMatrix = Matrix.From(values);
                    break;
                case "Td" or "TD" when values.Count == 2:
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
                            state.Fill, state.FillAlpha, state.BlendMode, cancellationToken, pixels,
                            options.Width, options.Height, scaleX, scaleY,
                            out string? imageDiagnostic))
                            diagnostics.Add(imageDiagnostic ?? "Image rendering is not implemented.");
                    }
                    else if (IsName(xObject.Dictionary, "Subtype", "Form"))
                        RenderForm(xObject, resources, state, depth);
                    break;
                case "BI" when values.Count == 1 && values[0] is PdfDictionary inlineDictionary
                    && instruction.InlineImageData.HasValue:
                    var inlineImage = new PdfStream(inlineDictionary,
                        instruction.InlineImageData.Value.Span);
                    if (!TryRenderImage(inlineImage, resources, state.Transform, state.Clips,
                        state.Fill, state.FillAlpha, state.BlendMode, cancellationToken, pixels,
                        options.Width, options.Height, scaleX, scaleY,
                        out string? inlineDiagnostic))
                        diagnostics.Add(inlineDiagnostic ?? "Inline-image rendering is not implemented.");
                    break;
                case "sh" when values.Count == 1 && values[0] is PdfName shadingName:
                    if (!TryRenderShading(resources, shadingName, state, pixels,
                        options.Width, options.Height, scaleX, scaleY, cancellationToken,
                        out string? shadingDiagnostic))
                        diagnostics.Add(shadingDiagnostic ?? "Shading rendering is not implemented.");
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
                    FillPaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        fillPath, state.Fill, state.FillAlpha, evenOdd,
                        state.BlendMode, state.Clips, cancellationToken);
                    return;
                }
                var fillClip = new ClipRegion(
                    [.. fillPath.Select(points => points.ToArray())], evenOdd);
                RenderTilingPattern(state.FillPattern, fillPath, fillClip,
                    resources, state, depth);
            }

            void PaintStroke(IReadOnlyList<List<Point>> strokePath)
            {
                IReadOnlyList<List<Point>> paintedPath = state.DashPattern.Count == 0
                    ? strokePath : CreateDashedPaths(strokePath, state.Transform,
                        state.DashPattern, state.DashPhase);
                if (state.StrokePattern is null)
                {
                    StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        paintedPath, state.Stroke, state.StrokeAlpha, state.LineWidth,
                        state.LineCap, state.LineJoin, state.MiterLimit,
                        state.BlendMode, state.Clips, cancellationToken);
                    return;
                }
                Point[][] segments = [.. paintedPath.Select(points => points.ToArray())];
                double radius = Math.Max(state.LineWidth / 2,
                    Math.Sqrt(0.5) / Math.Min(scaleX, scaleY));
                var strokeClip = new ClipRegion([], false,
                    (x, y) => IsWithinStroke(segments, radius, x, y,
                        state.LineCap, state.LineJoin, state.MiterLimit));
                RenderTilingPattern(state.StrokePattern, paintedPath, strokeClip,
                    resources, state, depth);
            }

            void RenderTilingPattern(TilingPatternPaint paint,
                IReadOnlyList<List<Point>> paintPath, ClipRegion paintClip,
                PdfDictionary parentResources, GraphicsState parentState, int patternDepth)
            {
                PdfStream pattern = paint.Stream;
                PdfArray box = pattern.Dictionary.TryGetValue(Name("BBox"), out PdfObject? boxValue)
                    ? ResolveArray(boxValue, 4, "Tiling pattern bounding box")
                    : throw new FormatException("A tiling pattern has no bounding box.");
                double left = Number(Resolve(box[0])), bottom = Number(Resolve(box[1]));
                double right = Number(Resolve(box[2])), top = Number(Resolve(box[3]));
                double xStep = PatternStep(pattern.Dictionary, "XStep");
                double yStep = PatternStep(pattern.Dictionary, "YStep");
                Matrix patternMatrix = pattern.Dictionary.TryGetValue(Name("Matrix"),
                    out PdfObject? matrixValue)
                    ? Matrix.From(ResolveArray(matrixValue, 6, "Tiling pattern matrix"))
                    : Matrix.Identity;
                Matrix patternToPage = patternMatrix.Then(parentState.Transform);
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
                byte[] content = PdfStreamDecoder.Decode(pattern, _document.Resolve,
                    PdfContentStreamReader.MaximumSourceBytes);
                PdfContentInstruction[] patternInstructions = [.. PdfContentStreamReader.Read(
                    content, cancellationToken: cancellationToken)];
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
                        ClipRegion[] clips = [.. parentState.Clips, paintClip,
                            new ClipRegion([cellBox], false)];
                        GraphicsState cellState = parentState with
                        {
                            Transform = cell,
                            Clips = Array.AsReadOnly(clips),
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
                            patternDepth + 1);
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
                extractionFont ??= PdfFontResourceReader.Read(_document, textFont);
                return extractionFont.IsVertical;
            }

            void ShowText(PdfString text)
            {
                if (textFont is null || textSize <= 0)
                {
                    diagnostics.Add("Text rendering is not implemented.");
                    return;
                }
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
                bool paintsType3 = textRenderingMode is >= 0 and <= 2;
                if (textRenderingMode is < 0 or > 3)
                    diagnostics.Add("Text rendering is not implemented.");
                foreach (byte code in text.Bytes.Span)
                {
                    string glyphName = encoding[code];
                    if (paintsType3 && glyphName.Length > 0
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
                        byte[] bytes = PdfStreamDecoder.Decode(glyph, _document.Resolve,
                            PdfContentStreamReader.MaximumSourceBytes);
                        Process(PdfContentStreamReader.Read(bytes,
                            cancellationToken: cancellationToken), fontResources,
                            glyphState, depth + 1);
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
                    extractionFont ??= fontCache.GetValueOrDefault(textFont!)
                        ?? (fontCache[textFont!] = PdfFontResourceReader.Read(
                            _document, textFont!));
                    if (textRenderingMode is < 0 or > 7)
                    {
                        diagnostics.Add("Text rendering is not implemented.");
                        return;
                    }
                    foreach (PdfDecodedCharacter character in extractionFont.Decode(text.Bytes))
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
                        PdfGlyphOutline? outline = extractionFont.GetGlyphOutline(character.Code);
                        int paintMode = textRenderingMode % 4;
                        bool clipsText = textRenderingMode >= 4;
                        if ((paintMode != 3 || clipsText) && outline is not null)
                        {
                            IReadOnlyList<List<Point>> glyphPaths =
                                FlattenGlyphOutline(outline, glyphTransform);
                            if (paintMode is 0 or 2)
                                FillPaths(pixels, options.Width, options.Height, scaleX, scaleY,
                                    glyphPaths, state.Fill, state.FillAlpha, false,
                                    state.BlendMode, state.Clips, cancellationToken);
                            if (paintMode is 1 or 2)
                                StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                                    glyphPaths, state.Stroke, state.StrokeAlpha, state.LineWidth,
                                    state.LineCap, state.LineJoin, state.MiterLimit,
                                    state.BlendMode, state.Clips, cancellationToken);
                            if (clipsText)
                                textClipPaths.AddRange(glyphPaths.Select(item => new List<Point>(item)));
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
        }

        void RenderForm(PdfStream form, PdfDictionary inheritedResources,
            GraphicsState parentState, int depth)
        {
            if (!activeForms.Add(form)) throw new FormatException("Cyclic Form XObject.");
            try
            {
                Matrix matrix = form.Dictionary.TryGetValue(Name("Matrix"), out PdfObject? value)
                    ? Matrix.From(ResolveArray(value, 6, "Form XObject matrix")) : Matrix.Identity;
                GraphicsState formState = parentState with
                {
                    Transform = matrix.Then(parentState.Transform)
                };
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
                    ClipRegion[] clips = [.. formState.Clips,
                        new ClipRegion([polygon], false)];
                    formState = formState with { Clips = Array.AsReadOnly(clips) };
                }
                PdfDictionary formResources = form.Dictionary.TryGetValue(
                    Name("Resources"), out PdfObject? resourceValue)
                    ? Resolve(resourceValue) as PdfDictionary
                        ?? throw new FormatException("Form XObject resources are not a dictionary.")
                    : inheritedResources;
                byte[] bytes = PdfStreamDecoder.Decode(form, _document.Resolve,
                    PdfContentStreamReader.MaximumSourceBytes);
                Process(PdfContentStreamReader.Read(bytes, cancellationToken: cancellationToken),
                    formResources, formState, depth + 1);
            }
            finally
            {
                activeForms.Remove(form);
            }
        }

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
                if (!TryGetAppearance(annotation, out PdfStream? appearance)
                    || appearance is null) continue;
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

    private bool EvaluateOptionalContent(PdfObject value,
        IReadOnlySet<int> hiddenOptionalContentGroups, int expressionDepth)
    {
        if (expressionDepth > 32)
            throw new FormatException(
                "An optional-content visibility expression is too deeply nested.");
        PdfObject resolved = Resolve(value);
        if (resolved is not PdfDictionary dictionary) return true;
        string type = NameValue(dictionary, "Type");
        if (type == "OCG")
            return value is not PdfIndirectReference groupReference
                || !hiddenOptionalContentGroups.Contains(groupReference.ObjectNumber);
        if (type != "OCMD") return true;
        if (dictionary.TryGetValue(Name("VE"), out PdfObject? expression))
            return EvaluateVisibilityExpression(expression,
                hiddenOptionalContentGroups, expressionDepth + 1);
        if (!dictionary.TryGetValue(Name("OCGs"), out PdfObject? groupsValue))
            throw new FormatException(
                "An optional-content membership dictionary has no /OCGs or /VE value.");
        PdfObject groups = Resolve(groupsValue);
        bool[] states = groups is PdfArray array
            ? [.. array.Select(group => EvaluateOptionalContent(group,
                hiddenOptionalContentGroups, expressionDepth + 1))]
            : [EvaluateOptionalContent(groupsValue,
                hiddenOptionalContentGroups, expressionDepth + 1)];
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

    private bool EvaluateVisibilityExpression(PdfObject value,
        IReadOnlySet<int> hiddenOptionalContentGroups, int expressionDepth)
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
                    hiddenOptionalContentGroups, expressionDepth + 1)
                : EvaluateOptionalContent(item,
                    hiddenOptionalContentGroups, expressionDepth + 1))];
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
        return page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? value)
            ? Resolve(value) as PdfDictionary
                ?? throw new FormatException("Page resources are not a dictionary.")
            : new PdfDictionary([]);
    }

    private PdfDictionary? ResolveFont(PdfDictionary resources, PdfName resourceName) =>
        resources.TryGetValue(Name("Font"), out PdfObject? fontsValue)
        && Resolve(fontsValue) is PdfDictionary fonts
        && fonts.TryGetValue(resourceName, out PdfObject? fontValue)
        ? Resolve(fontValue) as PdfDictionary : null;

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

    private bool TryGetTilingPattern(PdfDictionary resources, PdfName resourceName,
        out PdfStream? pattern, out int paintType)
    {
        pattern = resources.TryGetValue(Name("Pattern"), out PdfObject? patternsValue)
            && Resolve(patternsValue) is PdfDictionary patterns
            && patterns.TryGetValue(resourceName, out PdfObject? value)
            && Resolve(value) is PdfStream stream
            && stream.Dictionary.TryGetValue(Name("PatternType"), out PdfObject? typeValue)
            && Resolve(typeValue) is PdfInteger { Value: 1 }
            && stream.Dictionary.TryGetValue(Name("PaintType"), out PdfObject? paintValue)
            && Resolve(paintValue) is PdfInteger { Value: 1 or 2 }
            ? stream : null;
        paintType = pattern is not null
            ? checked((int)AssertInteger(pattern.Dictionary, "PaintType")) : 0;
        return pattern is not null;
    }

    private long AssertInteger(PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && Resolve(value) is PdfInteger integer
            ? integer.Value : throw new FormatException($"A /{key} integer is missing.");

    private bool TryRenderImage(PdfStream stream, PdfDictionary resources, Matrix transform,
        IReadOnlyList<ClipRegion> clips, Color stencilColor, double stencilAlpha,
        RendererBlendMode blendMode, CancellationToken cancellationToken,
        byte[] target, int targetWidth, int targetHeight, double scaleX, double scaleY,
        out string? diagnostic)
    {
        diagnostic = null;
        bool imageMask = stream.Dictionary.TryGetValue(Name("ImageMask"), out PdfObject? maskValue)
            && Resolve(maskValue) is PdfBoolean { Value: true };
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        int bits = imageMask ? 1 : PositiveInteger(stream.Dictionary, "BitsPerComponent");
        ImageColorSpace colorSpace;
        try
        {
            colorSpace = imageMask ? new ImageColorSpace(1, null)
                : ReadImageColorSpace(stream.Dictionary, resources);
        }
        catch (PdfFilterException)
        {
            diagnostic = "The image compression filter is not implemented.";
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
        SoftMask? softMask;
        double[] decode;
        int[]? colorKeyMask = null;
        PdfStream? explicitMask = null;
        if (stream.Dictionary.TryGetValue(Name("Mask"), out PdfObject? colorKeyValue))
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
        try
        {
            int expected = checked(((width * components * bits + 7) / 8) * height);
            samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
            if (samples.Length != expected) throw new FormatException("Image sample data has an invalid length.");
            softMask = ReadSoftMask(stream.Dictionary);
            if (softMask is null && explicitMask is not null)
                softMask = ReadExplicitImageMask(explicitMask);
            decode = ReadImageDecode(stream.Dictionary, colorSpace, imageMask);
        }
        catch (PdfFilterException)
        {
            diagnostic = "The image compression filter is not implemented.";
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
            transform, samples, width, height, components, bits, clips,
            imageMask, imageMask && StencilPaintsOne(stream.Dictionary), softMask, decode,
            colorKeyMask, colorSpace, stencilColor, stencilAlpha, blendMode,
            cancellationToken);
        return true;
    }

    private ImageColorSpace ReadImageColorSpace(
        PdfDictionary dictionary, PdfDictionary resources)
    {
        if (!dictionary.TryGetValue(Name("ColorSpace"), out PdfObject? value))
            throw new NotSupportedException();
        return ReadColorSpace(value, resources, 0);
    }

    private ImageColorSpace ReadColorSpace(
        PdfObject value, PdfDictionary resources, int depth)
    {
        if (depth > 16) throw new FormatException("An image color-space reference is cyclic.");
        PdfObject resolved = Resolve(value);
        if (resolved is PdfName name)
        {
            ImageColorSpace? standard = name.ValueAsLatin1() switch
            {
                "DeviceGray" or "G" => new ImageColorSpace(1, null),
                "DeviceRGB" or "RGB" => new ImageColorSpace(3, null),
                "DeviceCMYK" or "CMYK" => new ImageColorSpace(4, null),
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
            ImageColorSpace alternate = profile.Dictionary.TryGetValue(
                Name("Alternate"), out PdfObject? alternateValue)
                ? ReadColorSpace(alternateValue, resources, depth + 1)
                : count.Value switch
                {
                    1 => new ImageColorSpace(1, null),
                    3 => new ImageColorSpace(3, null),
                    _ => new ImageColorSpace(4, null)
                };
            if (alternate.Components != count.Value || alternate.Palette is not null)
                throw new FormatException("An ICCBased image alternate has the wrong component count.");
            return alternate;
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
            });
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
            });
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
            return new ImageColorSpace(3, null, (lightness, a, b, _) =>
            {
                double fy = (lightness + 16) / 116;
                double fx = fy + a / 500;
                double fz = fy - b / 200;
                return convertXyz(whitePoint[0] * LabInverse(fx),
                    whitePoint[1] * LabInverse(fy), whitePoint[2] * LabInverse(fz));
            }, [0, 100, range[0], range[1], range[2], range[3]]);
        }
        if (kind.ValueAsLatin1() == "Separation")
        {
            if (array.Count != 4 || Resolve(array[1]) is not PdfName
                || ReadColorSpace(array[2], resources, depth + 1) is not { Palette: null } alternate)
                throw new FormatException("A Separation image color space is invalid.");
            Func<double, Color> tintTransform = ReadColorFunction(
                array[3], alternate, "Separation tint transform");
            return new ImageColorSpace(1, null,
                (tint, _, _, _) => tintTransform(tint));
        }
        if (kind.ValueAsLatin1() == "DeviceN")
        {
            if (array.Count is not (4 or 5) || Resolve(array[1]) is not PdfArray names
                || names.Count is < 1 or > 32
                || names.Any(item => Resolve(item) is not PdfName))
                throw new FormatException("A DeviceN image color space is invalid.");
            if (names.Count > 8) throw new NotSupportedException();
            ImageColorSpace alternate = ReadColorSpace(array[2], resources, depth + 1);
            if (alternate.Palette is not null)
                throw new FormatException("A DeviceN image alternate color space is invalid.");
            Func<double[], Color> tintTransform = ReadMultidimensionalColorFunction(
                array[3], names.Count, alternate, "DeviceN tint transform");
            return new ImageColorSpace(names.Count, null, MultiConverter: tintTransform);
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
            PdfStream stream => PdfStreamDecoder.Decode(stream, _document.Resolve, expected),
            _ => throw new NotSupportedException()
        };
        if (lookup.Length < expected)
            throw new FormatException("An Indexed image color lookup is truncated.");
        var palette = new Color[entryCount];
        for (int entry = 0; entry < entryCount; entry++)
        {
            int offset = entry * baseComponents;
            palette[entry] = baseSpace.Convert(baseSpace.DefaultValue(0, lookup[offset] / 255d),
                baseComponents > 1 ? baseSpace.DefaultValue(1, lookup[offset + 1] / 255d) : 0,
                baseComponents > 2 ? baseSpace.DefaultValue(2, lookup[offset + 2] / 255d) : 0,
                baseComponents > 3 ? baseSpace.DefaultValue(3, lookup[offset + 3] / 255d) : 0);
        }
        return new ImageColorSpace(1, palette);
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
        return (x, y, z) =>
        {
            (double l, double m, double s) = Bradford(x, y, z);
            l *= scaleL;
            m *= scaleM;
            s *= scaleS;
            double adaptedX = 0.9869929 * l - 0.1470543 * m + 0.1599627 * s;
            double adaptedY = 0.4323053 * l + 0.5183603 * m + 0.0492912 * s;
            double adaptedZ = -0.0085287 * l + 0.0400428 * m + 0.9684867 * s;
            return Color.LinearRgb(
                3.2404542 * adaptedX - 1.5371385 * adaptedY - 0.4985314 * adaptedZ,
                -0.969266 * adaptedX + 1.8760108 * adaptedY + 0.041556 * adaptedZ,
                0.0556434 * adaptedX - 0.2040259 * adaptedY + 1.0572252 * adaptedZ);
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
            return alternate.Convert(Component(0), outputCount > 1 ? Component(1) : 0,
                outputCount > 2 ? Component(2) : 0, outputCount > 3 ? Component(3) : 0);
        };
    }

    private Func<double, Color> ReadColorFunction(
        PdfObject value, ImageColorSpace colorSpace, string description)
    {
        PdfObject resolved = Resolve(value);
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
            _ => throw new NotSupportedException()
        };
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
            && Resolve(orderValue) is not PdfInteger { Value: 1 })
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
        byte[] samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expectedBytes);
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
            && Resolve(orderValue) is not PdfInteger { Value: 1 })
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
        byte[] samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expectedBytes);
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
        PdfDictionary dictionary = stream.Dictionary;
        double[] domain = ReadFunctionArray(dictionary, "Domain", inputCount * 2,
            required: true, defaultValues: []);
        double[] range = ReadFunctionArray(dictionary, "Range", colorSpace.Components * 2,
            required: true, defaultValues: []);
        if (domain.Any(value => !double.IsFinite(value))
            || Enumerable.Range(0, inputCount).Any(index =>
                domain[index * 2] >= domain[index * 2 + 1])
            || range.Any(value => !double.IsFinite(value))
            || Enumerable.Range(0, colorSpace.Components).Any(index =>
                range[index * 2] > range[index * 2 + 1]))
            throw new FormatException($"A {description} domain or range is invalid.");
        byte[] program = PdfStreamDecoder.Decode(stream, _document.Resolve, 1024 * 1024);
        var tokenizer = new PdfTokenizer(program);
        if (tokenizer.Read().Kind != PdfTokenKind.BraceStart)
            throw new FormatException($"A {description} program is invalid.");
        var instructions = new List<CalculatorInstruction>();
        while (true)
        {
            PdfToken token = tokenizer.Read();
            if (token.Kind == PdfTokenKind.BraceEnd) break;
            if (token.Kind == PdfTokenKind.EndOfInput || instructions.Count >= 4096)
                throw new FormatException($"A {description} program is invalid or too large.");
            instructions.Add(token.Kind switch
            {
                PdfTokenKind.Integer => new CalculatorInstruction(
                    double.Parse(token.ValueAsLatin1(), System.Globalization.CultureInfo.InvariantCulture), null),
                PdfTokenKind.Real => new CalculatorInstruction(
                    double.Parse(token.ValueAsLatin1(), System.Globalization.CultureInfo.InvariantCulture), null),
                PdfTokenKind.Keyword => new CalculatorInstruction(null, token.ValueAsLatin1()),
                _ => throw new NotSupportedException()
            });
        }
        if (tokenizer.Read().Kind != PdfTokenKind.EndOfInput)
            throw new FormatException($"A {description} program has trailing content.");

        return inputs =>
        {
            if (inputs.Length != inputCount)
                throw new InvalidOperationException("A calculator function received the wrong input count.");
            var stack = new List<double>(Math.Max(16, inputCount + colorSpace.Components));
            for (int index = 0; index < inputCount; index++)
                stack.Add(Math.Clamp(inputs[index], domain[index * 2], domain[index * 2 + 1]));
            foreach (CalculatorInstruction instruction in instructions)
            {
                if (instruction.Number is double number)
                {
                    Push(number);
                    continue;
                }
                Execute(instruction.Operator!);
            }
            if (stack.Count < colorSpace.Components)
                throw new FormatException($"A {description} program produced too few values.");
            int outputStart = stack.Count - colorSpace.Components;
            var outputs = new double[colorSpace.Components];
            for (int index = 0; index < outputs.Length; index++)
                outputs[index] = Math.Clamp(stack[outputStart + index],
                    range[index * 2], range[index * 2 + 1]);
            return colorSpace.Convert(outputs);

            void Push(double value)
            {
                if (!double.IsFinite(value) || stack.Count >= 256)
                    throw new FormatException($"A {description} calculator stack is invalid.");
                stack.Add(value);
            }
            double Pop()
            {
                if (stack.Count == 0)
                    throw new FormatException($"A {description} calculator stack underflowed.");
                double value = stack[^1];
                stack.RemoveAt(stack.Count - 1);
                return value;
            }
            int PopInteger()
            {
                double value = Pop();
                if (value < int.MinValue || value > int.MaxValue || value != Math.Truncate(value))
                    throw new FormatException($"A {description} calculator integer is invalid.");
                return (int)value;
            }
            void Execute(string operation)
            {
                switch (operation)
                {
                    case "abs": Push(Math.Abs(Pop())); break;
                    case "add": { double b = Pop(); Push(Pop() + b); break; }
                    case "ceiling": Push(Math.Ceiling(Pop())); break;
                    case "cos": Push(Math.Cos(Pop() * Math.PI / 180)); break;
                    case "cvi": Push(Math.Truncate(Pop())); break;
                    case "cvr": break;
                    case "div": { double b = Pop(); Push(Pop() / b); break; }
                    case "dup": { double value = Pop(); Push(value); Push(value); break; }
                    case "exch": { double b = Pop(), a = Pop(); Push(b); Push(a); break; }
                    case "exp": { double exponent = Pop(); Push(Math.Pow(Pop(), exponent)); break; }
                    case "floor": Push(Math.Floor(Pop())); break;
                    case "idiv": { int b = PopInteger(); int a = PopInteger(); Push(a / b); break; }
                    case "index":
                    {
                        int index = PopInteger();
                        if (index < 0 || index >= stack.Count)
                            throw new FormatException($"A {description} calculator index is invalid.");
                        Push(stack[stack.Count - index - 1]);
                        break;
                    }
                    case "ln": Push(Math.Log(Pop())); break;
                    case "log": Push(Math.Log10(Pop())); break;
                    case "mod": { int b = PopInteger(); int a = PopInteger(); Push(a % b); break; }
                    case "mul": { double b = Pop(); Push(Pop() * b); break; }
                    case "neg": Push(-Pop()); break;
                    case "pop": Pop(); break;
                    case "roll":
                    {
                        int shift = PopInteger(), count = PopInteger();
                        if (count < 0 || count > stack.Count)
                            throw new FormatException($"A {description} calculator roll is invalid.");
                        if (count == 0) break;
                        shift %= count;
                        if (shift < 0) shift += count;
                        if (shift == 0) break;
                        int start = stack.Count - count;
                        double[] values = stack.GetRange(start, count).ToArray();
                        for (int index = 0; index < count; index++)
                            stack[start + (index + shift) % count] = values[index];
                        break;
                    }
                    case "round": Push(Math.Round(Pop(), MidpointRounding.AwayFromZero)); break;
                    case "sin": Push(Math.Sin(Pop() * Math.PI / 180)); break;
                    case "sqrt": Push(Math.Sqrt(Pop())); break;
                    case "sub": { double b = Pop(); Push(Pop() - b); break; }
                    case "truncate": Push(Math.Truncate(Pop())); break;
                    default: throw new NotSupportedException(
                        $"Calculator operator {operation} is not implemented.");
                }
            }
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
        if (dictionary.ContainsKey(Name("Range"))) throw new NotSupportedException();
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
            if (!double.IsFinite(bound) || bound <= previous || bound >= domain[1])
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
            double fraction = (clipped - start) / (end - start);
            double mapped = encode[segment * 2] + fraction
                * (encode[segment * 2 + 1] - encode[segment * 2]);
            return functions[segment](mapped);
        };
    }

    private bool TryRenderShading(PdfDictionary resources, PdfName resourceName,
        GraphicsState state, byte[] target, int targetWidth, int targetHeight,
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
            PdfObject resolved = Resolve(shadingValue);
            PdfDictionary shading = resolved switch
            {
                PdfDictionary dictionary => dictionary,
                PdfStream stream => stream.Dictionary,
                _ => throw new FormatException("An axial shading dictionary is invalid.")
            };
            if (!shading.TryGetValue(Name("ShadingType"), out PdfObject? typeValue)
                || Resolve(typeValue) is not PdfInteger shadingType)
                throw new NotSupportedException();
            if (shadingType.Value == 7)
                return resolved is PdfStream tensorPatch
                    ? RenderTensorPatchShading(tensorPatch, resources, state, target,
                        targetWidth, targetHeight, scaleX, scaleY, cancellationToken)
                    : throw new FormatException("A tensor-patch shading must be a stream.");
            if (shadingType.Value == 3)
                return RenderRadialShading(shading, resources, state, target,
                    targetWidth, targetHeight, scaleX, scaleY);
            if (shadingType.Value != 2) throw new NotSupportedException();
            if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
                throw new FormatException("An axial shading color space is missing.");
            ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0);
            if (colorSpace.Palette is not null)
                throw new NotSupportedException();
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
            for (int y = 0; y < targetHeight; y++)
                for (int x = 0; x < targetWidth; x++)
                {
                    double pageX = (x + 0.5) / scaleX;
                    double pageY = (targetHeight - y - 0.5) / scaleY;
                    if (!InsideClips(state.Clips, pageX, pageY)) continue;
                    if (bounds is not null && !Contains([bounds], false, pageX, pageY)) continue;
                    Point point = inverse.Apply(pageX, pageY);
                    double unit = ((point.X - x0) * axisX + (point.Y - y0) * axisY)
                        / axisLengthSquared;
                    if (unit < 0 && !extendStart || unit > 1 && !extendEnd) continue;
                    unit = Math.Clamp(unit, 0, 1);
                    double input = domain[0] + unit * (domain[1] - domain[0]);
                    SetPixel(target, targetWidth, x, y, function(input), state.FillAlpha,
                        state.BlendMode);
                }
            return true;
        }
        catch (NotSupportedException)
        {
            diagnostic = "The shading type or function is not implemented.";
            return false;
        }
    }

    private bool RenderTensorPatchShading(PdfStream stream, PdfDictionary resources,
        GraphicsState state, byte[] target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, CancellationToken cancellationToken)
    {
        PdfDictionary shading = stream.Dictionary;
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A tensor-patch shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0);
        if (colorSpace.Palette is not null) throw new NotSupportedException();
        int coordinateBits = PositiveInteger(shading, "BitsPerCoordinate");
        int componentBits = PositiveInteger(shading, "BitsPerComponent");
        int flagBits = PositiveInteger(shading, "BitsPerFlag");
        if (coordinateBits is < 1 or > 32 || componentBits is < 1 or > 32
            || flagBits is < 1 or > 8)
            throw new NotSupportedException();
        double[] decode = ReadFunctionArray(shading, "Decode",
            4 + colorSpace.Components * 2, required: true, defaultValues: []);
        if (decode.Any(value => !double.IsFinite(value)))
            throw new FormatException("A tensor-patch shading decode array is invalid.");
        byte[] bytes = PdfStreamDecoder.Decode(stream, _document.Resolve);
        int bitOffset = 0;
        bool rendered = false;
        while (bitOffset < bytes.Length * 8)
        {
            uint flag = Read(flagBits);
            if (flag != 0) throw new NotSupportedException();
            var points = new Point[16];
            for (int index = 0; index < points.Length; index++)
            {
                double x = Decode(Read(coordinateBits), coordinateBits, decode[0], decode[1]);
                double y = Decode(Read(coordinateBits), coordinateBits, decode[2], decode[3]);
                points[index] = state.Transform.Apply(x, y);
            }
            var colors = new Color[4];
            for (int corner = 0; corner < colors.Length; corner++)
            {
                var components = new double[colorSpace.Components];
                for (int component = 0; component < components.Length; component++)
                    components[component] = Decode(Read(componentBits), componentBits,
                        decode[4 + component * 2], decode[5 + component * 2]);
                colors[corner] = colorSpace.Convert(components);
            }
            RenderPatch(points, colors);
            rendered = true;
        }
        return rendered;

        uint Read(int bits)
        {
            if (bitOffset + bits > bytes.Length * 8)
                throw new FormatException("A tensor-patch shading stream is truncated.");
            uint value = ReadPackedSample(bytes, bitOffset, bits);
            bitOffset += bits;
            return value;
        }
        static double Decode(uint value, int bits, double minimum, double maximum)
        {
            double limit = bits == 32 ? uint.MaxValue : (1u << bits) - 1u;
            return minimum + value / limit * (maximum - minimum);
        }
        void RenderPatch(Point[] source, Color[] colors)
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
            var sampledColors = new Color[divisions + 1, divisions + 1];
            for (int row = 0; row <= divisions; row++)
                for (int column = 0; column <= divisions; column++)
                {
                    double u = column / (double)divisions;
                    double v = row / (double)divisions;
                    sampledPoints[row, column] = BezierSurface(grid, u, v);
                    sampledColors[row, column] = Bilinear(colors, u, v);
                }
            for (int row = 0; row < divisions; row++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int column = 0; column < divisions; column++)
                {
                    PaintTriangle(sampledPoints[row, column], sampledColors[row, column],
                        sampledPoints[row, column + 1], sampledColors[row, column + 1],
                        sampledPoints[row + 1, column + 1], sampledColors[row + 1, column + 1]);
                    PaintTriangle(sampledPoints[row, column], sampledColors[row, column],
                        sampledPoints[row + 1, column + 1], sampledColors[row + 1, column + 1],
                        sampledPoints[row + 1, column], sampledColors[row + 1, column]);
                }
            }
        }
        void PaintTriangle(Point first, Color firstColor, Point second, Color secondColor,
            Point third, Color thirdColor)
        {
            double area = Edge(first, second, third.X, third.Y);
            if (Math.Abs(area) < 1e-12) return;
            double minimumX = Math.Min(first.X, Math.Min(second.X, third.X));
            double maximumX = Math.Max(first.X, Math.Max(second.X, third.X));
            double minimumY = Math.Min(first.Y, Math.Min(second.Y, third.Y));
            double maximumY = Math.Max(first.Y, Math.Max(second.Y, third.Y));
            if (maximumX <= 0 || minimumX >= targetWidth / scaleX
                || maximumY <= 0 || minimumY >= targetHeight / scaleY) return;
            int left = Math.Clamp((int)Math.Floor(minimumX * scaleX), 0, targetWidth - 1);
            int right = Math.Clamp((int)Math.Ceiling(maximumX * scaleX), 0, targetWidth - 1);
            int top = Math.Clamp(targetHeight - (int)Math.Ceiling(maximumY * scaleY),
                0, targetHeight - 1);
            int bottom = Math.Clamp(targetHeight - (int)Math.Floor(minimumY * scaleY),
                0, targetHeight - 1);
            for (int y = top; y <= bottom; y++)
                for (int x = left; x <= right; x++)
                {
                    double pageX = (x + 0.5) / scaleX;
                    double pageY = (targetHeight - y - 0.5) / scaleY;
                    if (!InsideClips(state.Clips, pageX, pageY)) continue;
                    double a = Edge(second, third, pageX, pageY) / area;
                    double b = Edge(third, first, pageX, pageY) / area;
                    double c = 1 - a - b;
                    if (a < -1e-9 || b < -1e-9 || c < -1e-9) continue;
                    SetPixel(target, targetWidth, x, y, Mix(firstColor, secondColor, thirdColor,
                        a, b, c), state.FillAlpha, state.BlendMode);
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
        static Color Bilinear(Color[] colors, double u, double v) => MixFour(
            colors[0], colors[1], colors[2], colors[3],
            (1 - u) * (1 - v), u * (1 - v), u * v, (1 - u) * v);
        static Color Mix(Color first, Color second, Color third,
            double a, double b, double c) => new(
            Channel(first.Red * a + second.Red * b + third.Red * c),
            Channel(first.Green * a + second.Green * b + third.Green * c),
            Channel(first.Blue * a + second.Blue * b + third.Blue * c));
        static Color MixFour(Color first, Color second, Color third, Color fourth,
            double a, double b, double c, double d) => new(
            Channel(first.Red * a + second.Red * b + third.Red * c + fourth.Red * d),
            Channel(first.Green * a + second.Green * b + third.Green * c + fourth.Green * d),
            Channel(first.Blue * a + second.Blue * b + third.Blue * c + fourth.Blue * d));
        static byte Channel(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
    }

    private bool RenderRadialShading(PdfDictionary shading, PdfDictionary resources,
        GraphicsState state, byte[] target, int targetWidth, int targetHeight,
        double scaleX, double scaleY)
    {
        if (!shading.TryGetValue(Name("ColorSpace"), out PdfObject? colorSpaceValue))
            throw new FormatException("A radial shading color space is missing.");
        ImageColorSpace colorSpace = ReadColorSpace(colorSpaceValue, resources, 0);
        if (colorSpace.Palette is not null) throw new NotSupportedException();
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
        for (int y = 0; y < targetHeight; y++)
            for (int x = 0; x < targetWidth; x++)
            {
                double pageX = (x + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                if (!InsideClips(state.Clips, pageX, pageY)) continue;
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
                SetPixel(target, targetWidth, x, y, function(input), state.FillAlpha,
                    state.BlendMode);
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

    private SoftMask? ReadSoftMask(PdfDictionary dictionary)
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
        int rowBytes = checked((width * bits + 7) / 8);
        int expected = checked(rowBytes * height);
        byte[] packed = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
        if (packed.Length != expected)
            throw new FormatException("Image soft-mask sample data has an invalid length.");
        double decodeStart = 0, decodeEnd = 1;
        if (stream.Dictionary.TryGetValue(Name("Decode"), out PdfObject? decodeValue))
        {
            PdfArray decode = ResolveArray(decodeValue, 2, "Image soft-mask decode array");
            decodeStart = Number(Resolve(decode[0]));
            decodeEnd = Number(Resolve(decode[1]));
        }
        uint maximum = (1u << bits) - 1;
        var samples = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                uint sample = ReadPackedSample(packed,
                    checked(y * rowBytes * 8 + x * bits), bits);
                double decoded = decodeStart + sample / (double)maximum
                    * (decodeEnd - decodeStart);
                samples[y * width + x] = (byte)Math.Round(Math.Clamp(decoded, 0, 1) * 255);
            }
        return new SoftMask(samples, width, height);
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
        int rowBytes = checked((width + 7) / 8);
        int expected = checked(rowBytes * height);
        byte[] packed = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
        if (packed.Length != expected)
            throw new FormatException("Image mask sample data has an invalid length.");
        bool paintsOne = StencilPaintsOne(stream.Dictionary);
        var samples = new byte[checked(width * height)];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
            {
                bool one = ReadPackedSample(packed, y * rowBytes * 8 + x, 1) != 0;
                samples[y * width + x] = one == paintsOne ? byte.MaxValue : byte.MinValue;
            }
        return new SoftMask(samples, width, height);
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

    private static void PaintImage(byte[] target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix transform, byte[] samples,
        int sourceWidth, int sourceHeight, int components, int bits,
        IReadOnlyList<ClipRegion> clips, bool imageMask, bool stencilPaintsOne,
        SoftMask? softMask, double[] decode, int[]? colorKeyMask,
        ImageColorSpace colorSpace, Color stencilColor, double stencilAlpha,
        RendererBlendMode blendMode, CancellationToken cancellationToken)
    {
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
        int rowBytes = (sourceWidth * components * bits + 7) / 8;
        bool directRgb = !imageMask && bits == 8 && components == 3
            && softMask is null && colorKeyMask is null && clips.Count == 0
            && colorSpace.Palette is null && colorSpace.Converter is null
            && colorSpace.MultiConverter is null
            && decode is [0, 1, 0, 1, 0, 1]
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        if (directRgb)
        {
            double pageStepX = 1 / scaleX;
            double unitStepX = inverse.A * pageStepX;
            double unitStepY = inverse.B * pageStepX;
            for (int y = top; y < bottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double pageX = (left + 0.5) / scaleX;
                double pageY = (targetHeight - y - 0.5) / scaleY;
                Point first = inverse.Apply(pageX, pageY);
                double unitX = first.X;
                double unitY = first.Y;
                for (int x = left; x < right; x++, unitX += unitStepX, unitY += unitStepY)
                {
                    if (unitX < 0 || unitX >= 1 || unitY < 0 || unitY >= 1) continue;
                    int sourceX = Math.Min((int)(unitX * sourceWidth), sourceWidth - 1);
                    int sourceY = Math.Min((int)((1 - unitY) * sourceHeight), sourceHeight - 1);
                    int sourceOffset = sourceY * rowBytes + sourceX * 3;
                    int targetOffset = (y * targetWidth + x) * 4;
                    target[targetOffset] = samples[sourceOffset + 2];
                    target[targetOffset + 1] = samples[sourceOffset + 1];
                    target[targetOffset + 2] = samples[sourceOffset];
                    target[targetOffset + 3] = 255;
                }
            }
            return;
        }
        var componentValues = new double[components];
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = left; x < right; x++)
            {
                Point unit = inverse.Apply((x + 0.5) / scaleX,
                    (targetHeight - y - 0.5) / scaleY);
                if (unit.X < 0 || unit.X >= 1 || unit.Y < 0 || unit.Y >= 1) continue;
                if (!InsideClips(clips, (x + 0.5) / scaleX,
                    (targetHeight - y - 0.5) / scaleY)) continue;
                int sx = Math.Clamp((int)(unit.X * sourceWidth), 0, sourceWidth - 1);
                int sy = Math.Clamp((int)((1 - unit.Y) * sourceHeight), 0, sourceHeight - 1);
                sy = Math.Min(sy, sourceHeight - 1);
                Color color;
                double alpha = 1;
                if (imageMask)
                {
                    int bit = sx;
                    bool one = (samples[sy * rowBytes + bit / 8] & (0x80 >> (bit & 7))) != 0;
                    if (one != stencilPaintsOne) continue;
                    color = stencilColor;
                    alpha = stencilAlpha;
                }
                else
                {
                    color = colorSpace.Palette is not null
                        ? colorSpace.Palette[Math.Min(RawSample(0), colorSpace.Palette.Length - 1)]
                        : ConvertSamples();
                    if (colorKeyMask is not null)
                    {
                        bool matches = true;
                        for (int component = 0; component < components && matches; component++)
                        {
                            int sample = RawSample(component);
                            matches = sample >= colorKeyMask[component * 2]
                                && sample <= colorKeyMask[component * 2 + 1];
                        }
                        if (matches) alpha = 0;
                    }

                    double SampleValue(int component)
                    {
                        int sample = RawSample(component);
                        double normalized = sample / (double)((1 << bits) - 1);
                        return decode[component * 2] + normalized
                            * (decode[component * 2 + 1] - decode[component * 2]);
                    }

                    int RawSample(int component)
                    {
                        int bitOffset = checked(sy * rowBytes * 8
                            + (sx * components + component) * bits);
                        return checked((int)ReadPackedSample(samples, bitOffset, bits));
                    }

                    Color ConvertSamples()
                    {
                        for (int component = 0; component < components; component++)
                            componentValues[component] = SampleValue(component);
                        return colorSpace.Convert(componentValues);
                    }
                }
                if (softMask is not null)
                {
                    int maskX = Math.Min((int)((long)sx * softMask.Width / sourceWidth),
                        softMask.Width - 1);
                    int maskY = Math.Min((int)((long)sy * softMask.Height / sourceHeight),
                        softMask.Height - 1);
                    byte maskSample = softMask.Samples[maskY * softMask.Width + maskX];
                    alpha *= maskSample / 255d;
                }
                SetPixel(target, targetWidth, x, y, color, alpha, blendMode);
            }
        }
    }

    private bool TryGetGraphicsState(PdfDictionary resources, PdfName resourceName,
        out double? fillAlpha, out double? strokeAlpha, out RendererBlendMode? blendMode,
        out bool unsupportedBlend)
    {
        fillAlpha = strokeAlpha = null;
        blendMode = null;
        unsupportedBlend = false;
        if (!resources.TryGetValue(Name("ExtGState"), out PdfObject? statesValue)
            || Resolve(statesValue) is not PdfDictionary states
            || !states.TryGetValue(resourceName, out PdfObject? stateValue)
            || Resolve(stateValue) is not PdfDictionary dictionary)
            return false;
        fillAlpha = Alpha(dictionary, "ca");
        strokeAlpha = Alpha(dictionary, "CA");
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

    private static void FillPaths(byte[] pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha, bool evenOdd,
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips,
        CancellationToken cancellationToken)
    {
        var scaled = paths.Where(item => item.Count > 2).Select(item => item.Select(point =>
            new Point(point.X * scaleX, height - point.Y * scaleY)).ToArray()).ToArray();
        if (scaled.Length == 0) return;
        int left = (int)Math.Clamp(Math.Floor(
            scaled.Min(path => path.Min(point => point.X))), 0, width);
        int right = (int)Math.Clamp(Math.Ceiling(
            scaled.Max(path => path.Max(point => point.X))), 0, width);
        int top = (int)Math.Clamp(Math.Floor(
            scaled.Min(path => path.Min(point => point.Y))), 0, height);
        int bottom = (int)Math.Clamp(Math.Ceiling(
            scaled.Max(path => path.Max(point => point.Y))), 0, height);
        bool rectangle = scaled.Length == 1 && IsAxisAlignedRectangle(scaled[0]);
        bool directFill = clips.Count == 0 && alpha >= 1
            && blendMode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        List<(double X, int Winding)>?[] scanlines = rectangle
            ? [] : BuildScanlines();
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rectangle)
            {
                FillSpan(left, right, y);
                continue;
            }
            List<(double X, int Winding)>? intersections = scanlines[y - top];
            if (intersections is null) continue;
            intersections.Sort((first, second) => first.X.CompareTo(second.X));
            int winding = 0;
            bool inside = false;
            double spanStart = 0;
            for (int index = 0; index < intersections.Count;)
            {
                double x = intersections[index].X;
                int windingChange = 0;
                int crossingCount = 0;
                while (index < intersections.Count && intersections[index].X == x)
                {
                    windingChange += intersections[index].Winding;
                    crossingCount++;
                    index++;
                }
                bool wasInside = inside;
                if (evenOdd)
                    inside ^= crossingCount % 2 != 0;
                else
                {
                    winding += windingChange;
                    inside = winding != 0;
                }
                if (!wasInside && inside) spanStart = x;
                else if (wasInside && !inside)
                {
                    int spanLeft = Math.Max(left, (int)Math.Ceiling(spanStart - 0.5));
                    int spanRight = Math.Min(right, (int)Math.Ceiling(x - 0.5));
                    FillSpan(spanLeft, spanRight, y);
                }
            }
        }

        List<(double X, int Winding)>?[] BuildScanlines()
        {
            var result = new List<(double X, int Winding)>?[bottom - top];
            foreach (Point[] polygon in scaled)
            {
                for (int index = 1; index < polygon.Length; index++)
                    AddEdge(polygon[index - 1], polygon[index]);
                if (polygon[0] != polygon[^1]) AddEdge(polygon[^1], polygon[0]);
            }
            return result;

            void AddEdge(Point from, Point to)
            {
                if (from.Y == to.Y) return;
                int firstRow = Math.Max(top,
                    (int)Math.Ceiling(Math.Min(from.Y, to.Y) - 0.5));
                int lastRow = Math.Min(bottom,
                    (int)Math.Ceiling(Math.Max(from.Y, to.Y) - 0.5));
                int winding = from.Y > to.Y ? 1 : -1;
                double slope = (to.X - from.X) / (to.Y - from.Y);
                for (int row = firstRow; row < lastRow; row++)
                {
                    double x = from.X + (row + 0.5 - from.Y) * slope;
                    (result[row - top] ??= []).Add((x, winding));
                }
            }
        }

        void FillSpan(int spanLeft, int spanRight, int y)
        {
            if (directFill)
            {
                int rowOffset = (y * width + spanLeft) * 4;
                int rowEnd = (y * width + spanRight) * 4;
                for (int offset = rowOffset; offset < rowEnd; offset += 4)
                {
                    pixels[offset] = color.Blue;
                    pixels[offset + 1] = color.Green;
                    pixels[offset + 2] = color.Red;
                    pixels[offset + 3] = 255;
                }
                return;
            }
            double pageY = (height - y - 0.5) / scaleY;
            for (int x = spanLeft; x < spanRight; x++)
            {
                double pageX = (x + 0.5) / scaleX;
                if (InsideClips(clips, pageX, pageY))
                    SetPixel(pixels, width, x, y, color, alpha, blendMode);
            }
        }

        static bool IsAxisAlignedRectangle(Point[] polygon)
        {
            if (polygon.Length != 5 || polygon[0] != polygon[^1]) return false;
            double minimumX = polygon.Take(4).Min(point => point.X);
            double maximumX = polygon.Take(4).Max(point => point.X);
            double minimumY = polygon.Take(4).Min(point => point.Y);
            double maximumY = polygon.Take(4).Max(point => point.Y);
            if (maximumX <= minimumX || maximumY <= minimumY) return false;
            for (int index = 1; index < polygon.Length; index++)
            {
                Point from = polygon[index - 1], to = polygon[index];
                bool horizontal = from.Y == to.Y && from.X != to.X;
                bool vertical = from.X == to.X && from.Y != to.Y;
                if (!horizontal && !vertical) return false;
            }
            double twiceArea = 0;
            for (int index = 0; index < 4; index++)
                twiceArea += polygon[index].X * polygon[index + 1].Y
                    - polygon[index + 1].X * polygon[index].Y;
            double expected = 2 * (maximumX - minimumX) * (maximumY - minimumY);
            return Math.Abs(Math.Abs(twiceArea) - expected) <= expected * 1e-12;
        }
    }

    private static void StrokePaths(byte[] pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha,
        double lineWidth, RendererLineCap lineCap, RendererLineJoin lineJoin,
        double miterLimit, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips, CancellationToken cancellationToken)
    {
        double pageRadius = Math.Max(lineWidth / 2,
            Math.Sqrt(0.5) / Math.Min(scaleX, scaleY));
        Point[][] geometry = [.. paths.Where(path => path.Count > 1)
            .Select(path => path.ToArray())];
        if (geometry.Length == 0) return;
        double expansion = pageRadius * (lineJoin == RendererLineJoin.Miter ? miterLimit : 1);
        double minimumX = geometry.Min(path => path.Min(point => point.X)) - expansion;
        double maximumX = geometry.Max(path => path.Max(point => point.X)) + expansion;
        double minimumY = geometry.Min(path => path.Min(point => point.Y)) - expansion;
        double maximumY = geometry.Max(path => path.Max(point => point.Y)) + expansion;
        int left = (int)Math.Clamp(Math.Floor(minimumX * scaleX), 0, width);
        int right = (int)Math.Clamp(Math.Ceiling(maximumX * scaleX), 0, width);
        int top = (int)Math.Clamp(Math.Floor(height - maximumY * scaleY), 0, height);
        int bottom = (int)Math.Clamp(Math.Ceiling(height - minimumY * scaleY), 0, height);
        int regionWidth = right - left;
        int regionHeight = bottom - top;
        if (regionWidth <= 0 || regionHeight <= 0) return;
        var coverage = new System.Collections.BitArray(checked(regionWidth * regionHeight));
        foreach (Point[] path in geometry)
        {
            bool closed = path.Length > 2 && path[0] == path[^1];
            for (int index = 1; index < path.Length; index++)
            {
                Point from = path[index - 1], to = path[index];
                bool first = index == 1 && !closed;
                bool last = index == path.Length - 1 && !closed;
                double capExpansion = lineCap == RendererLineCap.ProjectingSquare
                    && (first || last) ? pageRadius * Math.Sqrt(2) : pageRadius;
                MarkBounds(Math.Min(from.X, to.X) - capExpansion,
                    Math.Max(from.X, to.X) + capExpansion,
                    Math.Min(from.Y, to.Y) - capExpansion,
                    Math.Max(from.Y, to.Y) + capExpansion,
                    (x, y) => IsWithinSegment(from, to, pageRadius, x, y,
                        lineCap, first, last));
            }
            int vertexCount = closed ? path.Length - 1 : path.Length;
            int firstVertex = closed ? 0 : 1;
            int lastVertex = closed ? vertexCount - 1 : vertexCount - 2;
            double joinExpansion = pageRadius
                * (lineJoin == RendererLineJoin.Miter ? miterLimit : 1);
            for (int vertexIndex = firstVertex; vertexIndex <= lastVertex; vertexIndex++)
            {
                int previousIndex = vertexIndex == 0 ? vertexCount - 1 : vertexIndex - 1;
                int nextIndex = (vertexIndex + 1) % vertexCount;
                Point vertex = path[vertexIndex];
                MarkBounds(vertex.X - joinExpansion, vertex.X + joinExpansion,
                    vertex.Y - joinExpansion, vertex.Y + joinExpansion,
                    (x, y) => IsInsideJoin(path[previousIndex], vertex,
                        path[nextIndex], pageRadius, lineJoin, miterLimit, x, y));
            }
        }
        for (int y = top; y < bottom; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double pageY = (height - y - 0.5) / scaleY;
            for (int x = left; x < right; x++)
            {
                if (coverage[(y - top) * regionWidth + x - left])
                {
                    double pageX = (x + 0.5) / scaleX;
                    if (InsideClips(clips, pageX, pageY))
                        SetPixel(pixels, width, x, y, color, alpha, blendMode);
                }
            }
        }

        void MarkBounds(double minimumPageX, double maximumPageX,
            double minimumPageY, double maximumPageY, Func<double, double, bool> contains)
        {
            int candidateLeft = Math.Max(left,
                (int)Math.Clamp(Math.Floor(minimumPageX * scaleX), 0, width));
            int candidateRight = Math.Min(right,
                (int)Math.Clamp(Math.Ceiling(maximumPageX * scaleX), 0, width));
            int candidateTop = Math.Max(top,
                (int)Math.Clamp(Math.Floor(height - maximumPageY * scaleY), 0, height));
            int candidateBottom = Math.Min(bottom,
                (int)Math.Clamp(Math.Ceiling(height - minimumPageY * scaleY), 0, height));
            for (int y = candidateTop; y < candidateBottom; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double pageY = (height - y - 0.5) / scaleY;
                for (int x = candidateLeft; x < candidateRight; x++)
                {
                    double pageX = (x + 0.5) / scaleX;
                    if (contains(pageX, pageY))
                        coverage[(y - top) * regionWidth + x - left] = true;
                }
            }
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

    private static IReadOnlyList<List<Point>> FlattenGlyphOutline(
        PdfGlyphOutline outline, Matrix transform)
    {
        var paths = new List<List<Point>>(outline.Contours.Count);
        foreach (PdfGlyphContour contour in outline.Contours)
        {
            IReadOnlyList<PdfGlyphPoint> points = contour.Points;
            if (points.Count == 0) continue;
            PdfGlyphPoint first = points[0], last = points[^1];
            if (points.Any(point => point.IsCubicControl))
            {
                if (!first.OnCurve) continue;
                var cubicPath = new List<Point> { transform.Apply(first.X, first.Y) };
                bool valid = true;
                for (int cubicIndex = 1; cubicIndex < points.Count;)
                {
                    PdfGlyphPoint point = points[cubicIndex];
                    if (point.OnCurve)
                    {
                        cubicPath.Add(transform.Apply(point.X, point.Y));
                        cubicIndex++;
                    }
                    else if (point.IsCubicControl && cubicIndex + 2 < points.Count
                        && points[cubicIndex + 1].IsCubicControl
                        && points[cubicIndex + 2].OnCurve)
                    {
                        AddCubic(cubicPath, cubicPath[^1],
                            transform.Apply(point.X, point.Y),
                            transform.Apply(points[cubicIndex + 1].X,
                                points[cubicIndex + 1].Y),
                            transform.Apply(points[cubicIndex + 2].X,
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
                paths.Add(cubicPath);
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
            var path = new List<Point> { transform.Apply(start.X, start.Y) };
            PdfGlyphPoint current = start;
            while (consumed < points.Count)
            {
                PdfGlyphPoint point = points[index % points.Count];
                if (point.OnCurve)
                {
                    path.Add(transform.Apply(point.X, point.Y));
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
                    path.Add(transform.Apply(
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
            paths.Add(path);
        }
        return paths;

        static PdfGlyphPoint Midpoint(PdfGlyphPoint first, PdfGlyphPoint second) =>
            new((first.X + second.X) / 2, (first.Y + second.Y) / 2, true);
    }

    private static void SetPixel(byte[] pixels, int width, int x, int y,
        Color color, double opacity, RendererBlendMode blendMode)
    {
        int offset = (y * width + x) * 4;
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
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
        IReadOnlyList<List<Point>> path, ref bool? pendingClipEvenOdd)
    {
        if (!pendingClipEvenOdd.HasValue) return state;
        Point[][] polygons = [.. path.Where(item => item.Count > 1)
            .Select(item => item.ToArray())];
        ClipRegion[] clips = [.. state.Clips,
            new ClipRegion(polygons, pendingClipEvenOdd.Value)];
        pendingClipEvenOdd = null;
        return state with { Clips = Array.AsReadOnly(clips) };
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

    private static bool InsideClips(IReadOnlyList<ClipRegion> clips, double x, double y) =>
        clips.All(clip => clip.Contains(x, y));

    private static bool Contains(IReadOnlyList<Point[]> polygons,
        bool evenOdd, double x, double y)
    {
        int winding = 0;
        int crossings = 0;
        foreach (Point[] polygon in polygons)
            for (int current = 0, previous = polygon.Length - 1;
                current < polygon.Length; previous = current++)
            {
                Point from = polygon[previous], to = polygon[current];
                if ((from.Y > y) != (to.Y > y)
                    && x < (to.X - from.X) * (y - from.Y) / (to.Y - from.Y) + from.X)
                    crossings++;
                double side = (to.X - from.X) * (y - from.Y)
                    - (x - from.X) * (to.Y - from.Y);
                if (from.Y <= y && to.Y > y && side > 0) winding++;
                else if (from.Y > y && to.Y <= y && side < 0) winding--;
            }
        return evenOdd ? crossings % 2 != 0 : winding != 0;
    }

    private static bool IsWithinStroke(IReadOnlyList<Point[]> paths, double radius,
        double x, double y, RendererLineCap lineCap, RendererLineJoin lineJoin,
        double miterLimit)
    {
        foreach (Point[] path in paths)
        {
            bool closed = path.Length > 2 && path[0] == path[^1];
            for (int index = 1; index < path.Length; index++)
                if (IsWithinSegment(path[index - 1], path[index], radius, x, y,
                    lineCap, index == 1 && !closed,
                    index == path.Length - 1 && !closed))
                    return true;
            int vertexCount = closed ? path.Length - 1 : path.Length;
            int firstVertex = closed ? 0 : 1;
            int lastVertex = closed ? vertexCount - 1 : vertexCount - 2;
            for (int vertexIndex = firstVertex; vertexIndex <= lastVertex; vertexIndex++)
            {
                int previousIndex = vertexIndex == 0 ? vertexCount - 1 : vertexIndex - 1;
                int nextIndex = (vertexIndex + 1) % vertexCount;
                if (IsInsideJoin(path[previousIndex], path[vertexIndex], path[nextIndex],
                    radius, lineJoin, miterLimit, x, y))
                    return true;
            }
        }
        return false;
    }

    private static bool IsWithinSegment(Point from, Point to, double radius,
        double x, double y, RendererLineCap lineCap, bool first, bool last)
    {
        double squaredRadius = radius * radius;
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double lengthSquared = dx * dx + dy * dy;
        if (lengthSquared <= 1e-24) return false;
        double raw = ((x - from.X) * dx + (y - from.Y) * dy) / lengthSquared;
        double extension = radius / Math.Sqrt(lengthSquared);
        if (raw < 0 && (!first || lineCap == RendererLineCap.Butt
            || lineCap == RendererLineCap.ProjectingSquare && raw < -extension))
            return false;
        if (raw > 1 && (!last || lineCap == RendererLineCap.Butt
            || lineCap == RendererLineCap.ProjectingSquare && raw > 1 + extension))
            return false;
        double parameter = lineCap == RendererLineCap.ProjectingSquare
            ? Math.Clamp(raw, first ? -extension : 0, last ? 1 + extension : 1)
            : Math.Clamp(raw, 0, 1);
        double offsetX = x - (from.X + parameter * dx);
        double offsetY = y - (from.Y + parameter * dy);
        return offsetX * offsetX + offsetY * offsetY <= squaredRadius;
    }

    private static bool IsInsideJoin(Point previous, Point vertex, Point next,
        double radius, RendererLineJoin lineJoin, double miterLimit, double x, double y)
    {
        double squaredRadius = radius * radius;
        double incomingX = vertex.X - previous.X;
        double incomingY = vertex.Y - previous.Y;
        double outgoingX = next.X - vertex.X;
        double outgoingY = next.Y - vertex.Y;
        double incomingLength = Math.Sqrt(
            incomingX * incomingX + incomingY * incomingY);
        double outgoingLength = Math.Sqrt(
            outgoingX * outgoingX + outgoingY * outgoingY);
        if (incomingLength <= 1e-12 || outgoingLength <= 1e-12) return false;
        incomingX /= incomingLength;
        incomingY /= incomingLength;
        outgoingX /= outgoingLength;
        outgoingY /= outgoingLength;
        double turn = Cross(incomingX, incomingY, outgoingX, outgoingY);
        double dot = incomingX * outgoingX + incomingY * outgoingY;
        if (Math.Abs(turn) <= 1e-12)
            return dot < 0 && lineJoin == RendererLineJoin.Round
                && SquaredDistance(vertex, x, y) <= squaredRadius;
        if (lineJoin == RendererLineJoin.Round)
            return SquaredDistance(vertex, x, y) <= squaredRadius;

        double side = turn > 0 ? 1 : -1;
        Point firstOuter = new(vertex.X + side * incomingY * radius,
            vertex.Y - side * incomingX * radius);
        Point secondOuter = new(vertex.X + side * outgoingY * radius,
            vertex.Y - side * outgoingX * radius);
        Point[] bevel = [vertex, firstOuter, secondOuter];
        if (lineJoin == RendererLineJoin.Bevel) return Contains([bevel], false, x, y);

        double denominator = Cross(incomingX, incomingY, outgoingX, outgoingY);
        double deltaX = secondOuter.X - firstOuter.X;
        double deltaY = secondOuter.Y - firstOuter.Y;
        double distance = Cross(deltaX, deltaY, outgoingX, outgoingY) / denominator;
        Point miter = new(firstOuter.X + distance * incomingX,
            firstOuter.Y + distance * incomingY);
        double miterRatio = Math.Sqrt(SquaredDistance(vertex, miter.X, miter.Y)) / radius;
        return miterRatio <= miterLimit
            ? Contains([[vertex, firstOuter, miter, secondOuter]], false, x, y)
            : Contains([bevel], false, x, y);

        static double Cross(double firstX, double firstY, double secondX, double secondY) =>
            firstX * secondY - firstY * secondX;

        static double SquaredDistance(Point point, double otherX, double otherY)
        {
            double dx = otherX - point.X;
            double dy = otherY - point.Y;
            return dx * dx + dy * dy;
        }
    }

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("A rendering operand is not numeric.")
    };

    private Color ReadPaintColor(ImageColorSpace colorSpace,
        IReadOnlyList<PdfObject> operands)
    {
        if (operands.Count != colorSpace.Components)
            throw new FormatException("A color operator has the wrong component count.");
        return colorSpace.Convert(operands.Select(value => Number(Resolve(value))).ToArray());
    }

    private readonly record struct GraphicsState(
        Matrix Transform, Color Fill, Color Stroke, double FillAlpha, double StrokeAlpha,
        double LineWidth, RendererLineCap LineCap, RendererLineJoin LineJoin,
        double MiterLimit,
        IReadOnlyList<double> DashPattern, double DashPhase,
        RendererBlendMode BlendMode,
        IReadOnlyList<ClipRegion> Clips, bool FillPatternSpace,
        ImageColorSpace? FillPatternBase, TilingPatternPaint? FillPattern,
        bool StrokePatternSpace, ImageColorSpace? StrokePatternBase,
        TilingPatternPaint? StrokePattern, ImageColorSpace? FillColorSpace,
        ImageColorSpace? StrokeColorSpace);
    private enum RendererLineCap { Butt, Round, ProjectingSquare }
    private enum RendererLineJoin { Miter, Round, Bevel }
    private enum RendererBlendMode
    {
        Normal, Compatible, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge,
        ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color,
        Luminosity
    }
    private readonly record struct ColorVector(double Red, double Green, double Blue);
    private sealed record ClipRegion(IReadOnlyList<Point[]> Polygons, bool EvenOdd,
        Func<double, double, bool>? Predicate = null)
    {
        internal bool Contains(double x, double y)
            => Predicate?.Invoke(x, y) ?? PdfPageRenderer.Contains(Polygons, EvenOdd, x, y);
    }
    private sealed record SoftMask(byte[] Samples, int Width, int Height);
    private sealed record TilingPatternPaint(PdfStream Stream, Color? BaseColor);
    private sealed record CalculatorInstruction(double? Number, string? Operator);
    private sealed record ImageColorSpace(int Components, Color[]? Palette,
        Func<double, double, double, double, Color>? Converter = null,
        double[]? DefaultDecode = null, Func<double[], Color>? MultiConverter = null)
    {
        internal Color Convert(double first, double second, double third, double fourth) =>
            Converter?.Invoke(first, second, third, fourth) ?? Components switch
            {
                1 => Color.Gray(first),
                3 => Color.Rgb(first, second, third),
                _ => Color.Cmyk(first, second, third, fourth)
            };
        internal Color Convert(ReadOnlySpan<double> values)
        {
            if (MultiConverter is not null) throw new NotSupportedException();
            return Convert(values[0], values.Length > 1 ? values[1] : 0,
                values.Length > 2 ? values[2] : 0, values.Length > 3 ? values[3] : 0);
        }
        internal Color Convert(double[] values) => Palette is not null
            ? Palette[Math.Clamp((int)Math.Round(values[0]), 0, Palette.Length - 1)]
            : MultiConverter is not null
                ? MultiConverter(values) : Convert((ReadOnlySpan<double>)values);
        internal double DefaultValue(int component, double normalized) => DefaultDecode is null
            ? normalized : DefaultDecode[component * 2] + normalized
                * (DefaultDecode[component * 2 + 1] - DefaultDecode[component * 2]);
    }
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
        internal static Color Black => new(0, 0, 0);
        internal static Color White => new(255, 255, 255);
        internal static Color Gray(double gray) => Rgb(gray, gray, gray);
        internal static Color Rgb(double red, double green, double blue) =>
            new(Channel(red), Channel(green), Channel(blue));
        internal static Color LinearRgb(double red, double green, double blue) =>
            Rgb(Compand(red), Compand(green), Compand(blue));
        internal static Color Cmyk(double cyan, double magenta, double yellow, double black) =>
            Rgb(1 - Math.Min(1, cyan + black), 1 - Math.Min(1, magenta + black),
                1 - Math.Min(1, yellow + black));
        private static byte Channel(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
        private static double Compand(double value) => value <= 0.0031308
            ? 12.92 * value : 1.055 * Math.Pow(value, 1 / 2.4) - 0.055;
    }
}
