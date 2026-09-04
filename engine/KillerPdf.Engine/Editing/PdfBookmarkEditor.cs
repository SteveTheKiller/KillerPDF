using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits existing bookmark objects while preserving navigation and presentation data.</summary>
public static class PdfBookmarkEditor
{
    private static readonly PdfName TitleKey = new("Title"u8);

    /// <summary>Renames one bookmark by its source object number.</summary>
    public static byte[] Rename(PdfDocument document, int objectNumber, string title)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(title))
            throw new ArgumentException("A bookmark title is required.", nameof(title));
        PdfBookmarkInfo bookmark = Flatten(PdfBookmarkReader.Read(document))
            .SingleOrDefault(item => item.ObjectNumber == objectNumber)
            ?? throw new KeyNotFoundException($"Bookmark {objectNumber} was not found.");
        var reference = new PdfIndirectReference(bookmark.ObjectNumber, bookmark.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The bookmark is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(title);
        entries[TitleKey] = new PdfString([0xFE, 0xFF, .. text], PdfStringForm.Hexadecimal);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries)).Build();
    }

    private static IEnumerable<PdfBookmarkInfo> Flatten(
        IEnumerable<PdfBookmarkInfo> bookmarks)
    {
        foreach (PdfBookmarkInfo bookmark in bookmarks)
        {
            yield return bookmark;
            foreach (PdfBookmarkInfo child in Flatten(bookmark.Children)) yield return child;
        }
    }
}
