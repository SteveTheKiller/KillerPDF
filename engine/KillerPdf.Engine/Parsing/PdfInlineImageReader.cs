using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Parsing;

internal static class PdfInlineImageReader
{
    internal static PdfContentInstruction Read(PdfObjectParser parser, ReadOnlyMemory<byte> source,
        int offset, int maximumEntries, CancellationToken cancellationToken,
        Func<PdfName, int?>? resolveColorComponents)
    {
        var entries = new Dictionary<PdfName, PdfObject>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var token = parser.TakeContentToken();
            if (token.Kind == PdfTokenKind.Keyword && token.ValueAsLatin1() == "ID") break;
            if (token.Kind != PdfTokenKind.Name)
                throw new PdfSyntaxException("Inline image requires a dictionary key or ID", token.Offset);
            if (entries.Count >= maximumEntries)
                throw new PdfSyntaxException("Inline image dictionary limit exceeded", token.Offset);
            string key = ExpandKey(token.ValueAsLatin1());
            PdfObject value = parser.ParseObject();
            if (key is "Filter" or "ColorSpace") value = ExpandNames(value);
            if (!entries.TryAdd(Name(key), value))
                throw new PdfSyntaxException("Duplicate inline image dictionary key", token.Offset);
        }
        var dictionary = new PdfDictionary(entries);
        int start = parser.ContentPosition;
        var bytes = source.Span;
        if (start >= bytes.Length || !Whitespace(bytes[start]))
            throw new PdfSyntaxException("ID requires whitespace before image data", start);
        if (bytes[start++] == 13 && start < bytes.Length && bytes[start] == 10) start++;
        int length;
        if (dictionary.TryGetValue(Name("Filter"), out var filter))
        {
            if (filter is PdfArray array && array.Count > 0) filter = array[0];
            if (filter is not PdfName filterName)
                throw new PdfSyntaxException("Inline image Filter must be a name or nonempty name array", offset);
            int earlyChange = 1;
            if (dictionary.TryGetValue(Name("DecodeParms"), out var parameters))
            {
                if (parameters is PdfArray { Count: > 0 } parameterArray) parameters = parameterArray[0];
                if (parameters is PdfDictionary parameterDictionary
                    && parameterDictionary.TryGetValue(Name("EarlyChange"), out var early))
                    earlyChange = early is PdfInteger { Value: 0 or 1 } integer ? (int)integer.Value
                        : throw new PdfSyntaxException("EarlyChange must be zero or one", offset);
            }
            length = filterName.ValueAsLatin1() == "CCITTFaxDecode"
                ? PdfInlineFaxBoundary.Find(source[start..], parameters as PdfDictionary, cancellationToken)
                : PdfInlineImageBoundary.Find(source[start..], filterName.ValueAsLatin1(), cancellationToken, earlyChange);
        }
        else length = SampleLength(dictionary, offset, resolveColorComponents);
        if (length > bytes.Length - start)
            throw new PdfSyntaxException("Truncated inline image data", start);
        int end = start + length;
        if (end >= bytes.Length || !Whitespace(bytes[end]))
            throw new PdfSyntaxException("Inline image data requires whitespace before EI", end);
        while (end < bytes.Length && Whitespace(bytes[end])) end++;
        if (end + 2 > bytes.Length || bytes[end] != 'E' || bytes[end + 1] != 'I'
            || (end + 2 < bytes.Length && !Whitespace(bytes[end + 2]) && !Delimiter(bytes[end + 2])))
            throw new PdfSyntaxException("Inline image data must end with EI", end);
        parser.SetContentPosition(end + 2);
        return new PdfContentInstruction("BI", offset, [dictionary], source.Slice(start, length));
    }

    private static int SampleLength(PdfDictionary dictionary, int offset, Func<PdfName, int?>? resolveColorComponents)
    {
        int width = PositiveInteger(dictionary, "Width", offset);
        int height = PositiveInteger(dictionary, "Height", offset);
        bool mask = dictionary.TryGetValue(Name("ImageMask"), out var m) && m is PdfBoolean { Value: true };
        int bits = mask ? 1 : PositiveInteger(dictionary, "BitsPerComponent", offset);
        if (bits is not (1 or 2 or 4 or 8 or 16))
            throw new PdfSyntaxException("Invalid inline image BitsPerComponent", offset);
        int components = 1;
        if (!mask)
        {
            dictionary.TryGetValue(Name("ColorSpace"), out var cs);
            string? space = cs is PdfName name ? name.ValueAsLatin1()
                : cs is PdfArray { Count: > 0 } a && a[0] is PdfName n ? n.ValueAsLatin1() : null;
            components = space switch
            {
                "DeviceGray" or "Indexed" => 1,
                "DeviceRGB" => 3,
                "DeviceCMYK" => 4,
                _ => cs is PdfName resourceName && resolveColorComponents?.Invoke(resourceName) is int resolved
                    && resolved is > 0 and <= 32 ? resolved
                    : throw new NotSupportedException("Unfiltered inline image requires a known color space to determine its exact byte length.")
            };
        }
        long rowBytes = ((long)width * components * bits + 7) / 8;
        if (rowBytes > PdfContentStreamReader.MaximumSourceBytes / height)
            throw new PdfSyntaxException("Inline image sample size exceeds the content limit", offset);
        return (int)(rowBytes * height);
    }

    private static int PositiveInteger(PdfDictionary dictionary, string key, int offset) =>
        dictionary.TryGetValue(Name(key), out var value) && value is PdfInteger { Value: > 0 and <= int.MaxValue } n
            ? (int)n.Value : throw new PdfSyntaxException($"Inline image {key} must be a positive integer", offset);

    private static PdfObject ExpandNames(PdfObject value) => value switch
    {
        PdfName name => Name(ExpandName(name.ValueAsLatin1())),
        PdfArray array => new PdfArray(array.Select(ExpandNames)),
        _ => value
    };

    private static string ExpandKey(string key) => key switch
    {
        "W" => "Width", "H" => "Height", "BPC" => "BitsPerComponent", "CS" => "ColorSpace",
        "F" => "Filter", "DP" => "DecodeParms", "D" => "Decode", "IM" => "ImageMask",
        "I" => "Interpolate", _ => key
    };
    private static string ExpandName(string name) => name switch
    {
        "G" => "DeviceGray", "RGB" => "DeviceRGB", "CMYK" => "DeviceCMYK", "I" => "Indexed",
        "AHx" => "ASCIIHexDecode", "A85" => "ASCII85Decode", "Fl" => "FlateDecode",
        "LZW" => "LZWDecode", "RL" => "RunLengthDecode", "CCF" => "CCITTFaxDecode",
        "DCT" => "DCTDecode", _ => name
    };
    private static PdfName Name(string value) => new(Encoding.ASCII.GetBytes(value));
    internal static bool Whitespace(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    private static bool Delimiter(byte value) => value is (byte)'/' or (byte)'<' or (byte)'>'
        or (byte)'[' or (byte)']' or (byte)'(' or (byte)')' or (byte)'%';
}
