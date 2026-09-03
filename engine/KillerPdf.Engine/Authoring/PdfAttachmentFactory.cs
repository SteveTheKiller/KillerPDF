using System.Security.Cryptography;
using System.Text;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Authoring;

internal static class PdfAttachmentFactory
{
    internal static void Validate(
        string fileName, string mimeType,
        PdfAssociatedFileRelationship relationship)
    {
        if (!IsPortableFileName(fileName))
            throw new ArgumentException(
                "An attachment name must be a portable plain file name.",
                nameof(fileName));
        PdfUnicodeEncoding.EncodeBigEndian(fileName);
        int separator = mimeType?.IndexOf('/') ?? -1;
        if (separator <= 0 || separator != mimeType!.LastIndexOf('/')
            || separator == mimeType.Length - 1
            || !IsMimeToken(mimeType.AsSpan(0, separator))
            || !IsMimeToken(mimeType.AsSpan(separator + 1)))
            throw new ArgumentException(
                "An attachment MIME type must contain exactly two valid token components.",
                nameof(mimeType));
        if (!Enum.IsDefined(relationship))
            throw new ArgumentOutOfRangeException(nameof(relationship));
    }

    internal static PdfStream EmbeddedFile(
        ReadOnlyMemory<byte> data, string mimeType,
        DateTimeOffset? modificationDate,
        DateTimeOffset? creationDate)
    {
        var parameters = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(Name("Size"), new PdfInteger(data.Length))
        };
        if (modificationDate.HasValue)
            parameters.Add(new KeyValuePair<PdfName, PdfObject>(
                Name("ModDate"), Latin1String(PdfDate(modificationDate.Value))));
        if (creationDate.HasValue)
            parameters.Add(new KeyValuePair<PdfName, PdfObject>(
                Name("CreationDate"), Latin1String(PdfDate(creationDate.Value))));
        parameters.Add(new KeyValuePair<PdfName, PdfObject>(
            Name("CheckSum"), new PdfString(MD5.HashData(data.Span),
                PdfStringForm.Hexadecimal)));
        return new PdfStream(new PdfDictionary([
            new(Name("Type"), Name("EmbeddedFile")),
            new(Name("Subtype"), Name(mimeType)),
            new(Name("Params"), new PdfDictionary(parameters))
        ]), data.Span);
    }

    internal static PdfDictionary FileSpecification(
        string fileName, string? description,
        PdfAssociatedFileRelationship relationship,
        PdfIndirectReference embeddedFile)
    {
        var entries = new List<KeyValuePair<PdfName, PdfObject>>
        {
            new(Name("Type"), Name("Filespec")),
            new(Name("F"), TextString(fileName)),
            new(Name("UF"), TextString(fileName)),
            new(Name("EF"), new PdfDictionary([
                new(Name("F"), embeddedFile),
                new(Name("UF"), embeddedFile)
            ])),
            new(Name("AFRelationship"), Name(relationship.ToString()))
        };
        if (!string.IsNullOrEmpty(description))
            entries.Add(new KeyValuePair<PdfName, PdfObject>(
                Name("Desc"), TextString(description)));
        return new PdfDictionary(entries);
    }

    private static bool IsMimeTokenCharacter(char value) =>
        value is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-'
            or '.' or '^' or '_' or (char)0x60 or '|' or '~';

    private static bool IsMimeToken(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
            if (!IsMimeTokenCharacter(character)) return false;
        return true;
    }

    private static bool IsPortableFileName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.EndsWith(' ') || value.EndsWith('.'))
            return false;
        foreach (char character in value)
            if (char.IsControl(character) || character is '<' or '>' or ':' or '"'
                or '/' or '\\' or '|' or '?' or '*')
                return false;
        int extension = value.IndexOf('.');
        ReadOnlySpan<char> stem = extension < 0 ? value : value.AsSpan(0, extension);
        return !(stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
            || IsNumberedDeviceName(stem, "COM")
            || IsNumberedDeviceName(stem, "LPT"));
    }

    private static bool IsNumberedDeviceName(ReadOnlySpan<char> value, string prefix) =>
        value.Length == 4
        && value[..3].Equals(prefix, StringComparison.OrdinalIgnoreCase)
        && value[3] is >= '1' and <= '9';

    private static PdfString TextString(string value) =>
        new([0xFE, 0xFF, .. PdfUnicodeEncoding.EncodeBigEndian(value)],
            PdfStringForm.Hexadecimal);
    private static PdfString Latin1String(string value) =>
        new(Encoding.Latin1.GetBytes(value), PdfStringForm.Literal);
    private static PdfName Name(string value) =>
        new(Encoding.ASCII.GetBytes(value));
    private static string PdfDate(DateTimeOffset value)
    {
        TimeSpan offset = value.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"D:{value:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }
}
