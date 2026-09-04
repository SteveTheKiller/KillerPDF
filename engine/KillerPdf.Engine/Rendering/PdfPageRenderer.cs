using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Filters;
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
            1, 1, 1, []);
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
            bool? pendingClipEvenOdd = null;
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
                    if (TryGetGraphicsState(resources, stateName, out double fillAlpha,
                        out double strokeAlpha, out bool unsupportedBlend))
                        state = state with { FillAlpha = fillAlpha, StrokeAlpha = strokeAlpha };
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
                        instruction.Operator == "f*", state.Clips);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "S" or "s" when path.Count > 0:
                    if (instruction.Operator == "s" && subpath is { Count: > 1 }) subpath.Add(subpath[0]);
                    StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Stroke, state.StrokeAlpha, state.LineWidth, state.Clips);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "B" or "B*" or "b" or "b*" when path.Count > 0:
                    if (instruction.Operator[0] == 'b' && subpath is { Count: > 1 })
                        subpath.Add(subpath[0]);
                    FillPaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Fill, state.FillAlpha,
                        instruction.Operator.EndsWith('*'), state.Clips);
                    StrokePaths(pixels, options.Width, options.Height, scaleX, scaleY,
                        path, state.Stroke, state.StrokeAlpha, state.LineWidth, state.Clips);
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "n":
                    state = ApplyPendingClip(state, path, ref pendingClipEvenOdd);
                    path.Clear();
                    subpath = null;
                    break;
                case "BT" or "ET" or "Tf" or "Tm" or "Td" or "TD" or "T*"
                    or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts" or "Tj" or "TJ"
                    or "'" or "\"":
                    diagnostics.Add("Text rendering is not implemented.");
                    break;
                case "Do" when values.Count == 1 && values[0] is PdfName xObjectName:
                    if (!TryGetXObject(resources, xObjectName, out PdfStream? xObject)
                        || xObject is null)
                        diagnostics.Add("An XObject resource could not be resolved.");
                    else if (IsName(xObject.Dictionary, "Subtype", "Image"))
                    {
                        if (!TryRenderImage(xObject, resources, state.Transform, state.Clips,
                            state.Fill, state.FillAlpha, pixels,
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
                        state.Fill, state.FillAlpha, pixels,
                        options.Width, options.Height, scaleX, scaleY,
                        out string? inlineDiagnostic))
                        diagnostics.Add(inlineDiagnostic ?? "Inline-image rendering is not implemented.");
                    break;
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
        if (bits is not (1 or 8))
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
            colorKeyMask, colorSpace, stencilColor, stencilAlpha);
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

    private SoftMask? ReadSoftMask(PdfDictionary dictionary)
    {
        if (!dictionary.TryGetValue(Name("SMask"), out PdfObject? value)) return null;
        PdfObject resolved = Resolve(value);
        if (resolved is PdfName name && name.ValueAsLatin1() == "None") return null;
        if (resolved is not PdfStream stream
            || NameValue(stream.Dictionary, "Subtype") != "Image"
            || NameValue(stream.Dictionary, "ColorSpace") != "DeviceGray"
            || PositiveInteger(stream.Dictionary, "BitsPerComponent") != 8
            || stream.Dictionary.ContainsKey(Name("Mask"))
            || stream.Dictionary.ContainsKey(Name("SMask")))
            throw new NotSupportedException();
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        int expected = checked(width * height);
        byte[] samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
        if (samples.Length != expected)
            throw new FormatException("Image soft-mask sample data has an invalid length.");
        bool inverted = false;
        if (stream.Dictionary.TryGetValue(Name("Decode"), out PdfObject? decodeValue))
        {
            PdfArray decode = ResolveArray(decodeValue, 2, "Image soft-mask decode array");
            inverted = Number(Resolve(decode[0])) > Number(Resolve(decode[1]));
        }
        return new SoftMask(samples, width, height, inverted);
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
        ImageColorSpace colorSpace, Color stencilColor, double stencilAlpha)
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
                    double first = SampleValue(0);
                    color = colorSpace.Palette is not null
                        ? colorSpace.Palette[Math.Min(RawSample(0), colorSpace.Palette.Length - 1)]
                        : colorSpace.Convert(first, components > 1 ? SampleValue(1) : 0,
                            components > 2 ? SampleValue(2) : 0,
                            components > 3 ? SampleValue(3) : 0);
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
                        int bitOffset = sx * components + component;
                        return bits == 8
                            ? samples[sy * rowBytes + bitOffset]
                            : (samples[sy * rowBytes + bitOffset / 8]
                                >> (7 - bitOffset % 8)) & 1;
                    }
                }
                if (softMask is not null)
                {
                    int maskX = Math.Min((int)((long)sx * softMask.Width / sourceWidth),
                        softMask.Width - 1);
                    int maskY = Math.Min((int)((long)sy * softMask.Height / sourceHeight),
                        softMask.Height - 1);
                    byte maskSample = softMask.Samples[maskY * softMask.Width + maskX];
                    alpha *= (softMask.Inverted ? 255 - maskSample : maskSample) / 255d;
                }
                SetPixel(target, targetWidth, x, y, color, alpha);
            }
    }

    private bool TryGetGraphicsState(PdfDictionary resources, PdfName resourceName,
        out double fillAlpha, out double strokeAlpha, out bool unsupportedBlend)
    {
        fillAlpha = strokeAlpha = 1;
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
            unsupportedBlend = blend switch
            {
                PdfName name => name.ValueAsLatin1() != "Normal",
                PdfArray array => !array.Any(item => Resolve(item) is PdfName name
                    && name.ValueAsLatin1() == "Normal"),
                _ => true
            };
        }
        return true;

        double Alpha(PdfDictionary source, string key)
        {
            if (!source.TryGetValue(Name(key), out PdfObject? value)) return 1;
            double alpha = Number(Resolve(value));
            return double.IsFinite(alpha) ? Math.Clamp(alpha, 0, 1) : 1;
        }
    }

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
        IReadOnlyList<ClipRegion> clips)
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
                    SetPixel(pixels, width, x, y, color, alpha);
            }
        }
    }

    private static void StrokePaths(byte[] pixels, int width, int height, double scaleX,
        double scaleY, IReadOnlyList<List<Point>> paths, Color color, double alpha,
        double lineWidth,
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
                    SetPixel(pixels, width, x, y, color, alpha);
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

    private static void SetPixel(byte[] pixels, int width, int x, int y,
        Color color, double opacity)
    {
        int offset = (y * width + x) * 4;
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        double targetAlpha = pixels[offset + 3] / 255d;
        double outputAlpha = sourceAlpha + targetAlpha * (1 - sourceAlpha);
        if (outputAlpha <= 0) return;
        pixels[offset] = Blend(color.Blue, pixels[offset]);
        pixels[offset + 1] = Blend(color.Green, pixels[offset + 1]);
        pixels[offset + 2] = Blend(color.Red, pixels[offset + 2]);
        pixels[offset + 3] = (byte)Math.Round(outputAlpha * 255);

        byte Blend(byte source, byte target) => (byte)Math.Round(
            (source * sourceAlpha + target * targetAlpha * (1 - sourceAlpha)) / outputAlpha);
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
        double LineWidth,
        IReadOnlyList<ClipRegion> Clips);
    private sealed record ClipRegion(IReadOnlyList<Point[]> Polygons, bool EvenOdd)
    {
        internal bool Contains(double x, double y)
            => PdfPageRenderer.Contains(Polygons, EvenOdd, x, y);
    }
    private sealed record SoftMask(byte[] Samples, int Width, int Height, bool Inverted);
    private sealed record ImageColorSpace(int Components, Color[]? Palette,
        Func<double, double, double, double, Color>? Converter = null,
        double[]? DefaultDecode = null)
    {
        internal Color Convert(double first, double second, double third, double fourth) =>
            Converter?.Invoke(first, second, third, fourth) ?? Components switch
            {
                1 => Color.Gray(first),
                3 => Color.Rgb(first, second, third),
                _ => Color.Cmyk(first, second, third, fourth)
            };
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
