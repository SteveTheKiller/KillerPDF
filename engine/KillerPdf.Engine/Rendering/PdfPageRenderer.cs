using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;

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
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = 255;
            pixels[offset + 1] = 255;
            pixels[offset + 2] = 255;
            pixels[offset + 3] = background;
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
            1, 1, 1, RendererBlendMode.Normal, []);
        var diagnostics = new HashSet<string>();
        var activeForms = new HashSet<PdfStream>();
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
                    state = state with { Fill = Color.Gray(Number(values[0])) };
                    break;
                case "rg" when values.Count == 3:
                    state = state with
                    {
                        Fill = Color.Rgb(Number(values[0]), Number(values[1]), Number(values[2]))
                    };
                    break;
                case "k" when values.Count == 4:
                    state = state with
                    {
                        Fill = Color.Cmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3]))
                    };
                    break;
                case "G" when values.Count == 1:
                    state = state with { Stroke = Color.Gray(Number(values[0])) };
                    break;
                case "RG" when values.Count == 3:
                    state = state with
                    {
                        Stroke = Color.Rgb(Number(values[0]), Number(values[1]), Number(values[2]))
                    };
                    break;
                case "K" when values.Count == 4:
                    state = state with
                    {
                        Stroke = Color.Cmyk(Number(values[0]), Number(values[1]),
                            Number(values[2]), Number(values[3]))
                    };
                    break;
                case "w" when values.Count == 1:
                    state = state with { LineWidth = Math.Max(0, Number(values[0])) };
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
                    FillPaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Fill, state.FillAlpha,
                        instruction.Operator == "f*", state.BlendMode, state.Clips);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "S" or "s" when path.Count > 0:
                    if (instruction.Operator == "s" && subpath is { Count: > 1 }) subpath.Add(subpath[0]);
                    StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Stroke, state.StrokeAlpha, state.LineWidth,
                        state.BlendMode, state.Clips);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "B" or "B*" or "b" or "b*" when path.Count > 0:
                    if (instruction.Operator[0] == 'b' && subpath is { Count: > 1 })
                        subpath.Add(subpath[0]);
                    FillPaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Fill, state.FillAlpha,
                        instruction.Operator.EndsWith('*'), state.BlendMode, state.Clips);
                    StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Stroke, state.StrokeAlpha, state.LineWidth,
                        state.BlendMode, state.Clips);
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
                    else if (IsName(xObject.Dictionary, "Subtype", "Image"))
                    {
                        if (!TryRenderImage(xObject, resources, state.Transform, state.Clips,
                            state.Fill, state.FillAlpha, state.BlendMode, pixels,
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
                        state.Fill, state.FillAlpha, state.BlendMode, pixels,
                        options.Width, options.Height, scaleX, scaleY,
                        out string? inlineDiagnostic))
                        diagnostics.Add(inlineDiagnostic ?? "Inline-image rendering is not implemented.");
                    break;
                case "sh" when values.Count == 1 && values[0] is PdfName shadingName:
                    if (!TryRenderShading(resources, shadingName, state, pixels,
                        options.Width, options.Height, scaleX, scaleY, out string? shadingDiagnostic))
                        diagnostics.Add(shadingDiagnostic ?? "Shading rendering is not implemented.");
                    break;
                }
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
                    extractionFont ??= PdfFontResourceReader.Read(_document, textFont!);
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
                                    state.BlendMode, state.Clips);
                            if (paintMode is 1 or 2)
                                StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                                    glyphPaths, state.Stroke, state.StrokeAlpha, state.LineWidth,
                                    state.BlendMode, state.Clips);
                            if (clipsText)
                                textClipPaths.AddRange(glyphPaths.Select(item => new List<Point>(item)));
                        }
                        else if (paintMode != 3 || clipsText)
                            diagnostics.Add("A text glyph outline is not implemented.");
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

    private bool TryRenderImage(PdfStream stream, PdfDictionary resources, Matrix transform,
        IReadOnlyList<ClipRegion> clips, Color stencilColor, double stencilAlpha,
        RendererBlendMode blendMode,
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
        if (stream.Dictionary.TryGetValue(Name("Mask"), out PdfObject? colorKeyValue))
        {
            if (Resolve(colorKeyValue) is not PdfArray colorKey
                || colorKey.Count != components * 2 || imageMask)
            {
                diagnostic = "Masked-image rendering is not implemented.";
                return false;
            }
            colorKeyMask = colorKey.Select(item => Resolve(item) is PdfInteger integer
                    && integer.Value >= 0 && integer.Value <= (1 << bits) - 1
                    ? (int)integer.Value
                    : throw new FormatException("An image color-key mask range is invalid."))
                .ToArray();
        }
        try
        {
            int expected = checked(((width * components * bits + 7) / 8) * height);
            samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
            if (samples.Length != expected) throw new FormatException("Image sample data has an invalid length.");
            softMask = ReadSoftMask(stream.Dictionary);
            decode = ReadImageDecode(stream.Dictionary, colorSpace, imageMask);
        }
        catch (PdfFilterException)
        {
            diagnostic = "The image compression filter is not implemented.";
            return false;
        }
        catch (NotSupportedException)
        {
            diagnostic = "The image soft mask is not implemented.";
            return false;
        }
        PaintImage(target, targetWidth, targetHeight, scaleX, scaleY,
            transform, samples, width, height, components, bits, clips,
            imageMask, imageMask && StencilPaintsOne(stream.Dictionary), softMask, decode,
            colorKeyMask, colorSpace, stencilColor, stencilAlpha, blendMode);
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
            Func<double[], Color> tintTransform = ReadMultidimensionalSampledFunction(
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
            || bitsInteger.Value is not (1 or 2 or 4 or 8 or 12 or 16))
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
        uint maximum = (1u << bits) - 1;
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

    private Func<double[], Color> ReadMultidimensionalSampledFunction(PdfObject value,
        int inputCount, ImageColorSpace colorSpace, string description)
    {
        PdfObject resolved = Resolve(value);
        if (resolved is not PdfStream stream)
            throw new NotSupportedException();
        PdfDictionary dictionary = stream.Dictionary;
        if (!dictionary.TryGetValue(Name("FunctionType"), out PdfObject? typeValue)
            || Resolve(typeValue) is not PdfInteger { Value: 0 })
            throw new NotSupportedException();
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
            || bitsInteger.Value is not (1 or 2 or 4 or 8 or 12 or 16))
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
        uint maximum = (1u << bits) - 1;
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
        double scaleX, double scaleY, out string? diagnostic)
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
        RendererBlendMode blendMode)
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
        var componentValues = new double[components];
        for (int y = top; y < bottom; y++)
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
        RendererBlendMode blendMode, IReadOnlyList<ClipRegion> clips)
    {
        var scaled = paths.Where(item => item.Count > 2).Select(item => item.Select(point =>
            new Point(point.X * scaleX, height - point.Y * scaleY)).ToArray()).ToArray();
        for (int y = 0; y < height; y++)
        {
            double sampleY = y + 0.5;
            for (int x = 0; x < width; x++)
            {
                double sampleX = x + 0.5;
                if (Contains(scaled, evenOdd, sampleX, sampleY)
                    && InsideClips(clips, sampleX / scaleX, (height - sampleY) / scaleY))
                    SetPixel(pixels, width, x, y, color, alpha, blendMode);
            }
        }
    }

    private static void StrokePaths(byte[] pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha,
        double lineWidth, RendererBlendMode blendMode,
        IReadOnlyList<ClipRegion> clips)
    {
        int radius = Math.Max(0, (int)Math.Ceiling(lineWidth * Math.Max(scaleX, scaleY) / 2));
        var coverage = new bool[checked(width * height)];
        int left = width, right = 0, top = height, bottom = 0;
        foreach (List<Point> path in paths)
            for (int i = 1; i < path.Count; i++)
            {
                Point from = new(path[i - 1].X * scaleX, height - path[i - 1].Y * scaleY);
                Point to = new(path[i].X * scaleX, height - path[i].Y * scaleY);
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y))));
                for (int step = 0; step <= steps; step++)
                {
                    int cx = (int)Math.Round(from.X + (to.X - from.X) * step / steps);
                    int cy = (int)Math.Round(from.Y + (to.Y - from.Y) * step / steps);
                    for (int yy = cy - radius; yy <= cy + radius; yy++)
                        for (int xx = cx - radius; xx <= cx + radius; xx++)
                            if (xx >= 0 && xx < width && yy >= 0 && yy < height)
                                if (InsideClips(clips, (xx + 0.5) / scaleX,
                                    (height - yy - 0.5) / scaleY))
                                {
                                    coverage[yy * width + xx] = true;
                                    left = Math.Min(left, xx);
                                    right = Math.Max(right, xx + 1);
                                    top = Math.Min(top, yy);
                                    bottom = Math.Max(bottom, yy + 1);
                                }
                }
            }
        for (int y = top; y < bottom; y++)
            for (int x = left; x < right; x++)
                if (coverage[y * width + x])
                    SetPixel(pixels, width, x, y, color, alpha, blendMode);
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

    private static double Number(PdfObject value) => value switch
    {
        PdfInteger integer => integer.Value,
        PdfReal real => real.Value,
        _ => throw new FormatException("A rendering operand is not numeric.")
    };

    private readonly record struct GraphicsState(
        Matrix Transform, Color Fill, Color Stroke, double FillAlpha, double StrokeAlpha,
        double LineWidth, RendererBlendMode BlendMode,
        IReadOnlyList<ClipRegion> Clips);
    private enum RendererBlendMode
    {
        Normal, Compatible, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge,
        ColorBurn, HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color,
        Luminosity
    }
    private readonly record struct ColorVector(double Red, double Green, double Blue);
    private sealed record ClipRegion(IReadOnlyList<Point[]> Polygons, bool EvenOdd)
    {
        internal bool Contains(double x, double y)
            => PdfPageRenderer.Contains(Polygons, EvenOdd, x, y);
    }
    private sealed record SoftMask(byte[] Samples, int Width, int Height);
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
        internal Color Convert(double[] values) => MultiConverter is not null
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
