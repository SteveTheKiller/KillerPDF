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
        var state = new GraphicsState(normalize.Then(rotate), Color.Black, Color.Black,
            1, 1, 1, []);
        var stack = new Stack<GraphicsState>();
        var path = new List<List<Point>>();
        List<Point>? subpath = null;
        bool? pendingClipEvenOdd = null;
        var diagnostics = new HashSet<string>();
        foreach (PdfContentInstruction instruction in _content.ReadInstructions(
            pageIndex, cancellationToken))
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
                    if (TryGetGraphicsState(pageIndex, stateName, out double fillAlpha,
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
                case "Do" when values.Count == 1 && values[0] is PdfName imageName:
                    if (!TryGetImage(pageIndex, imageName, out PdfStream? image) || image is null)
                        diagnostics.Add("Form XObject rendering is not implemented.");
                    else if (!TryRenderImage(image, state.Transform, state.Clips, pixels,
                        options.Width, options.Height, scaleX, scaleY,
                        out string? imageDiagnostic))
                        diagnostics.Add(imageDiagnostic ?? "Image rendering is not implemented.");
                    break;
                case "BI" when values.Count == 1 && values[0] is PdfDictionary inlineDictionary
                    && instruction.InlineImageData.HasValue:
                    var inlineImage = new PdfStream(inlineDictionary,
                        instruction.InlineImageData.Value.Span);
                    if (!TryRenderImage(inlineImage, state.Transform, state.Clips, pixels,
                        options.Width, options.Height, scaleX, scaleY,
                        out string? inlineDiagnostic))
                        diagnostics.Add(inlineDiagnostic ?? "Inline-image rendering is not implemented.");
                    break;
            }
        }
        if (options.IncludeAnnotations)
            diagnostics.Add("Annotation rendering is not implemented.");
        if (options.IncludeFormFields)
            diagnostics.Add("Form-field rendering is not implemented.");
        return new PdfRenderedPage(options.Width, options.Height, pixels, diagnostics);
    }

    private bool TryGetImage(int pageIndex, PdfName resourceName, out PdfStream? image)
    {
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        image = page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            && Resolve(resourcesValue) is PdfDictionary resources
            && resources.TryGetValue(Name("XObject"), out PdfObject? xObjectsValue)
            && Resolve(xObjectsValue) is PdfDictionary xObjects
            && xObjects.TryGetValue(resourceName, out PdfObject? imageValue)
            && Resolve(imageValue) is PdfStream stream
            && IsName(stream.Dictionary, "Subtype", "Image") ? stream : null;
        return image is not null;
    }

    private bool TryRenderImage(PdfStream stream, Matrix transform,
        IReadOnlyList<ClipRegion> clips,
        byte[] target, int targetWidth, int targetHeight, double scaleX, double scaleY,
        out string? diagnostic)
    {
        diagnostic = null;
        if (stream.Dictionary.ContainsKey(Name("Mask"))
            || stream.Dictionary.ContainsKey(Name("SMask"))
            || stream.Dictionary.TryGetValue(Name("ImageMask"), out PdfObject? maskValue)
                && Resolve(maskValue) is PdfBoolean { Value: true })
        {
            diagnostic = "Masked-image rendering is not implemented.";
            return false;
        }
        int width = PositiveInteger(stream.Dictionary, "Width");
        int height = PositiveInteger(stream.Dictionary, "Height");
        int bits = PositiveInteger(stream.Dictionary, "BitsPerComponent");
        string colorSpace = NameValue(stream.Dictionary, "ColorSpace");
        int components = colorSpace switch
        {
            "DeviceGray" => 1,
            "DeviceRGB" => 3,
            "DeviceCMYK" => 4,
            _ => 0
        };
        if (components == 0 || bits is not (1 or 8))
        {
            diagnostic = "The image color space or sample depth is not implemented.";
            return false;
        }
        byte[] samples;
        try
        {
            int expected = checked(((width * components * bits + 7) / 8) * height);
            samples = PdfStreamDecoder.Decode(stream, _document.Resolve, expected);
            if (samples.Length != expected) throw new FormatException("Image sample data has an invalid length.");
        }
        catch (PdfFilterException)
        {
            diagnostic = "The image compression filter is not implemented.";
            return false;
        }
        PaintImage(target, targetWidth, targetHeight, scaleX, scaleY,
            transform, samples, width, height, components, bits, clips);
        return true;
    }

    private static void PaintImage(byte[] target, int targetWidth, int targetHeight,
        double scaleX, double scaleY, Matrix transform, byte[] samples,
        int sourceWidth, int sourceHeight, int components, int bits,
        IReadOnlyList<ClipRegion> clips)
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
                if (bits == 1)
                {
                    int bit = sx * components;
                    bool white = (samples[sy * rowBytes + bit / 8] & (0x80 >> (bit & 7))) != 0;
                    color = white ? Color.White : Color.Black;
                }
                else
                {
                    int offset = sy * rowBytes + sx * components;
                    color = components switch
                    {
                        1 => new Color(samples[offset], samples[offset], samples[offset]),
                        3 => new Color(samples[offset], samples[offset + 1], samples[offset + 2]),
                        _ => Color.Cmyk(samples[offset] / 255d, samples[offset + 1] / 255d,
                            samples[offset + 2] / 255d, samples[offset + 3] / 255d)
                    };
                }
                SetPixel(target, targetWidth, x, y, color, 1);
            }
    }

    private bool TryGetGraphicsState(int pageIndex, PdfName resourceName,
        out double fillAlpha, out double strokeAlpha, out bool unsupportedBlend)
    {
        fillAlpha = strokeAlpha = 1;
        unsupportedBlend = false;
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        if (!page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            || Resolve(resourcesValue) is not PdfDictionary resources
            || !resources.TryGetValue(Name("ExtGState"), out PdfObject? statesValue)
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
        internal static Color Cmyk(double cyan, double magenta, double yellow, double black) =>
            Rgb(1 - Math.Min(1, cyan + black), 1 - Math.Min(1, magenta + black),
                1 - Math.Min(1, yellow + black));
        private static byte Channel(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
