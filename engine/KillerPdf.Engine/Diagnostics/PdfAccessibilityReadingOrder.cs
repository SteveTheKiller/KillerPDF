using System.Text;
using System.Text.Json;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Diagnostics;

/// <summary>One marked-content item in logical structure-tree reading order.</summary>
public sealed record PdfAccessibilityReadingOrderItem(
    int Sequence, string Role, int PageIndex, int MarkedContentId,
    int? StructureObjectNumber, string? AlternateDescription, string? ActualText);

/// <summary>Inspectable logical reading order for a tagged PDF.</summary>
public sealed class PdfAccessibilityReadingOrderReport
{
    internal PdfAccessibilityReadingOrderReport(
        IEnumerable<PdfAccessibilityReadingOrderItem> items) =>
        Items = Array.AsReadOnly(items.ToArray());

    /// <summary>Gets marked-content items in structure-tree order.</summary>
    public IReadOnlyList<PdfAccessibilityReadingOrderItem> Items { get; }

    /// <summary>Serializes the reading order with stable camel-case names.</summary>
    public string ToJson(bool indented = false) => JsonSerializer.Serialize(
        new { Items }, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = indented
        });

    /// <summary>Formats a compact reading-order report.</summary>
    public string ToText()
    {
        var output = new StringBuilder()
            .Append("Reading-order items: ").AppendLine(Items.Count.ToString());
        foreach (PdfAccessibilityReadingOrderItem item in Items)
            output.Append(item.Sequence + 1).Append(". ").Append(item.Role)
                .Append(" | Page ").Append(item.PageIndex + 1)
                .Append(" | MCID ").AppendLine(item.MarkedContentId.ToString());
        return output.ToString();
    }
}

/// <summary>Reads the explicit logical sequence from a tagged PDF structure tree.</summary>
public static class PdfAccessibilityReadingOrder
{
    /// <summary>Reads marked-content references in structure-tree order.</summary>
    public static PdfAccessibilityReadingOrderReport Read(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (!document.IsDecrypted)
            throw new InvalidOperationException(
                "Authenticate the document before reading accessibility order.");
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.TryGetValue(Name("StructTreeRoot"), out PdfObject? rootValue))
            throw new InvalidOperationException("The document has no structure tree.");
        PdfDictionary root = Resolve(document, rootValue) as PdfDictionary
            ?? throw new InvalidOperationException("The structure-tree root is not a dictionary.");
        var pageIndexes = tree.Pages.ToDictionary(
            page => (page.Reference.ObjectNumber, page.Reference.Generation),
            page => page.Index);
        var visited = new HashSet<(int, int)>();
        var items = new List<PdfAccessibilityReadingOrderItem>();
        if (root.TryGetValue(Name("K"), out PdfObject? children))
            Visit(children, null, null, null, null, 0);
        return new PdfAccessibilityReadingOrderReport(items);

        void Visit(PdfObject value, string? role, int? pageIndex,
            int? structureObjectNumber, PdfDictionary? structureElement, int depth)
        {
            if (depth >= 256)
                throw new InvalidOperationException(
                    "The structure tree exceeds the supported nesting depth.");
            PdfIndirectReference? reference = value as PdfIndirectReference;
            if (reference is not null
                && !visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("The structure tree contains a repeated reference.");
            PdfObject resolved = Resolve(document, value);
            if (resolved is PdfArray array)
            {
                foreach (PdfObject child in array)
                    Visit(child, role, pageIndex, structureObjectNumber,
                        structureElement, depth + 1);
                return;
            }
            if (resolved is PdfInteger markedContent)
            {
                Add(role, pageIndex, markedContent.Value,
                    structureObjectNumber, structureElement);
                return;
            }
            if (resolved is not PdfDictionary dictionary) return;
            if (IsName(dictionary, "Type", "MCR"))
            {
                int? markedContentId = Integer(dictionary, "MCID");
                Add(role, PageIndex(dictionary) ?? pageIndex, markedContentId,
                    structureObjectNumber, structureElement);
                return;
            }
            if (!IsName(dictionary, "Type", "StructElem")) return;
            string elementRole = dictionary.TryGetValue(Name("S"), out PdfObject? roleValue)
                && Resolve(document, roleValue) is PdfName roleName
                    ? roleName.ValueAsLatin1()
                    : throw new InvalidOperationException(
                        "A structure element has no valid role.");
            int? elementPage = PageIndex(dictionary) ?? pageIndex;
            int? elementObject = reference?.ObjectNumber;
            if (dictionary.TryGetValue(Name("K"), out PdfObject? kids))
                Visit(kids, elementRole, elementPage, elementObject,
                    dictionary, depth + 1);
        }

        void Add(string? role, int? pageIndex, long? markedContentId,
            int? objectNumber, PdfDictionary? element)
        {
            if (role is null || pageIndex is null || !markedContentId.HasValue
                || markedContentId.Value < 0 || markedContentId.Value > int.MaxValue)
                throw new InvalidOperationException(
                    "A reading-order item has no valid role, page, or marked-content identifier.");
            items.Add(new PdfAccessibilityReadingOrderItem(items.Count, role,
                pageIndex.Value, (int)markedContentId.Value, objectNumber,
                Text(element, "Alt"), Text(element, "ActualText")));
        }

        int? PageIndex(PdfDictionary dictionary)
        {
            if (!dictionary.TryGetValue(Name("Pg"), out PdfObject? pageValue)
                || pageValue is not PdfIndirectReference pageReference) return null;
            return pageIndexes.TryGetValue(
                (pageReference.ObjectNumber, pageReference.Generation), out int index)
                ? index : throw new InvalidOperationException(
                    "A structure item references a page outside the document.");
        }

        string? Text(PdfDictionary? dictionary, string key)
        {
            if (dictionary is null
                || !dictionary.TryGetValue(Name(key), out PdfObject? value)) return null;
            return Resolve(document, value) is PdfString text
                ? PdfUnicodeEncoding.DecodeTextString(text.Bytes.Span,
                    $"A structure element /{key} value")
                : throw new InvalidOperationException(
                    $"A structure element /{key} value is not a string.");
        }
    }

    private static int? Integer(PdfDictionary dictionary, string key) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && value is PdfInteger integer ? checked((int)integer.Value) : null;

    private static bool IsName(PdfDictionary dictionary, string key, string expected) =>
        dictionary.TryGetValue(Name(key), out PdfObject? value)
            && value is PdfName name && name.ValueAsLatin1() == expected;

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var references = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!references.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException(
                    "An accessibility reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
