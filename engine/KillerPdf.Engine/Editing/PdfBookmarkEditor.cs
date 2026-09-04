using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits existing bookmark objects while preserving navigation and presentation data.</summary>
public static class PdfBookmarkEditor
{
    private static readonly PdfName TitleKey = new("Title"u8);
    private static readonly PdfName StyleKey = new("F"u8);
    private static readonly PdfName ColorKey = new("C"u8);

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

    /// <summary>Changes one bookmark's title style and optional color.</summary>
    public static byte[] SetAppearance(PdfDocument document, int objectNumber,
        PdfBookmarkStyle style = PdfBookmarkStyle.Regular, PdfRgbColor? color = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if ((style & ~(PdfBookmarkStyle.Italic | PdfBookmarkStyle.Bold)) != 0)
            throw new ArgumentOutOfRangeException(nameof(style));
        PdfBookmarkInfo bookmark = Flatten(PdfBookmarkReader.Read(document))
            .SingleOrDefault(item => item.ObjectNumber == objectNumber)
            ?? throw new KeyNotFoundException($"Bookmark {objectNumber} was not found.");
        var reference = new PdfIndirectReference(bookmark.ObjectNumber, bookmark.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The bookmark is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        if (style == PdfBookmarkStyle.Regular) entries.Remove(StyleKey);
        else entries[StyleKey] = new PdfInteger((long)style);
        if (color is PdfRgbColor value)
            entries[ColorKey] = new PdfArray([
                Number(value.Red), Number(value.Green), Number(value.Blue)]);
        else
            entries.Remove(ColorKey);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries)).Build();
    }

    private static PdfObject Number(double value) => value == Math.Truncate(value)
        ? new PdfInteger((long)value) : new PdfReal(value);

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
