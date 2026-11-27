using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

public static partial class PdfPageTransformer
{
    /// <summary>
    /// Moves explicit destinations that point into a transformed page into the page's new coordinate system. It
    /// reaches the outline, the catalog /Dests and /Names /Dests stores, the open action and every page's links.
    /// A destination stored once is changed once, wherever it is reached from.
    /// </summary>
    private sealed class DestinationMapper(PdfDocument document, PdfIncrementalUpdateBuilder update,
        IReadOnlyDictionary<int, PdfPageTransform> transforms)
    {
        private static readonly PdfName DestName = Name("Dest");
        private static readonly PdfName ActionName = Name("A");
        private static readonly PdfName ActionTargetName = Name("D");
        private static readonly PdfName ActionTypeName = Name("S");
        private static readonly PdfName OutlinesName = Name("Outlines");
        private static readonly PdfName FirstName = Name("First");
        private static readonly PdfName NextName = Name("Next");
        private static readonly PdfName NamesName = Name("Names");
        private static readonly PdfName DestsName = Name("Dests");
        private static readonly PdfName OpenActionName = Name("OpenAction");
        private static readonly PdfName NamesArrayName = Name("Names");
        private const int MaximumOutlineDepth = 256;
        private const int MaximumOutlineItems = 1_000_000;

        private readonly HashSet<int> _replaced = [];
        private readonly HashSet<int> _outlineItems = [];

        /// <summary>Maps the destinations stored in the catalog and on the pages that are not transformed.</summary>
        internal void MapDocument(PdfPageTree tree, IReadOnlyDictionary<int, PdfPageTransform> transformedPages)
        {
            if (transforms.Count == 0) return;
            var catalog = tree.Catalog.ToDictionary(item => item.Key, item => item.Value);
            bool catalogChanged = false;
            Guarded(() =>
            {
                if (catalog.TryGetValue(OutlinesName, out PdfObject? outlines)
                    && Resolve(document, outlines) is PdfDictionary root && root.TryGetValue(FirstName, out PdfObject? first))
                    WalkOutline(first, 0);
            });
            Guarded(() =>
            {
                if (catalog.TryGetValue(DestsName, out PdfObject? dests)
                    && MapObject<PdfDictionary>(dests, MapDestsDictionary) is { } direct)
                {
                    catalog[DestsName] = direct;
                    catalogChanged = true;
                }
            });
            Guarded(() =>
            {
                if (catalog.TryGetValue(NamesName, out PdfObject? names)
                    && MapObject<PdfDictionary>(names, MapNamesDictionary) is { } direct)
                {
                    catalog[NamesName] = direct;
                    catalogChanged = true;
                }
            });
            Guarded(() =>
            {
                if (!catalog.TryGetValue(OpenActionName, out PdfObject? open)) return;
                PdfObject? changed = Resolve(document, open) is PdfArray
                    ? MapObject<PdfArray>(open, MapDestination)
                    : MapObject<PdfDictionary>(open, MapAction);
                if (changed is null) return;
                catalog[OpenActionName] = changed;
                catalogChanged = true;
            });
            if (catalogChanged)
                update.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog));

            for (int index = 0; index < tree.Pages.Count; index++)
            {
                if (transformedPages.ContainsKey(index)) continue;
                PdfPageTreeEntry page = tree.Pages[index];
                Guarded(() =>
                {
                    if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annots)
                        || MapObject<PdfArray>(annots, MapLinkArray) is not { } direct) return;
                    var entries = page.Dictionary.ToDictionary(item => item.Key, item => item.Value);
                    entries[AnnotsName] = direct;
                    update.ReplaceObject(page.Reference.ObjectNumber, new PdfDictionary(entries));
                });
            }
        }

        /// <summary>Gives the link with its explicit destinations moved, or null when none changes.</summary>
        internal PdfDictionary? MapHolder(PdfDictionary holder)
        {
            Dictionary<PdfName, PdfObject>? entries = null;
            if (holder.TryGetValue(DestName, out PdfObject? destination)
                && MapObject<PdfArray>(destination, MapDestination) is { } mapped)
            {
                entries = holder.ToDictionary(item => item.Key, item => item.Value);
                entries[DestName] = mapped;
            }
            if (holder.TryGetValue(ActionName, out PdfObject? action)
                && MapObject<PdfDictionary>(action, MapAction) is { } mappedAction)
            {
                entries ??= holder.ToDictionary(item => item.Key, item => item.Value);
                entries[ActionName] = mappedAction;
            }
            return entries is null ? null : new PdfDictionary(entries);
        }

        private static void Guarded(Action work)
        {
            // A damaged store costs only its own destinations; the transform itself still goes ahead.
            try { work(); }
            catch (Exception error) when (PdfReaderDamage.Is(error)) { }
        }

        // Applies a mapping to a value that is either direct or an indirect object. A direct value comes back as its
        // replacement. An indirect object is replaced where it is stored, once, and the reference stays as it was.
        private PdfObject? MapObject<T>(PdfObject value, Func<T, T?> map) where T : PdfObject
        {
            if (value is PdfIndirectReference reference)
            {
                if (Resolve(document, reference) is not T resolved || !_replaced.Add(reference.ObjectNumber)) return null;
                if (map(resolved) is { } replacement) update.ReplaceObject(reference.ObjectNumber, replacement);
                return null;
            }
            return value is T direct ? map(direct) : null;
        }

        private void WalkOutline(PdfObject first, int depth)
        {
            PdfObject? current = first;
            while (current is PdfIndirectReference reference && _outlineItems.Count < MaximumOutlineItems
                && _outlineItems.Add(reference.ObjectNumber))
            {
                if (Resolve(document, reference) is not PdfDictionary item) return;
                if (MapHolder(item) is { } replacement) update.ReplaceObject(reference.ObjectNumber, replacement);
                if (depth < MaximumOutlineDepth && item.TryGetValue(FirstName, out PdfObject? child))
                    WalkOutline(child, depth + 1);
                current = item.TryGetValue(NextName, out PdfObject? next) ? next : null;
            }
        }

        private PdfDictionary? MapAction(PdfDictionary action)
        {
            if (!action.TryGetValue(ActionTypeName, out PdfObject? type)
                || Resolve(document, type) is not PdfName name || name.ValueAsLatin1() != "GoTo") return null;
            return MapTarget(action);
        }

        // A dictionary with a /D destination: a GoTo action, or a value in the /Dests dictionary.
        private PdfDictionary? MapTarget(PdfDictionary holder)
        {
            if (!holder.TryGetValue(ActionTargetName, out PdfObject? target)
                || MapObject<PdfArray>(target, MapDestination) is not { } mapped) return null;
            var entries = holder.ToDictionary(item => item.Key, item => item.Value);
            entries[ActionTargetName] = mapped;
            return new PdfDictionary(entries);
        }

        // A stored destination is an array, or a dictionary holding one under /D. A name or string is a lookup
        // that is fixed where the name is defined.
        private PdfObject? MapStoredDestination(PdfObject value) =>
            Resolve(document, value) switch
            {
                PdfArray => MapObject<PdfArray>(value, MapDestination),
                PdfDictionary => MapObject<PdfDictionary>(value, MapTarget),
                _ => null
            };

        private PdfDictionary? MapDestsDictionary(PdfDictionary dests)
        {
            Dictionary<PdfName, PdfObject>? entries = null;
            foreach (var (key, value) in dests)
                if (MapStoredDestination(value) is { } mapped)
                {
                    entries ??= dests.ToDictionary(item => item.Key, item => item.Value);
                    entries[key] = mapped;
                }
            return entries is null ? null : new PdfDictionary(entries);
        }

        private PdfDictionary? MapNamesDictionary(PdfDictionary names)
        {
            if (!names.TryGetValue(DestsName, out PdfObject? tree)
                || MapObject<PdfDictionary>(tree, MapNameTreeNode) is not { } mapped) return null;
            var entries = names.ToDictionary(item => item.Key, item => item.Value);
            entries[DestsName] = mapped;
            return new PdfDictionary(entries);
        }

        private PdfDictionary? MapNameTreeNode(PdfDictionary node)
        {
            Dictionary<PdfName, PdfObject>? entries = null;
            if (node.TryGetValue(NamesArrayName, out PdfObject? pairs)
                && MapObject<PdfArray>(pairs, MapNamePairs) is { } mappedPairs)
            {
                entries = node.ToDictionary(item => item.Key, item => item.Value);
                entries[NamesArrayName] = mappedPairs;
            }
            if (node.TryGetValue(KidsName, out PdfObject? kids)
                && MapObject<PdfArray>(kids, MapKids) is { } mappedKids)
            {
                entries ??= node.ToDictionary(item => item.Key, item => item.Value);
                entries[KidsName] = mappedKids;
            }
            return entries is null ? null : new PdfDictionary(entries);
        }

        private PdfArray? MapNamePairs(PdfArray pairs)
        {
            List<PdfObject>? items = null;
            for (int index = 1; index < pairs.Count; index += 2)
                if (MapStoredDestination(pairs[index]) is { } mapped)
                {
                    items ??= [.. pairs];
                    items[index] = mapped;
                }
            return items is null ? null : new PdfArray(items);
        }

        private PdfArray? MapKids(PdfArray kids)
        {
            List<PdfObject>? items = null;
            for (int index = 0; index < kids.Count; index++)
                if (MapObject<PdfDictionary>(kids[index], MapNameTreeNode) is { } mapped)
                {
                    items ??= [.. kids];
                    items[index] = mapped;
                }
            return items is null ? null : new PdfArray(items);
        }

        private PdfArray? MapLinkArray(PdfArray annotations)
        {
            List<PdfObject>? items = null;
            for (int index = 0; index < annotations.Count; index++)
                if (MapObject<PdfDictionary>(annotations[index], MapLink) is { } mapped)
                {
                    items ??= [.. annotations];
                    items[index] = mapped;
                }
            return items is null ? null : new PdfArray(items);
        }

        private PdfDictionary? MapLink(PdfDictionary annotation) =>
            annotation.TryGetValue(SubtypeName, out PdfObject? subtype)
            && Resolve(document, subtype) is PdfName name && name.ValueAsLatin1() == "Link"
                ? MapHolder(annotation) : null;

        // The destination array [page /Kind ...] with its coordinates moved when it points at a transformed page.
        private PdfArray? MapDestination(PdfArray destination)
        {
            if (destination.Count < 2 || destination[0] is not PdfIndirectReference page
                || !transforms.TryGetValue(page.ObjectNumber, out PdfPageTransform transform)
                || Resolve(document, destination[1]) is not PdfName kind) return null;
            double scale = Math.Max(Math.Max(Math.Abs(transform.A), Math.Abs(transform.B)),
                Math.Max(Math.Abs(transform.C), Math.Abs(transform.D)));
            bool upright = Math.Abs(transform.B) <= 1e-9 * scale && Math.Abs(transform.C) <= 1e-9 * scale;
            bool turned = Math.Abs(transform.A) <= 1e-9 * scale && Math.Abs(transform.D) <= 1e-9 * scale;

            switch (kind.ValueAsLatin1())
            {
                case "XYZ":
                {
                    if (!TryOptional(destination, 2, out double? left) || !TryOptional(destination, 3, out double? top))
                        return null;
                    if (!left.HasValue && !top.HasValue) return null;
                    double? newLeft = null, newTop = null;
                    if (left.HasValue && top.HasValue)
                    {
                        var (x, y) = transform.Apply(left.Value, top.Value);
                        newLeft = x;
                        newTop = y;
                    }
                    else if (upright)
                    {
                        newLeft = left.HasValue ? transform.A * left.Value + transform.E : null;
                        newTop = top.HasValue ? transform.D * top.Value + transform.F : null;
                    }
                    else if (turned)
                    {
                        // A quarter turn swaps the axes, so the old left is the new vertical position.
                        newLeft = top.HasValue ? transform.C * top.Value + transform.E : null;
                        newTop = left.HasValue ? transform.B * left.Value + transform.F : null;
                    }
                    // At any other angle a lone coordinate has no single counterpart, so the view goes to the page.
                    return new PdfArray([page, kind, Number(newLeft), Number(newTop),
                        destination.Count > 4 ? destination[4] : PdfNull.Instance]);
                }
                case "FitR":
                {
                    double[] box = new double[4];
                    if (destination.Count < 6) return null;
                    for (int index = 0; index < 4; index++)
                        if (!TryNumber(document, destination[index + 2], out box[index])) return null;
                    (double X, double Y)[] corners = [transform.Apply(box[0], box[1]), transform.Apply(box[2], box[1]),
                        transform.Apply(box[0], box[3]), transform.Apply(box[2], box[3])];
                    return new PdfArray([page, kind, new PdfReal(corners.Min(p => p.X)), new PdfReal(corners.Min(p => p.Y)),
                        new PdfReal(corners.Max(p => p.X)), new PdfReal(corners.Max(p => p.Y))]);
                }
                case "FitH" or "FitBH":
                {
                    bool bounding = kind.ValueAsLatin1() == "FitBH";
                    if (!TryOptional(destination, 2, out double? top)) return null;
                    if (upright)
                        return top.HasValue
                            ? new PdfArray([page, kind, new PdfReal(transform.D * top.Value + transform.F)]) : null;
                    if (turned)
                        return new PdfArray([page, Name(bounding ? "FitBV" : "FitV"),
                            Number(top.HasValue ? transform.C * top.Value + transform.E : null)]);
                    return new PdfArray([page, Name(bounding ? "FitB" : "Fit")]);
                }
                case "FitV" or "FitBV":
                {
                    bool bounding = kind.ValueAsLatin1() == "FitBV";
                    if (!TryOptional(destination, 2, out double? left)) return null;
                    if (upright)
                        return left.HasValue
                            ? new PdfArray([page, kind, new PdfReal(transform.A * left.Value + transform.E)]) : null;
                    if (turned)
                        return new PdfArray([page, Name(bounding ? "FitBH" : "FitH"),
                            Number(left.HasValue ? transform.B * left.Value + transform.F : null)]);
                    return new PdfArray([page, Name(bounding ? "FitB" : "Fit")]);
                }
                default:
                    return null;
            }
        }

        private static PdfObject Number(double? value) => value.HasValue ? new PdfReal(value.Value) : PdfNull.Instance;

        // A missing or null coordinate is allowed; anything else that is not a number makes the destination unusable.
        private bool TryOptional(PdfArray array, int index, out double? value)
        {
            value = null;
            if (index >= array.Count || Resolve(document, array[index]) is PdfNull) return true;
            if (!TryNumber(document, array[index], out double number)) return false;
            value = number;
            return true;
        }
    }
}
