using System.Text;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits portfolio presentation settings while preserving collection schema and sort data.</summary>
public static class PdfCollectionEditor
{
    private static readonly PdfName CollectionName = Name("Collection");

    /// <summary>Sets the portfolio view and optional initially selected embedded document.</summary>
    public static byte[] SetPresentation(
        PdfDocument document, PdfCollectionView view, string? initialDocument = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (view is PdfCollectionView.Unknown || !Enum.IsDefined(view))
            throw new ArgumentOutOfRangeException(nameof(view));
        if (initialDocument is not null && string.IsNullOrWhiteSpace(initialDocument))
            throw new ArgumentException(
                "An initial portfolio document cannot be empty.", nameof(initialDocument));
        PdfPageTree tree = PdfPageTree.Read(document);
        PdfObject? current = tree.Catalog.GetValueOrDefault(CollectionName);
        PdfIndirectReference? reference = current as PdfIndirectReference;
        PdfDictionary collection = current is null ? new PdfDictionary([])
            : Resolve(document, current) as PdfDictionary
                ?? throw new InvalidOperationException(
                    "The catalog /Collection value is not a dictionary.");
        var entries = collection.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[Name("Type")] = Name("Collection");
        entries[Name("View")] = Name(view switch
        {
            PdfCollectionView.Details => "D",
            PdfCollectionView.Tile => "T",
            PdfCollectionView.Hidden => "H",
            _ => throw new ArgumentOutOfRangeException(nameof(view))
        });
        if (initialDocument is null) entries.Remove(Name("D"));
        else entries[Name("D")] = Text(initialDocument);
        var replacement = new PdfDictionary(entries);
        var update = new PdfIncrementalUpdateBuilder(document);
        if (reference is not null)
            update.ReplaceObject(reference.ObjectNumber, replacement);
        else
        {
            var catalog = tree.Catalog.ToDictionary(entry => entry.Key, entry => entry.Value);
            catalog[CollectionName] = replacement;
            update.ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog));
        }
        return update.Build();
    }

    /// <summary>Removes portfolio presentation metadata without removing embedded files.</summary>
    public static byte[] Clear(PdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        PdfPageTree tree = PdfPageTree.Read(document);
        if (!tree.Catalog.ContainsKey(CollectionName)) return document.Source.ToArray();
        var catalog = tree.Catalog
            .Where(entry => !entry.Key.Equals(CollectionName));
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(tree.CatalogReference.ObjectNumber, new PdfDictionary(catalog))
            .Build();
    }

    private static PdfObject Resolve(PdfDocument document, PdfObject value)
    {
        var visited = new HashSet<(int, int)>();
        while (value is PdfIndirectReference reference)
        {
            if (!visited.Add((reference.ObjectNumber, reference.Generation)))
                throw new InvalidOperationException("A collection reference contains a cycle.");
            value = document.Resolve(reference);
        }
        return value;
    }

    private static PdfString Text(string value) => new(
        [0xFE, 0xFF, .. PdfUnicodeEncoding.EncodeBigEndian(value)],
        PdfStringForm.Hexadecimal);
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
}
