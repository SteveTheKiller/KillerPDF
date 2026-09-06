using System.Text;
using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Filters;
using KillerPdf.Engine.Fonts;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using Matrix = KillerPdf.Engine.Parsing.PdfTextContentReader.Matrix;

namespace KillerPdf.Engine.Documents;

/// <summary>Extracts text and image placements from document page resources and content streams.</summary>
public sealed class PdfPageContentReader
{
    private readonly PdfDocument _document;
    private readonly PdfPageTree _tree;
    private static readonly PdfDictionary Empty = new([]);

    /// <summary>Creates a reader for an immutable document.</summary>
    public PdfPageContentReader(PdfDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
        if (!document.IsDecrypted) throw new InvalidOperationException("Authenticate the document before extracting page content.");
        _tree = PdfPageTree.Read(document);
    }
    /// <summary>Gets the number of pages.</summary>
    public int PageCount => _tree.Pages.Count;

    /// <summary>Reads the unexpanded instructions in a page's decoded content streams.</summary>
    public IReadOnlyList<PdfContentInstruction> ReadInstructions(
        int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = _tree.Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(Name("Contents"), out PdfObject? content)) return [];
        PdfDictionary resources = page.InheritedValues.TryGetValue(Name("Resources"), out PdfObject? inherited)
            ? Resolve(inherited) as PdfDictionary ?? Empty : Empty;
        using var output = new MemoryStream();
        PdfObject resolved = Resolve(content);
        IEnumerable<PdfObject> items = resolved is PdfArray array ? array : [resolved];
        foreach (PdfObject item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Resolve(item) is PdfNull) continue;
            PdfStream stream = Resolve(item) as PdfStream
                ?? throw new FormatException("Page content is not a stream.");
            byte[] bytes = _document.DecodeStream(
                stream, PdfContentStreamReader.MaximumSourceBytes);
            if (output.Length + bytes.Length + 1 > PdfContentStreamReader.MaximumSourceBytes)
                throw new FormatException("Page content exceeds the extraction limit.");
            output.Write(bytes);
            output.WriteByte((byte)'\n');
        }
        return PdfContentStreamReader.Read(output.ToArray(), cancellationToken: cancellationToken,
            resolveColorComponents: name => ColorComponents(name, resources, 0),
            compatibilityRecovery: _document.UsesCompatibilityRecovery);

        int? ColorComponents(PdfObject value, PdfDictionary current, int depth)
        {
            if (depth >= 32) throw new FormatException("Color space nesting limit exceeded.");
            value = Resolve(value);
            if (value is PdfName name)
            {
                int? standard = name.ValueAsLatin1() switch
                {
                    "DeviceGray" or "G" => 1,
                    "DeviceRGB" or "RGB" => 3,
                    "DeviceCMYK" or "CMYK" => 4,
                    _ => null
                };
                if (standard.HasValue) return standard;
                if (!current.TryGetValue(Name("ColorSpace"), out PdfObject? spacesValue)
                    || Resolve(spacesValue) is not PdfDictionary spaces
                    || !spaces.TryGetValue(name, out PdfObject? namedValue))
                    return null;
                return ColorComponents(namedValue, current, depth + 1);
            }
            if (value is not PdfArray array || array.Count == 0
                || Resolve(array[0]) is not PdfName family)
                return null;
            return family.ValueAsLatin1() switch
            {
                "CalGray" or "Indexed" or "I" or "Separation" => 1,
                "CalRGB" or "Lab" => 3,
                "DeviceN" when array.Count > 1 && Resolve(array[1]) is PdfArray names => names.Count,
                "ICCBased" when array.Count > 1 && Resolve(array[1]) is PdfStream profile
                    && profile.Dictionary.TryGetValue(Name("N"), out PdfObject? count) =>
                    checked((int)Number(count)),
                _ => null
            };
        }
    }

    /// <summary>Extracts one zero-based page in unrotated, crop-relative PDF points.</summary>
    public PdfPageContent Read(int pageIndex, CancellationToken cancellationToken = default)
    {
        if (pageIndex < 0 || pageIndex >= PageCount) throw new ArgumentOutOfRangeException(nameof(pageIndex));
        var page = _tree.Pages[pageIndex];
        PdfObject boxValue = page.InheritedValues.TryGetValue(Name("CropBox"), out var crop) ? crop
            : page.InheritedValues[Name("MediaBox")];
        var box = Box(boxValue);
        var resources = page.InheritedValues.TryGetValue(Name("Resources"), out var inherited)
            ? Resolve(inherited) as PdfDictionary ?? Empty : Empty;
        var instructions = new List<PdfContentInstruction>();
        var interpretedInstructions = new List<PdfContentInstruction>();
        var fonts = new Dictionary<string, PdfExtractionFont>();
        var fontNames = new Dictionary<PdfDictionary, string>();
        var images = new List<PdfExtractedImage>();
        var paths = new List<PdfExtractedPath>();
        var shadings = new List<PdfExtractedShading>();
        var diagnostics = new HashSet<string>();
        var activeForms = new HashSet<PdfStream>();
        long decodedBytes = 0;
        int visitedInstructions = 0;
        var initial = new Matrix(1, 0, 0, 1, -box.Left, -box.Bottom);
        instructions.Add(Instruction("cm", 1, 0, 0, 1, -box.Left, -box.Bottom));
        if (page.Dictionary.TryGetValue(Name("Contents"), out var content))
            Walk(ContentBytes(content), resources, initial, new(0, 0, box.Width, box.Height), 0);
        var text = PdfTextContentReader.ReadInstructions(instructions, fonts, cancellationToken: cancellationToken);
        return new PdfPageContent(box.Width, box.Height,
            text.Select(t => new PdfExtractedLetter(t.Text, t.Bounds, t.FontName, t.FontSize,
                t.PointSize, t.Origin, t.AdvanceEnd)), images, interpretedInstructions,
            paths, shadings, diagnostics);

        string Font(PdfObject value)
        {
            var dictionary = Resolve(value) as PdfDictionary ?? throw new FormatException("Font resource is not a dictionary.");
            if (!fontNames.TryGetValue(dictionary, out var key))
            {
                key = "ExtractedFont" + fonts.Count;
                fonts.Add(key, PdfFontResourceReader.Read(_document, dictionary));
                fontNames.Add(dictionary, key);
            }
            return key;
        }
        void Add(PdfContentInstruction instruction)
        {
            if (instructions.Count >= 1_000_000) throw new FormatException("Expanded page instruction limit exceeded.");
            instructions.Add(instruction);
        }
        byte[] ContentBytes(PdfObject value)
        {
            using var output = new MemoryStream();
            var resolved = Resolve(value);
            IEnumerable<PdfObject> items = resolved is PdfArray array ? array : new[] { resolved };
            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Resolve(item) is PdfNull) continue;
                var stream = Resolve(item) as PdfStream ?? throw new FormatException("Page content is not a stream.");
                var bytes = Decode(stream);
                if (output.Length + bytes.Length + 1 > PdfContentStreamReader.MaximumSourceBytes)
                    throw new FormatException("Page content exceeds the extraction limit.");
                output.Write(bytes);
                output.WriteByte(10);
            }
            return output.ToArray();
        }
        byte[] Decode(PdfStream stream)
        {
            byte[] bytes = _document.DecodeStream(
                stream, PdfContentStreamReader.MaximumSourceBytes);
            decodedBytes += bytes.Length;
            if (decodedBytes > PdfContentStreamReader.MaximumSourceBytes) throw new FormatException("Expanded page content exceeds the extraction limit.");
            return bytes;
        }
        PdfObject Resource(PdfDictionary current, string category, PdfName key)
        {
            if (!current.TryGetValue(Name(category), out var categoryValue) || Resolve(categoryValue) is not PdfDictionary dictionary ||
                !dictionary.TryGetValue(key, out var value)) throw new FormatException($"Missing {category} resource {key.ValueAsLatin1()}.");
            return value;
        }
        void Walk(byte[] bytes, PdfDictionary current, Matrix ctm, PdfContentBounds clip, int depth)
        {
            if (depth >= 32) throw new FormatException("Form nesting limit exceeded.");
            var stack = new Stack<(Matrix Matrix, PdfContentBounds Clip)>();
            var path = new List<PdfContentBounds>();
            var pathSegments = new List<PdfExtractedPathSegment>();
            bool pendingClip = false;
            foreach (var instruction in PdfContentStreamReader.Read(bytes, cancellationToken: cancellationToken,
                resolveColorComponents: name => ColorComponents(Resource(current, "ColorSpace", name), current, 0),
                compatibilityRecovery: _document.UsesCompatibilityRecovery))
            {
                cancellationToken.ThrowIfCancellationRequested();
                interpretedInstructions.Add(instruction);
                var args = instruction.Operands;
                if (++visitedInstructions > 1_000_000)
                    throw new FormatException("Expanded page instruction limit exceeded.");
                switch (instruction.Operator)
                {
                    case "q":
                        if (stack.Count >= 256) throw new FormatException("Graphics state nesting limit exceeded.");
                        stack.Push((ctm, clip)); break;
                    case "Q":
                        if (!stack.TryPop(out var saved)) throw new FormatException("Unbalanced graphics state.");
                        ctm = saved.Matrix; clip = saved.Clip; break;
                    case "cm":
                        if (args.Count != 6) throw new FormatException("Invalid transformation matrix.");
                        ctm = new Matrix(Number(args[0]), Number(args[1]), Number(args[2]), Number(args[3]), Number(args[4]), Number(args[5])).Then(ctm); break;
                    case "Tf":
                        if (args.Count != 2 || args[0] is not PdfName fontName) throw new FormatException("Invalid font selection.");
                        Add(new PdfContentInstruction("Tf", instruction.Offset, [Name(Font(Resource(current, "Font", fontName))), args[1]]));
                        continue;
                    case "gs":
                        if (args.Count != 1 || args[0] is not PdfName gsName)
                            throw new FormatException("Invalid graphics state resource.");
                        if (!current.TryGetValue(Name("ExtGState"), out var states) || Resolve(states) is not PdfDictionary stateResources ||
                            !stateResources.TryGetValue(gsName, out var stateValue) || Resolve(stateValue) is not PdfDictionary gs)
                        {
                            diagnostics.Add("A missing graphics state resource was ignored.");
                            continue;
                        }
                        if (gs.TryGetValue(Name("Font"), out var gsFont) && Resolve(gsFont) is PdfArray fontArray && fontArray.Count == 2)
                            Add(new PdfContentInstruction("Tf", instruction.Offset, [Name(Font(fontArray[0])), Resolve(fontArray[1])]));
                        continue;
                    case "Do":
                        if (args.Count != 1 || args[0] is not PdfName xName || Resolve(Resource(current, "XObject", xName)) is not PdfStream xobject)
                            throw new FormatException("Invalid XObject resource.");
                        var subtype = xobject.Dictionary.TryGetValue(Name("Subtype"), out var type) ? Resolve(type) as PdfName : null;
                        if (subtype?.ValueAsLatin1() == "Image")
                        {
                            Image(ctm, clip, xName.ValueAsLatin1(), false, xobject.Dictionary);
                            continue;
                        }
                        if (subtype?.ValueAsLatin1() != "Form") continue;
                        if (!activeForms.Add(xobject)) throw new FormatException("Cyclic form XObject.");
                        try
                        {
                            Matrix formMatrix = Matrix.Identity;
                            if (xobject.Dictionary.TryGetValue(Name("Matrix"), out var matrixValue))
                            {
                                if (Resolve(matrixValue) is not PdfArray matrix || matrix.Count != 6) throw new FormatException("Invalid form matrix.");
                                formMatrix = new(Number(matrix[0]), Number(matrix[1]), Number(matrix[2]), Number(matrix[3]), Number(matrix[4]), Number(matrix[5]));
                            }
                            var formCtm = formMatrix.Then(ctm);
                            var formClip = xobject.Dictionary.TryGetValue(Name("BBox"), out var bounds)
                                ? Intersect(clip, Transform(Box(bounds), formCtm)) : clip;
                            var formResources = xobject.Dictionary.TryGetValue(Name("Resources"), out var r)
                                ? Resolve(r) as PdfDictionary ?? Empty : current;
                            Add(Instruction("q"));
                            Add(Instruction("cm", formMatrix.A, formMatrix.B, formMatrix.C, formMatrix.D, formMatrix.E, formMatrix.F));
                            Walk(Decode(xobject), formResources, formCtm, formClip, depth + 1);
                            Add(Instruction("Q"));
                        }
                        finally { activeForms.Remove(xobject); }
                        continue;
                    case "BI":
                        if (args.Count != 1 || args[0] is not PdfDictionary inlineImage)
                            throw new FormatException("Invalid inline image dictionary.");
                        Image(ctm, clip, null, true, inlineImage);
                        continue;
                    case "sh":
                        if (args.Count != 1 || args[0] is not PdfName shadingName
                            || Resolve(Resource(current, "Shading", shadingName))
                                is not PdfDictionary shading
                            || !shading.TryGetValue(Name("ShadingType"),
                                out PdfObject? shadingTypeValue)
                            || Resolve(shadingTypeValue) is not PdfInteger shadingType
                            || shadingType.Value is < 1 or > 7)
                            throw new FormatException("Invalid shading resource.");
                        shadings.Add(new PdfExtractedShading(
                            shadingName.ValueAsLatin1(), (int)shadingType.Value, clip));
                        break;
                    case "BDC":
                        if (args.Count == 2 && args[1] is PdfName propertyName)
                        {
                            Add(new PdfContentInstruction("BDC", instruction.Offset,
                                [args[0], Resolve(Resource(current, "Properties", propertyName))]));
                            continue;
                        }
                        break;
                    case "re":
                        if (args.Count == 4)
                        {
                            double x = Number(args[0]), y = Number(args[1]), w = Number(args[2]), h = Number(args[3]);
                            PdfPoint[] points = [ctm.Point(x, y), ctm.Point(x + w, y),
                                ctm.Point(x + w, y + h), ctm.Point(x, y + h)];
                            path.Add(new(points.Min(point => point.X), points.Min(point => point.Y),
                                points.Max(point => point.X), points.Max(point => point.Y)));
                            pathSegments.Add(new("re", Array.AsReadOnly(points)));
                        }
                        break;
                    case "m": case "l": case "c": case "v": case "y":
                        var segmentPoints = new List<PdfPoint>();
                        for (int i = 0; i + 1 < args.Count; i += 2)
                        {
                            var point = ctm.Point(Number(args[i]), Number(args[i + 1]));
                            path.Add(new(point.X, point.Y, point.X, point.Y));
                            segmentPoints.Add(point);
                        }
                        pathSegments.Add(new(instruction.Operator, segmentPoints.AsReadOnly()));
                        break;
                    case "h": pathSegments.Add(new("h", Array.Empty<PdfPoint>())); break;
                    case "W": case "W*": pendingClip = true; break;
                    case "n": case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*":
                        if (path.Count > 0)
                            paths.Add(new PdfExtractedPath(Array.AsReadOnly(pathSegments.ToArray()),
                                PdfContentBounds.Union(path), instruction.Operator, pendingClip));
                        if (pendingClip) clip = path.Count == 0 ? default : Intersect(clip, PdfContentBounds.Union(path));
                        pendingClip = false; path.Clear(); pathSegments.Clear(); break;
                }
                Add(instruction);
            }
            if (stack.Count != 0)
            {
                diagnostics.Add("Unclosed graphics states were restored at the end of the content stream.");
                while (stack.Count > 0) { stack.Pop(); Add(Instruction("Q")); }
            }
        }
        int? ColorComponents(PdfObject value, PdfDictionary current, int depth)
        {
            if (depth >= 32) throw new FormatException("Color space nesting limit exceeded.");
            value = Resolve(value);
            if (value is PdfName name)
                return name.ValueAsLatin1() switch
                {
                    "DeviceGray" or "G" => 1,
                    "DeviceRGB" or "RGB" => 3,
                    "DeviceCMYK" or "CMYK" => 4,
                    _ => ColorComponents(Resource(current, "ColorSpace", name), current, depth + 1)
                };
            if (value is not PdfArray array || array.Count == 0 || Resolve(array[0]) is not PdfName family) return null;
            return family.ValueAsLatin1() switch
            {
                "CalGray" or "Indexed" or "I" or "Separation" => 1,
                "CalRGB" or "Lab" => 3,
                "DeviceN" when array.Count > 1 && Resolve(array[1]) is PdfArray names => names.Count,
                "ICCBased" when array.Count > 1 && Resolve(array[1]) is PdfStream profile &&
                    profile.Dictionary.TryGetValue(Name("N"), out var count) => checked((int)Number(count)),
                _ => null
            };
        }
        void Image(Matrix matrix, PdfContentBounds clip, string? resourceName, bool isInline, PdfDictionary dictionary)
        {
            var bounds = Intersect(Transform(new(0, 0, 1, 1), matrix), clip);
            double renderedWidth = Math.Sqrt(matrix.A * matrix.A + matrix.B * matrix.B);
            double renderedHeight = Math.Sqrt(matrix.C * matrix.C + matrix.D * matrix.D);
            int pixelWidth = ImageDimension(dictionary, "Width");
            int pixelHeight = ImageDimension(dictionary, "Height");
            if (bounds.Width > 0 && bounds.Height > 0)
                images.Add(new(bounds, resourceName, isInline, pixelWidth, pixelHeight, renderedWidth, renderedHeight));
        }
        int ImageDimension(PdfDictionary dictionary, string key)
        {
            if (!dictionary.TryGetValue(Name(key), out var value)) return 0;
            double number = Number(value);
            return number > 0 && number <= int.MaxValue && number == Math.Truncate(number) ? (int)number : 0;
        }
    }

    private PdfObject Resolve(PdfObject value)
    {
        for (int i = 0; value is PdfIndirectReference reference; i++)
        {
            if (i >= 32) throw new FormatException("Resource reference nesting limit exceeded.");
            value = _document.Resolve(reference);
        }
        return value;
    }
    private double Number(PdfObject value) => Resolve(value) switch
    { PdfInteger i => i.Value, PdfReal r => r.Value, _ => throw new FormatException("Expected a resource number.") };
    private PdfContentBounds Box(PdfObject value)
    {
        if (Resolve(value) is not PdfArray array || array.Count != 4) throw new FormatException("Invalid page or form bounds.");
        double x1 = Number(array[0]), y1 = Number(array[1]), x2 = Number(array[2]), y2 = Number(array[3]);
        return new(Math.Min(x1, x2), Math.Min(y1, y2), Math.Max(x1, x2), Math.Max(y1, y2));
    }
    private static PdfContentBounds Transform(PdfContentBounds box, Matrix matrix)
    {
        var points = new[] { matrix.Point(box.Left, box.Bottom), matrix.Point(box.Right, box.Bottom),
            matrix.Point(box.Left, box.Top), matrix.Point(box.Right, box.Top) };
        return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }
    private static PdfContentBounds Intersect(PdfContentBounds a, PdfContentBounds b) =>
        new(Math.Max(a.Left, b.Left), Math.Max(a.Bottom, b.Bottom), Math.Min(a.Right, b.Right), Math.Min(a.Top, b.Top));
    private static PdfName Name(string name) => new(Encoding.ASCII.GetBytes(name));
    private static PdfContentInstruction Instruction(string op, params double[] values) =>
        new(op, 0, values.Select(v => (PdfObject)new PdfReal(v)));
}
