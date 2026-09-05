using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

internal sealed class PdfPageTree
{
    private static readonly PdfName RootName = Name("Root");
    private static readonly PdfName PagesName = Name("Pages");
    private static readonly PdfName KidsName = Name("Kids");
    private static readonly PdfName CountName = Name("Count");
    private static readonly PdfName ParentName = Name("Parent");
    private static readonly PdfName TypeName = Name("Type");
    private static readonly PdfName CatalogName = Name("Catalog");
    private static readonly PdfName PageName = Name("Page");
    private static readonly PdfName[] InheritableNames =
    [
        Name("Resources"), Name("MediaBox"), Name("CropBox"), Name("Rotate")
    ];
    private const int MaximumDepth = 256;
    internal const int MaximumPageCount = 1_000_000;

    private PdfPageTree(
        PdfIndirectReference catalogReference, PdfDictionary catalog,
        PdfIndirectReference rootReference, IReadOnlyList<PdfPageTreeEntry> pages)
    {
        CatalogReference = catalogReference;
        Catalog = catalog;
        RootReference = rootReference;
        Pages = pages;
    }

    internal PdfIndirectReference CatalogReference { get; }
    internal PdfDictionary Catalog { get; }
    internal PdfIndirectReference RootReference { get; }
    internal IReadOnlyList<PdfPageTreeEntry> Pages { get; }

    internal static PdfPageTree Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfIndirectReference catalogReference = document.CrossReferences.TryGetTrailerValue(
            RootName, out PdfObject rootValue)
            ? rootValue as PdfIndirectReference
                ?? throw new InvalidOperationException("The trailer /Root is not an indirect reference.")
            : throw new InvalidOperationException("The PDF trailer has no /Root.");
        (PdfObject resolvedCatalog, PdfIndirectReference resolvedCatalogReference) =
            ResolveReference(catalogReference, "The document catalog");
        PdfDictionary catalog = resolvedCatalog as PdfDictionary
            ?? throw new InvalidOperationException("The document catalog is not a dictionary.");
        catalogReference = resolvedCatalogReference;
        if (!catalog.TryGetValue(TypeName, out PdfObject? catalogTypeValue)
            || Resolve(catalogTypeValue) is not PdfName catalogType
            || !catalogType.Equals(CatalogName))
        {
            if (!document.UsesCompatibilityRecovery || catalog.ContainsKey(TypeName))
                throw new InvalidOperationException(
                    "The trailer /Root dictionary does not declare /Type /Catalog.");
        }
        PdfIndirectReference rootReference = catalog.TryGetValue(PagesName, out PdfObject pagesValue)
            ? pagesValue as PdfIndirectReference
                ?? throw new InvalidOperationException("The catalog /Pages is not an indirect reference.")
            : throw new InvalidOperationException("The document catalog has no /Pages tree.");
        (PdfObject resolvedRoot, PdfIndirectReference resolvedRootReference) =
            ResolveReference(rootReference, "The catalog /Pages tree");
        if (resolvedRoot is not PdfDictionary)
            throw new InvalidOperationException("The catalog /Pages is not a page-tree dictionary.");
        rootReference = resolvedRootReference;

        var pages = new List<PdfPageTreeEntry>();
        var active = new HashSet<(int ObjectNumber, int Generation)>();
        var visitedNodes = new HashSet<(int ObjectNumber, int Generation)>();
        Visit(rootReference, null, 0, new Dictionary<PdfName, PdfObject>());
        return new PdfPageTree(catalogReference, catalog, rootReference, pages);

        int Visit(
            PdfIndirectReference reference, PdfIndirectReference? expectedParent, int depth,
            IReadOnlyDictionary<PdfName, PdfObject> inherited)
        {
            if (depth >= MaximumDepth)
                throw new InvalidOperationException("The page tree exceeds the supported nesting depth.");
            (PdfObject resolvedNode, PdfIndirectReference resolvedReference) =
                ResolveReference(reference, "A page-tree node");
            reference = resolvedReference;
            var key = (reference.ObjectNumber, reference.Generation);
            if (!active.Add(key))
                throw new InvalidOperationException("The page tree contains a cycle.");
            if (!visitedNodes.Add(key))
            {
                active.Remove(key);
                throw new InvalidOperationException(
                    "The page tree references the same node more than once.");
            }
            try
            {
                PdfDictionary node = resolvedNode as PdfDictionary
                    ?? throw new InvalidOperationException("A page-tree reference is not a dictionary.");
                if (expectedParent is null)
                {
                    if (node.ContainsKey(ParentName) && !document.UsesCompatibilityRecovery)
                        throw new InvalidOperationException(
                            "The root page-tree node contains a /Parent entry.");
                }
                else
                {
                    PdfIndirectReference? parent = node.TryGetValue(
                        ParentName, out PdfObject? parentValue)
                        ? parentValue as PdfIndirectReference
                        : null;
                    if (parent is null)
                    {
                        if (!document.UsesCompatibilityRecovery)
                            throw new InvalidOperationException(node.ContainsKey(ParentName)
                                ? "A non-root page-tree node /Parent is not an indirect reference."
                                : "A non-root page-tree node has no /Parent reference.");
                    }
                    else
                    {
                        (_, parent) = ResolveReference(
                            parent, "A page-tree node /Parent value");
                        if ((parent.ObjectNumber != expectedParent.ObjectNumber
                                || parent.Generation != expectedParent.Generation)
                            && !document.UsesCompatibilityRecovery)
                            throw new InvalidOperationException(
                                "A page-tree node /Parent does not identify the node that contains it.");
                    }
                }
                var effective = new Dictionary<PdfName, PdfObject>(inherited);
                foreach (PdfName name in InheritableNames)
                    if (node.TryGetValue(name, out PdfObject value)) effective[name] = value;
                PdfObject type = node.TryGetValue(TypeName, out PdfObject? typeValue)
                    ? Resolve(typeValue)
                    : throw new InvalidOperationException("A page-tree node has no /Type value.");
                if (type is PdfName typeName && typeName.Equals(PageName))
                {
                    if (node.ContainsKey(KidsName) || node.ContainsKey(CountName))
                        throw new InvalidOperationException(
                            "A /Type /Page leaf contains page-tree /Kids or /Count entries.");
                    if (pages.Count >= MaximumPageCount)
                        throw new InvalidOperationException("The PDF contains too many pages.");
                    pages.Add(new PdfPageTreeEntry(pages.Count, reference, node, effective));
                    return 1;
                }
                if (type is not PdfName pagesType || !pagesType.Equals(PagesName))
                    throw new InvalidOperationException(
                        "A page-tree node /Type value is neither /Page nor /Pages.");
                PdfArray kids = node.TryGetValue(KidsName, out PdfObject kidsValue)
                    ? Resolve(kidsValue) as PdfArray
                        ?? throw new InvalidOperationException("A page-tree /Kids value is not an array.")
                    : throw new InvalidOperationException("A page-tree node has neither /Type /Page nor /Kids.");
                if (depth > 0 && kids.Count == 0)
                    throw new InvalidOperationException("A non-root page-tree /Kids array is empty.");
                PdfInteger count = node.TryGetValue(CountName, out PdfObject? countValue)
                    ? Resolve(countValue) as PdfInteger
                        ?? throw new InvalidOperationException("A page-tree /Count value is not an integer.")
                    : throw new InvalidOperationException("A /Type /Pages node has no /Count value.");
                if (count.Value < 0)
                    throw new InvalidOperationException("A page-tree /Count value is negative.");
                int actualCount = 0;
                foreach (PdfObject kid in kids)
                    actualCount = checked(actualCount + Visit(kid as PdfIndirectReference
                        ?? throw new InvalidOperationException("A page-tree kid is not an indirect reference."),
                        reference, depth + 1, effective));
                if (count.Value != actualCount)
                    throw new InvalidOperationException(
                        "A page-tree /Count value does not match its descendant page count.");
                return actualCount;
            }
            finally
            {
                active.Remove(key);
            }
        }

        PdfObject Resolve(PdfObject value)
        {
            var visitedReferences = new HashSet<(int ObjectNumber, int Generation)>();
            for (int depth = 0; value is PdfIndirectReference reference; depth++)
            {
                if (depth >= 32)
                    throw new InvalidOperationException(
                        "A page-tree structural value is too deeply indirect.");
                if (!visitedReferences.Add((reference.ObjectNumber, reference.Generation)))
                    throw new InvalidOperationException(
                        "A page-tree structural value contains an indirect-reference cycle.");
                value = document.Resolve(reference);
            }
            return value;
        }

        (PdfObject Value, PdfIndirectReference Reference) ResolveReference(
            PdfIndirectReference reference, string description)
        {
            PdfObject value = reference;
            PdfIndirectReference finalReference = reference;
            var visitedReferences = new HashSet<(int ObjectNumber, int Generation)>();
            for (int depth = 0; value is PdfIndirectReference current; depth++)
            {
                if (depth >= 32)
                    throw new InvalidOperationException(
                        $"{description} is too deeply indirect.");
                if (!visitedReferences.Add((current.ObjectNumber, current.Generation)))
                    throw new InvalidOperationException(
                        $"{description} contains an indirect-reference cycle.");
                finalReference = current;
                value = document.Resolve(current);
            }
            return (value, finalReference);
        }
    }

    internal static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}

internal sealed record PdfPageTreeEntry(
    int Index, PdfIndirectReference Reference, PdfDictionary Dictionary,
    IReadOnlyDictionary<PdfName, PdfObject> InheritedValues);
