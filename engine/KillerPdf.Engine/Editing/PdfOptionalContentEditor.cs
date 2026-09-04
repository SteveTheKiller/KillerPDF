using KillerPdf.Engine.Documents;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Editing;

/// <summary>Edits optional-content groups while preserving their existing configuration.</summary>
public static class PdfOptionalContentEditor
{
    private static readonly PdfName NameKey = new("Name"u8);

    /// <summary>Renames one registered layer by its source object number.</summary>
    public static byte[] RenameGroup(PdfDocument document, int objectNumber, string name)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (objectNumber <= 0) throw new ArgumentOutOfRangeException(nameof(objectNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("A layer name is required.", nameof(name));
        PdfOptionalContentInfo info = PdfOptionalContentReader.Read(document);
        PdfOptionalContentGroupInfo group = info.Groups.FirstOrDefault(
            item => item.ObjectNumber == objectNumber)
            ?? throw new KeyNotFoundException($"Optional-content group {objectNumber} is not registered.");
        if (info.Groups.Any(item => item.ObjectNumber != objectNumber
                && string.Equals(item.Name, name, StringComparison.Ordinal)))
            throw new ArgumentException("Layer names must be unique.", nameof(name));
        var reference = new PdfIndirectReference(group.ObjectNumber, group.Generation);
        PdfDictionary dictionary = document.Resolve(reference) as PdfDictionary
            ?? throw new InvalidOperationException("The optional-content group is not a dictionary.");
        var entries = dictionary.ToDictionary(entry => entry.Key, entry => entry.Value);
        entries[NameKey] = UnicodeString(name);
        return new PdfIncrementalUpdateBuilder(document)
            .ReplaceObject(objectNumber, new PdfDictionary(entries))
            .Build();
    }

    private static PdfString UnicodeString(string value)
    {
        byte[] text = PdfUnicodeEncoding.EncodeBigEndian(value);
        byte[] bytes = new byte[text.Length + 2];
        bytes[0] = 0xFE;
        bytes[1] = 0xFF;
        text.CopyTo(bytes, 2);
        return new PdfString(bytes, PdfStringForm.Hexadecimal);
    }
}
