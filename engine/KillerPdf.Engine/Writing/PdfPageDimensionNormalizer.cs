using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Writing;

/// <summary>Proportionally scales pages whose dimensions fall outside a required range.</summary>
public static class PdfPageDimensionNormalizer
{
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName QuadPointsName = Name("QuadPoints");
    private static readonly PdfName[] BoxNames =
    [
        Name("MediaBox"), Name("CropBox"), Name("BleedBox"),
        Name("TrimBox"), Name("ArtBox")
    ];

    /// <summary>Returns page indexes whose effective visible width or height is outside the range.</summary>
    public static IReadOnlyList<int> FindPagesOutsideRange(
        PdfDocument document, double minimum, double maximum)
    {
        ValidateRange(document, minimum, maximum);
        PdfPageTree tree = PdfPageTree.Read(document);
        return [.. tree.Pages.Select((page, index) => (dimensions: MediaDimensions(document, page), index))
            .Where(item => item.dimensions.Width < minimum || item.dimensions.Width > maximum
                || item.dimensions.Height < minimum || item.dimensions.Height > maximum)
            .Select(item => item.index)];
    }

    /// <summary>
    /// Appends one byte-preserving revision that proportionally scales selected pages, their
    /// boundaries, content coordinate systems, annotation rectangles, and link quadrilaterals.
    /// </summary>
    public static byte[] NormalizePages(
        PdfDocument document, IReadOnlyCollection<int> pageIndexes,
        double minimum, double maximum)
    {
        ValidateRange(document, minimum, maximum);
        ArgumentNullException.ThrowIfNull(pageIndexes);
        if (pageIndexes.Count == 0) return document.Source.ToArray();

        PdfPageTree tree = PdfPageTree.Read(document);
        int[] selected = [.. pageIndexes.Distinct().Order()];
        if (selected.Any(index => index < 0 || index >= tree.Pages.Count))
            throw new ArgumentOutOfRangeException(nameof(pageIndexes));

        var update = new PdfIncrementalUpdateBuilder(document);
        bool changed = false;
        foreach (int pageIndex in selected)
        {
            var (Width, Height, Corners) = MediaDimensions(document, tree.Pages[pageIndex]);
            double scale = ScaleFor(Width, Height, minimum, maximum, Corners);
            if (Math.Abs(scale - 1) < 1e-12) continue;
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            var entries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);

            string factor = FormatFactor(scale);
            PdfIndirectReference prefix = update.AddObject(Stream($"q {factor} 0 0 {factor} 0 0 cm\n"));
            PdfIndirectReference suffix = update.AddObject(Stream("\nQ\n"));
            var contents = new List<PdfObject> { prefix };
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? contentValue))
            {
                PdfObject resolved = Resolve(document, contentValue).Value;
                if (resolved is PdfArray array)
                    contents.AddRange(array.Select(value => EnsureIndirectStream(update, document, value)));
                else
                    contents.Add(EnsureIndirectStream(update, document, contentValue));
            }
            contents.Add(suffix);
            entries[ContentsName] = new PdfArray(contents);

            foreach (PdfName name in BoxNames)
            {
                PdfObject? value = page.InheritedValues.TryGetValue(name, out PdfObject? inherited)
                    ? inherited : page.Dictionary.TryGetValue(name, out PdfObject? direct) ? direct : null;
                if (value is not null) entries[name] = ScaleNumbers(document, value, scale, 4, "page box");
            }

            if (page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue))
                entries[AnnotsName] = RewriteAnnotations(document, update, annotationsValue, scale);

            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
            changed = true;
        }
        return changed ? update.Build() : document.Source.ToArray();
    }

    private static PdfObject RewriteAnnotations(PdfDocument document,
        PdfIncrementalUpdateBuilder update, PdfObject value, double scale)
    {
        Resolved resolvedArray = Resolve(document, value);
        if (resolvedArray.Value is not PdfArray annotations)
            throw new InvalidOperationException("A page /Annots value is not an array.");
        var rewritten = new PdfObject[annotations.Count];
        for (int index = 0; index < annotations.Count; index++)
        {
            PdfObject original = annotations[index];
            Resolved resolved = Resolve(document, original);
            if (resolved.Value is not PdfDictionary annotation)
                throw new InvalidOperationException("A page annotation is not a dictionary.");
            var entries = annotation.ToDictionary(item => item.Key, item => item.Value);
            if (annotation.TryGetValue(RectName, out PdfObject? rectangle))
                entries[RectName] = ScaleNumbers(document, rectangle, scale, 4, "/Rect");
            if (annotation.TryGetValue(QuadPointsName, out PdfObject? quadrilaterals))
                entries[QuadPointsName] = ScaleNumbers(document, quadrilaterals, scale, null, "/QuadPoints");
            var replacement = new PdfDictionary(entries);
            if (resolved.Reference is PdfIndirectReference reference)
            {
                update.ReplaceObject(reference.ObjectNumber, replacement);
                rewritten[index] = original;
            }
            else rewritten[index] = replacement;
        }
        var replacementArray = new PdfArray(rewritten);
        if (resolvedArray.Reference is PdfIndirectReference arrayReference)
        {
            update.ReplaceObject(arrayReference.ObjectNumber, replacementArray);
            return value;
        }
        return replacementArray;
    }

    private static PdfArray ScaleNumbers(PdfDocument document, PdfObject value,
        double scale, int? exactCount, string description)
    {
        PdfObject resolved = Resolve(document, value).Value;
        if (resolved is not PdfArray array || (exactCount.HasValue && array.Count != exactCount.Value))
            throw new InvalidOperationException($"The page {description} value is not a valid array.");
        return new PdfArray(array.Select(item => Number(document, item, description) * scale)
            .Select(number => (PdfObject)new PdfReal(number)));
    }

    private static PdfObject EnsureIndirectStream(PdfIncrementalUpdateBuilder update,
        PdfDocument document, PdfObject value)
    {
        if (value is PdfIndirectReference) return value;
        PdfObject resolved = Resolve(document, value).Value;
        if (resolved is not PdfStream stream)
            throw new InvalidOperationException("A page /Contents value is not a stream or stream array.");
        return update.AddObject(stream);
    }

    private static double Number(PdfDocument document, PdfObject value, string description) =>
        Resolve(document, value).Value switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real when double.IsFinite(real.Value) => real.Value,
            _ => throw new InvalidOperationException($"The page {description} array contains a nonnumeric value.")
        };

    private static double ScaleFor(double width, double height, double minimum, double maximum,
        double[] corners)
    {
        if (width >= minimum && width <= maximum && height >= minimum && height <= maximum)
            return 1;
        double scale = width > maximum || height > maximum
            ? Math.Min(maximum / width, maximum / height)
            : Math.Max(minimum / width, minimum / height);
        for (int attempt = 0; attempt < 14 && scale > 0 && double.IsFinite(scale); attempt++)
        {
            double scaledWidth = width * scale;
            double scaledHeight = height * scale;
            bool below = scaledWidth < minimum || scaledHeight < minimum;
            bool above = !double.IsFinite(scaledWidth) || !double.IsFinite(scaledHeight)
                || scaledWidth > maximum || scaledHeight > maximum;
            if (!below && !above
                && EmittedBox(corners, scale, out double emittedWidth, out double emittedHeight)
                && emittedWidth <= maximum && emittedHeight <= maximum)
                return scale;
            if (above) scale = Math.BitDecrement(scale);
            else scale = Math.BitIncrement(scale);
        }
        return 1;

    }
    
    private static bool EmittedBox(double[] c, double s, out double w, out double h)
    {
        double x0 = c[0] * s, x2 = c[2] * s;
        double y0 = c[1] * s, y3 = c[3] * s;
        if (!double.IsFinite(x0) || !double.IsFinite(x2) || !double.IsFinite(y0) || !double.IsFinite(y3))
        {
            w = h = double.MaxValue;
            return false;
        }
        w = Math.Abs(x2 - x0);
        h = Math.Abs(y3 - y0);
        return double.IsFinite(w) && double.IsFinite(h) && w > 0 && h > 0;
    }
    /// <summary>
    /// Formats a scale factor for the content-stream CTM exactly as the guard
    /// verified it: the legacy 0.######## format truncates tiny factors
    /// (1.44E-08 became 0.00000001, so a 1,000,000,000,000-point page got a
    /// 14,400-point box while its content drew at 10,000 points), so the
    /// canonical writer format is used whenever the short form would not
    /// parse back to the exact factor.
    /// </summary>
    private static string FormatFactor(double scale)
    {
        string shortForm = scale.ToString("0.########", CultureInfo.InvariantCulture);
        if (double.TryParse(shortForm, NumberStyles.Float, CultureInfo.InvariantCulture,
                out double roundTrip) && roundTrip == scale)
            return shortForm;
        return Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfReal(scale)));
    }

    private static (double Width, double Height, double[] Corners) MediaDimensions(
        PdfDocument document, PdfPageTreeEntry page)
    {
        PdfObject value = page.InheritedValues.TryGetValue(Name("MediaBox"), out PdfObject? media)
            ? media : throw new InvalidOperationException("A page has no effective /MediaBox.");
        PdfArray box = Resolve(document, value).Value as PdfArray
            ?? throw new InvalidOperationException("A page /MediaBox value is not an array.");
        if (box.Count != 4)
            throw new InvalidOperationException("A page /MediaBox does not contain four numbers.");
        double[] corners =
        [
            Number(document, box[0], "/MediaBox"), Number(document, box[1], "/MediaBox"),
            Number(document, box[2], "/MediaBox"), Number(document, box[3], "/MediaBox")
        ];
        return (Math.Abs(corners[2] - corners[0]), Math.Abs(corners[3] - corners[1]), corners);
    }

    private static void ValidateRange(PdfDocument document, double minimum, double maximum)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!double.IsFinite(minimum) || !double.IsFinite(maximum)
            || minimum <= 0 || maximum <= minimum)
            throw new ArgumentOutOfRangeException(nameof(minimum));
    }

    private static PdfStream Stream(string content) =>
        new(new PdfDictionary([]), Encoding.ASCII.GetBytes(content));

    private static Resolved Resolve(PdfDocument document, PdfObject value)
    {
        PdfIndirectReference? terminal = null;
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 64 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A page object has an invalid reference chain.");
            terminal = reference;
            value = document.Resolve(reference);
        }
        return new Resolved(value, terminal);
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    private sealed record Resolved(PdfObject Value, PdfIndirectReference? Reference);
}
