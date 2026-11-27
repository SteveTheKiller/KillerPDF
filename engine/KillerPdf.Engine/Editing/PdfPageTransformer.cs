using System.Globalization;
using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Parsing;
using KillerPdf.Engine.Rendering;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>
/// A page's new geometry: the matrix [A B C D E F] maps the page's default user space, with any /Rotate
/// ignored, onto a new page whose media and crop boxes are [0 0 Width Height].
/// </summary>
public readonly record struct PdfPageTransform(double A, double B, double C, double D, double E, double F,
    double Width, double Height)
{
    internal (double X, double Y) Apply(double x, double y) => (A * x + C * y + E, B * x + D * y + F);
}

/// <summary>
/// Turns, scales and mirrors pages in place. The page keeps its object, content streams and marked content, so
/// text stays selectable and the structure tree still points at it. Links move with the page; every other
/// visible annotation, form widgets included, is painted into the page as the page render showed it and removed.
/// A field keeps its widgets on pages that are not transformed.
/// </summary>
public static partial class PdfPageTransformer
{
    private const int HiddenFlag = 2;
    private const int NoViewFlag = 32;
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName ResourcesName = Name("Resources");
    private static readonly PdfName XObjectName = Name("XObject");
    private static readonly PdfName PropertiesName = Name("Properties");
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName QuadPointsName = Name("QuadPoints");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName FlagsName = Name("F");
    private static readonly PdfName AppearanceName = Name("AP");
    private static readonly PdfName NormalName = Name("N");
    private static readonly PdfName StateName = Name("AS");
    private static readonly PdfName OptionalContentName = Name("OC");
    private static readonly PdfName BoundingBoxName = Name("BBox");
    private static readonly PdfName MatrixName = Name("Matrix");
    private static readonly PdfName TypeName = Name("Type");
    private static readonly PdfName FormName = Name("Form");
    private static readonly PdfName ParentName = Name("Parent");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName[] DroppedBoxes = [Name("BleedBox"), Name("TrimBox"), Name("ArtBox")];

    /// <summary>Appends one revision that transforms the given pages; the rest are left as they are.</summary>
    public static byte[] Transform(PdfDocument document, IReadOnlyDictionary<int, PdfPageTransform> pages)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(pages);
        if (pages.Count == 0) return document.Source.ToArray();
        PdfPageTree tree = PdfPageTree.Read(document);
        foreach (var (index, transform) in pages)
        {
            if (index < 0 || index >= tree.Pages.Count) throw new ArgumentOutOfRangeException(nameof(pages));
            double determinant = transform.A * transform.D - transform.B * transform.C;
            if (!new[] { transform.A, transform.B, transform.C, transform.D, transform.E, transform.F }.All(double.IsFinite)
                || !double.IsFinite(determinant) || Math.Abs(determinant) < 1e-12
                || !double.IsFinite(transform.Width) || !double.IsFinite(transform.Height)
                || transform.Width <= 0 || transform.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(pages), "A page transform is not a usable geometry.");
        }

        var update = new PdfIncrementalUpdateBuilder(document);
        var destinations = new DestinationMapper(document, update, pages.ToDictionary(
            pair => tree.Pages[pair.Key].Reference.ObjectNumber, pair => pair.Value));
        var renderer = new PdfPageRenderer(document);
        var widgetReader = new PdfFormWidgetReader(document);
        var removedWidgets = new Dictionary<int, (PdfIndirectReference Reference, string Field)>();
        int resourceNumber = 1;
        foreach (var (pageIndex, transform) in pages.OrderBy(pair => pair.Key))
        {
            PdfPageTreeEntry page = tree.Pages[pageIndex];
            var widgetFields = widgetReader.Read(pageIndex)
                .Where(widget => widget.ObjectNumber > 0)
                .GroupBy(widget => widget.ObjectNumber)
                .ToDictionary(group => group.Key, group => group.First().FieldName);
            var entries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);

            PdfDictionary resources = page.InheritedValues.TryGetValue(ResourcesName, out PdfObject? resourceValue)
                ? Resolve(document, resourceValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A page /Resources value is not a dictionary.")
                : new PdfDictionary([]);
            var resourceEntries = resources.ToDictionary(item => item.Key, item => item.Value);
            var xObjects = resourceEntries.TryGetValue(XObjectName, out PdfObject? xObjectValue)
                ? (Resolve(document, xObjectValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A page /XObject resource is not a dictionary."))
                    .ToDictionary(item => item.Key, item => item.Value)
                : [];
            var properties = resourceEntries.TryGetValue(PropertiesName, out PdfObject? propertiesValue)
                ? (Resolve(document, propertiesValue) as PdfDictionary
                    ?? throw new InvalidOperationException("A page /Properties resource is not a dictionary."))
                    .ToDictionary(item => item.Key, item => item.Value)
                : [];

            var kept = new List<PdfObject>();
            var overlay = new StringBuilder();
            if (page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotsValue)
                && Resolve(document, annotsValue) is PdfArray annotations)
            {
                foreach (PdfObject item in annotations)
                {
                    if (Resolve(document, item) is not PdfDictionary annotation) continue;
                    PdfName? subtype = Resolve(document, annotation.TryGetValue(SubtypeName, out PdfObject? s) ? s : PdfNull.Instance) as PdfName;
                    string kind = subtype?.ValueAsLatin1() ?? "";
                    if (kind == "Link")
                    {
                        kept.Add(MoveLink(document, update, destinations, item, annotation, transform));
                        continue;
                    }
                    if (item is PdfIndirectReference widgetReference
                        && widgetFields.TryGetValue(widgetReference.ObjectNumber, out string? field))
                        removedWidgets[widgetReference.ObjectNumber] = (widgetReference, field);
                    if (kind == "Popup") continue;
                    int flags = annotation.TryGetValue(FlagsName, out PdfObject? flagValue)
                        && Resolve(document, flagValue) is PdfInteger integer ? (int)integer.Value : 0;
                    if ((flags & (HiddenFlag | NoViewFlag)) != 0) continue;

                    // The same appearance the page render drew: a field shows its regenerated value and legacy
                    // markup without /AP gets the appearance built for it in page space.
                    PdfObject? savedValue = SelectedAppearance(document, annotation);
                    PdfStream? saved = savedValue is null ? null : Resolve(document, savedValue) as PdfStream;
                    if (renderer.ShownAppearance(pageIndex, annotation, saved, out bool inPageSpace)
                        is not { } appearance) continue;
                    PdfObject appearanceValue = ReferenceEquals(appearance, saved) ? savedValue! : appearance;
                    string placement;
                    if (inPageSpace) placement = "";
                    else
                    {
                        if (!TryRect(document, annotation, out double[] rect)
                            || !TryAppearanceBounds(document, appearance, out double[] bounds)) continue;
                        double sourceWidth = bounds[2] - bounds[0], sourceHeight = bounds[3] - bounds[1];
                        double targetWidth = rect[2] - rect[0], targetHeight = rect[3] - rect[1];
                        if (sourceWidth <= 0 || sourceHeight <= 0 || targetWidth <= 0 || targetHeight <= 0) continue;
                        double scaleX = targetWidth / sourceWidth, scaleY = targetHeight / sourceHeight;
                        placement = Number(scaleX) + " 0 0 " + Number(scaleY) + " "
                            + Number(rect[0] - bounds[0] * scaleX) + " " + Number(rect[1] - bounds[1] * scaleY) + " cm ";
                    }

                    PdfName resource;
                    do resource = Name("KpfTf" + resourceNumber++);
                    while (xObjects.ContainsKey(resource));
                    xObjects[resource] = FormReference(update, appearanceValue, appearance);
                    bool layered = annotation.TryGetValue(OptionalContentName, out PdfObject? layer);
                    if (layered)
                    {
                        PdfName property;
                        do property = Name("KpfTfOc" + resourceNumber++);
                        while (properties.ContainsKey(property));
                        properties[property] = layer!;
                        overlay.Append("/OC /").Append(property.ValueAsLatin1()).Append(" BDC\n");
                    }
                    overlay.Append("q ").Append(placement).Append('/')
                        .Append(resource.ValueAsLatin1()).Append(" Do Q\n");
                    if (layered) overlay.Append("EMC\n");
                }
            }

            string matrix = string.Join(' ', new[] { transform.A, transform.B, transform.C, transform.D,
                transform.E, transform.F }.Select(Number));
            var originals = new List<PdfObject>();
            if (page.Dictionary.TryGetValue(ContentsName, out PdfObject? contentValue))
            {
                if (Resolve(document, contentValue) is PdfArray array)
                    originals.AddRange(array.Select(value => IndirectStream(update, document, value)));
                else
                    originals.Add(IndirectStream(update, document, contentValue));
            }
            // Content that restores more than it saves would pop the matrix inserted here, so the matrix is followed
            // by as many extra saves as the worst unmatched restore needs.
            string extraSaves = string.Concat(Enumerable.Repeat("q\n", UnmatchedRestores(document, originals)));
            var contents = new List<PdfObject> { update.AddObject(Stream($"q {matrix} cm\nq\n{extraSaves}")) };
            contents.AddRange(originals);
            string closing = overlay.Length == 0 ? "\nQ\nQ\n"
                : "\nQ\n/Artifact BMC\n" + overlay + "EMC\nQ\n";
            contents.Add(update.AddObject(Stream(closing)));
            entries[ContentsName] = new PdfArray(contents);

            if (overlay.Length > 0)
            {
                resourceEntries[XObjectName] = new PdfDictionary(xObjects);
                if (properties.Count > 0) resourceEntries[PropertiesName] = new PdfDictionary(properties);
                entries[ResourcesName] = new PdfDictionary(resourceEntries);
            }

            var box = new PdfArray([new PdfReal(0), new PdfReal(0),
                new PdfReal(transform.Width), new PdfReal(transform.Height)]);
            entries[Name("MediaBox")] = box;
            entries[Name("CropBox")] = box;
            foreach (PdfName name in DroppedBoxes) entries.Remove(name);
            entries[Name("Rotate")] = new PdfInteger(0);
            if (kept.Count > 0) entries[AnnotsName] = new PdfArray(kept);
            else entries.Remove(AnnotsName);

            update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
        }

        destinations.MapDocument(tree, pages);

        // A field whose widgets all sat on turned pages is gone, the way a rendered page dropped it. A field that
        // still has a widget on another page keeps working there and only loses the turned pages' widgets.
        var keptFields = new HashSet<string>(StringComparer.Ordinal);
        for (int pageIndex = 0; pageIndex < tree.Pages.Count; pageIndex++)
            if (!pages.ContainsKey(pageIndex))
                foreach (PdfFormWidgetInfo widget in widgetReader.Read(pageIndex))
                    if (!removedWidgets.ContainsKey(widget.ObjectNumber)) keptFields.Add(widget.FieldName);
        DetachWidgets(document, update, removedWidgets.Values
            .Where(widget => keptFields.Contains(widget.Field)).Select(widget => widget.Reference));
        string[] removedFields = [.. removedWidgets.Values.Select(widget => widget.Field)
            .Where(field => !keptFields.Contains(field)).Distinct(StringComparer.Ordinal)];

        byte[] output = update.Build();
        if (removedFields.Length == 0) return output;
        var editor = new PdfIncrementalPageEditor(PdfDocument.Open(output));
        foreach (string field in removedFields) editor.RemoveFormField(field);
        return editor.Build();
    }

    // The lowest depth the page's own q and Q operators reach below zero, read over all its streams as one, which is
    // how many unmatched restores the content has. Content that cannot be read counts as balanced.
    private static int UnmatchedRestores(PdfDocument document, IReadOnlyList<PdfObject> streams)
    {
        try
        {
            var joined = new List<byte>();
            foreach (PdfObject value in streams)
            {
                if (Resolve(document, value) is not PdfStream stream) continue;
                joined.AddRange(document.DecodeStream(stream));
                joined.Add((byte)'\n');
            }
            int depth = 0, lowest = 0;
            foreach (var instruction in PdfContentStreamReader.Read(joined.ToArray(), compatibilityRecovery: true))
            {
                if (instruction.Operator == "q") depth++;
                else if (instruction.Operator == "Q") lowest = Math.Min(lowest, --depth);
            }
            return -lowest;
        }
        catch (Exception exception) when (PdfReaderDamage.Is(exception))
        {
            return 0;
        }
    }

    // Takes widgets out of their parent field's /Kids. A widget with no parent is its own field, which only
    // happens when that field has a single widget, so it never reaches here with another widget kept.
    private static void DetachWidgets(PdfDocument document, PdfIncrementalUpdateBuilder update,
        IEnumerable<PdfIndirectReference> widgets)
    {
        var byParent = new Dictionary<int, (PdfIndirectReference Parent, HashSet<int> Widgets)>();
        foreach (PdfIndirectReference widget in widgets)
        {
            if (Resolve(document, widget) is not PdfDictionary dictionary
                || !dictionary.TryGetValue(ParentName, out PdfObject? parentValue)
                || parentValue is not PdfIndirectReference parentReference) continue;
            if (!byParent.TryGetValue(parentReference.ObjectNumber, out var entry))
                byParent[parentReference.ObjectNumber] = entry = (parentReference, []);
            entry.Widgets.Add(widget.ObjectNumber);
        }
        foreach (var (parentNumber, (parentReference, detached)) in byParent)
        {
            if (Resolve(document, parentReference) is not PdfDictionary parent
                || !parent.TryGetValue(KidsName, out PdfObject? kidsValue)
                || Resolve(document, kidsValue) is not PdfArray kids) continue;
            var remaining = new PdfArray([.. kids.Where(kid =>
                kid is not PdfIndirectReference reference || !detached.Contains(reference.ObjectNumber))]);
            if (kidsValue is PdfIndirectReference kidsReference)
                update.ReplaceObject(kidsReference.ObjectNumber, remaining);
            else
                update.ReplaceObject(parentNumber, new PdfDictionary(parent
                    .Where(item => !item.Key.Equals(KidsName))
                    .Append(new KeyValuePair<PdfName, PdfObject>(KidsName, remaining))));
        }
    }

    // A link keeps working: its rectangle becomes the box around its turned corners, its quadrilaterals turn point
    // by point.
    private static PdfObject MoveLink(PdfDocument document, PdfIncrementalUpdateBuilder update,
        DestinationMapper destinations,
        PdfObject original, PdfDictionary link, PdfPageTransform transform)
    {
        PdfDictionary moved = destinations.MapHolder(link) ?? link;
        var entries = moved.ToDictionary(item => item.Key, item => item.Value);
        if (TryRect(document, link, out double[] rect))
        {
            (double X, double Y)[] corners = [transform.Apply(rect[0], rect[1]), transform.Apply(rect[2], rect[1]),
                transform.Apply(rect[0], rect[3]), transform.Apply(rect[2], rect[3])];
            entries[RectName] = new PdfArray([new PdfReal(corners.Min(p => p.X)), new PdfReal(corners.Min(p => p.Y)),
                new PdfReal(corners.Max(p => p.X)), new PdfReal(corners.Max(p => p.Y))]);
        }
        if (link.TryGetValue(QuadPointsName, out PdfObject? quadValue)
            && Resolve(document, quadValue) is PdfArray quads && quads.Count % 2 == 0
            && quads.All(value => TryNumber(document, value, out _)))
        {
            var points = new List<PdfObject>(quads.Count);
            for (int index = 0; index < quads.Count; index += 2)
            {
                TryNumber(document, quads[index], out double x);
                TryNumber(document, quads[index + 1], out double y);
                var (X, Y) = transform.Apply(x, y);
                points.Add(new PdfReal(X));
                points.Add(new PdfReal(Y));
            }
            entries[QuadPointsName] = new PdfArray(points);
        }
        var replacement = new PdfDictionary(entries);
        if (original is PdfIndirectReference reference)
        {
            update.ReplaceObject(reference.ObjectNumber, replacement);
            return original;
        }
        return replacement;
    }

    private static PdfObject? SelectedAppearance(PdfDocument document, PdfDictionary annotation)
    {
        if (!annotation.TryGetValue(AppearanceName, out PdfObject? value)
            || Resolve(document, value) is not PdfDictionary appearance
            || !appearance.TryGetValue(NormalName, out PdfObject? normal)) return null;
        PdfObject resolved = Resolve(document, normal);
        if (resolved is PdfStream) return normal;
        if (resolved is not PdfDictionary states) return null;
        // Like the page render, a state dictionary with no /AS selects nothing.
        return annotation.TryGetValue(StateName, out PdfObject? stateValue)
            && Resolve(document, stateValue) is PdfName state
            && states.TryGetValue(state, out PdfObject? selected) ? selected : null;
    }

    // The appearance must be a form XObject to be drawn with Do; one stored directly or without its subtype gets a
    // complete copy.
    private static PdfObject FormReference(PdfIncrementalUpdateBuilder update, PdfObject value, PdfStream appearance)
    {
        bool isForm = appearance.Dictionary.TryGetValue(SubtypeName, out PdfObject? subtype)
            && subtype is PdfName name && name.Equals(FormName);
        if (value is PdfIndirectReference && isForm) return value;
        var dictionary = appearance.Dictionary.ToDictionary(item => item.Key, item => item.Value);
        dictionary[TypeName] = XObjectName;
        dictionary[SubtypeName] = FormName;
        return update.AddObject(new PdfStream(new PdfDictionary(dictionary), appearance.EncodedData.Span));
    }

    // The box the appearance covers once its own matrix is applied, which the annotation rectangle receives.
    private static bool TryAppearanceBounds(PdfDocument document, PdfStream appearance, out double[] bounds)
    {
        bounds = [];
        if (!appearance.Dictionary.TryGetValue(BoundingBoxName, out PdfObject? boxValue)
            || !TryNumbers(document, boxValue, 4, out double[] box)) return false;
        double[] matrix = appearance.Dictionary.TryGetValue(MatrixName, out PdfObject? matrixValue)
            && TryNumbers(document, matrixValue, 6, out double[] values) ? values : [1, 0, 0, 1, 0, 0];
        (double X, double Y)[] corners = [.. new[] { (box[0], box[1]), (box[0], box[3]), (box[2], box[1]), (box[2], box[3]) }
            .Select(point => (matrix[0] * point.Item1 + matrix[2] * point.Item2 + matrix[4],
                matrix[1] * point.Item1 + matrix[3] * point.Item2 + matrix[5]))];
        bounds = [corners.Min(p => p.X), corners.Min(p => p.Y), corners.Max(p => p.X), corners.Max(p => p.Y)];
        return true;
    }

    private static bool TryRect(PdfDocument document, PdfDictionary annotation, out double[] rect)
    {
        rect = [];
        if (!annotation.TryGetValue(RectName, out PdfObject? value) || !TryNumbers(document, value, 4, out double[] raw))
            return false;
        rect = [Math.Min(raw[0], raw[2]), Math.Min(raw[1], raw[3]), Math.Max(raw[0], raw[2]), Math.Max(raw[1], raw[3])];
        return true;
    }

    private static bool TryNumbers(PdfDocument document, PdfObject value, int count, out double[] numbers)
    {
        numbers = [];
        if (Resolve(document, value) is not PdfArray array || array.Count != count) return false;
        var result = new double[count];
        for (int index = 0; index < count; index++)
            if (!TryNumber(document, array[index], out result[index])) return false;
        numbers = result;
        return true;
    }

    private static bool TryNumber(PdfDocument document, PdfObject value, out double number)
    {
        switch (Resolve(document, value))
        {
            case PdfInteger integer: number = integer.Value; return true;
            case PdfReal real when double.IsFinite(real.Value): number = real.Value; return true;
            default: number = 0; return false;
        }
    }

    private static PdfObject IndirectStream(PdfIncrementalUpdateBuilder update, PdfDocument document, PdfObject value)
    {
        if (value is PdfIndirectReference) return value;
        return Resolve(document, value) is PdfStream stream ? update.AddObject(stream)
            : throw new InvalidOperationException("A page /Contents value is not a stream or stream array.");
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A PDF reference chain contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfStream Stream(string content) => new(new PdfDictionary([]), Encoding.ASCII.GetBytes(content));

    private static string Number(double value)
    {
        if (Math.Abs(value) < 1e-12) return "0";
        string text = value.ToString("0.##########", CultureInfo.InvariantCulture);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double back)
            && Math.Abs(back - value) <= Math.Abs(value) * 1e-12
            ? text : Encoding.ASCII.GetString(PdfObjectWriter.Write(new PdfReal(value)));
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
