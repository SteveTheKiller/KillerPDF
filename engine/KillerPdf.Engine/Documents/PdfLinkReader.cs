using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Documents;

/// <summary>Reads native link annotations and resolves their local targets.</summary>
public static class PdfLinkReader
{
    private static readonly PdfName AnnotsName = Name("Annots");
    private static readonly PdfName SubtypeName = Name("Subtype");
    private static readonly PdfName LinkName = Name("Link");
    private static readonly PdfName RectName = Name("Rect");
    private static readonly PdfName ActionName = Name("A");
    private static readonly PdfName ActionTypeName = Name("S");
    private static readonly PdfName GoToName = Name("GoTo");
    private static readonly PdfName UriActionName = Name("URI");
    private static readonly PdfName DestinationName = Name("Dest");
    private static readonly PdfName DName = Name("D");
    private static readonly PdfName UriName = Name("URI");
    private static readonly PdfName ContentsName = Name("Contents");
    private static readonly PdfName NamesName = Name("Names");
    private static readonly PdfName DestsName = Name("Dests");

    /// <summary>Reads the native link annotations on one zero-based page.</summary>
    public static IReadOnlyList<PdfLinkInfo> ReadPage(PdfDocument document, int pageIndex)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (pageIndex < 0 || pageIndex >= tree.Pages.Count)
            throw new ArgumentOutOfRangeException(nameof(pageIndex));
        PdfPageTreeEntry page = tree.Pages[pageIndex];
        if (!page.Dictionary.TryGetValue(AnnotsName, out PdfObject? annotationsValue)) return [];
        PdfArray annotations = Resolve(document, annotationsValue, "A page /Annots value") as PdfArray
            ?? throw new InvalidOperationException("A page /Annots value is not an array.");
        var pages = tree.Pages.ToDictionary(
            item => (item.Reference.ObjectNumber, item.Reference.Generation), item => item.Index);
        Dictionary<string, PdfObject> named = ReadNamedDestinations(document, tree.Catalog);
        var result = new List<PdfLinkInfo>();
        for (int index = 0; index < annotations.Count; index++)
        {
            PdfObject annotationValue = annotations[index];
            PdfObject resolvedAnnotation = Resolve(document, annotationValue, "A page annotation");
            if (resolvedAnnotation is not PdfDictionary annotation
                || !annotation.TryGetValue(SubtypeName, out PdfObject? subtypeValue)
                || Resolve(document, subtypeValue, "An annotation subtype") is not PdfName subtype
                || !subtype.Equals(LinkName)) continue;
            if (!annotation.TryGetValue(RectName, out PdfObject? rectangleValue)
                || Resolve(document, rectangleValue, "A link rectangle") is not PdfArray rectangle
                || rectangle.Count != 4)
                throw new InvalidOperationException("A link annotation has no valid /Rect array.");
            double x1 = Number(document, rectangle[0]);
            double y1 = Number(document, rectangle[1]);
            double x2 = Number(document, rectangle[2]);
            double y2 = Number(document, rectangle[3]);
            int? destinationPage = null;
            string? namedDestination = null;
            string? uri = null;
            PdfObject? destination = null;
            if (annotation.TryGetValue(ActionName, out PdfObject? actionValue)
                && Resolve(document, actionValue, "A link action") is PdfDictionary action
                && action.TryGetValue(ActionTypeName, out PdfObject? actionTypeValue))
            {
                PdfName actionType = Resolve(document, actionTypeValue, "A link action type") as PdfName
                    ?? throw new InvalidOperationException("A link action type is not a name.");
                if (actionType.Equals(GoToName) && action.TryGetValue(DName, out PdfObject? actionDestination))
                    destination = actionDestination;
                else if (actionType.Equals(UriActionName) && action.TryGetValue(UriName, out PdfObject? uriValue))
                    uri = Resolve(document, uriValue, "A link URI") is PdfString uriString
                        ? PdfUnicodeEncoding.DecodeTextString(uriString.Bytes.Span, "A link URI")
                        : throw new InvalidOperationException("A link /URI value is not a string.");
            }
            else if (annotation.TryGetValue(DestinationName, out PdfObject? directDestination))
                destination = directDestination;
            if (destination is not null)
                (destinationPage, namedDestination) = ResolveDestination(
                    document, destination, pages, named);
            if (destinationPage is null && namedDestination is null && uri is null) continue;
            string? description = annotation.TryGetValue(ContentsName, out PdfObject? contentsValue)
                && Resolve(document, contentsValue, "A link description") is PdfString contents
                    ? PdfUnicodeEncoding.DecodeTextString(
                        contents.Bytes.Span, "A link description")
                    : null;
            result.Add(new PdfLinkInfo
            {
                PageIndex = pageIndex,
                AnnotationIndex = index,
                ObjectNumber = (annotationValue as PdfIndirectReference)?.ObjectNumber,
                Generation = (annotationValue as PdfIndirectReference)?.Generation,
                Left = Math.Min(x1, x2), Bottom = Math.Min(y1, y2),
                Right = Math.Max(x1, x2), Top = Math.Max(y1, y2),
                DestinationPageIndex = destinationPage,
                NamedDestination = namedDestination,
                Uri = uri,
                Description = description
            });
        }
        return result;
    }

    private static (int? PageIndex, string? Named) ResolveDestination(
        PdfDocument document, PdfObject value, Dictionary<(int ObjectNumber, int Generation), int> pages,
        Dictionary<string, PdfObject> namedDestinations)
    {
        PdfObject resolved = Resolve(document, value, "A link destination");
        string? named = resolved switch
        {
            PdfString text => PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span, "A named link destination"),
            PdfName name => name.ValueAsLatin1(),
            _ => null
        };
        if (named is not null)
        {
            if (!namedDestinations.TryGetValue(named, out PdfObject? namedValue)) return (null, named);
            resolved = Resolve(document, namedValue, "A named link destination value");
            if (resolved is PdfDictionary dictionary && dictionary.TryGetValue(DName, out PdfObject? d))
                resolved = Resolve(document, d, "A named link destination /D value");
        }
        if (resolved is not PdfArray array || array.Count == 0) return (null, named);
        int? pageIndex = array[0] switch
        {
            PdfIndirectReference reference when pages.TryGetValue(
                (reference.ObjectNumber, reference.Generation), out int found) => found,
            PdfInteger integer when integer.Value >= 0 && integer.Value < pages.Count => (int)integer.Value,
            _ => null
        };
        return (pageIndex, named);
    }

    private static Dictionary<string, PdfObject> ReadNamedDestinations(
        PdfDocument document, PdfDictionary catalog)
    {
        var result = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        if (catalog.TryGetValue(NamesName, out PdfObject? namesValue)
            && Resolve(document, namesValue, "The catalog /Names value") is PdfDictionary names
            && names.TryGetValue(DestsName, out PdfObject? tree))
            foreach (PdfNameTreeEntry entry in PdfNameTree.Read(document, tree))
                result.Add(PdfUnicodeEncoding.DecodeTextString(
                    entry.Key.Bytes.Span, "A named destination key"), entry.Value);
        if (catalog.TryGetValue(DestsName, out PdfObject? legacyValue)
            && Resolve(document, legacyValue, "The catalog /Dests value") is PdfDictionary legacy)
            foreach ((PdfName key, PdfObject destination) in legacy)
                result.TryAdd(key.ValueAsLatin1(), destination);
        return result;
    }

    private static double Number(PdfDocument document, PdfObject value) =>
        Resolve(document, value, "A link rectangle coordinate") switch
        {
            PdfInteger integer => integer.Value,
            PdfReal real => real.Value,
            _ => throw new InvalidOperationException("A link rectangle coordinate is not numeric.")
        };

    private static PdfObject Resolve(PdfDocument document, PdfObject value, string description)
    {
        var visited = new HashSet<(int, int)>();
        for (int depth = 0; value is PdfIndirectReference reference; depth++)
        {
            if (depth >= 32 || !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException($"{description} has an invalid reference chain.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
