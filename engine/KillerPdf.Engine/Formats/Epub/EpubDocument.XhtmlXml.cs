using System.Text;
using System.Text.RegularExpressions;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private static readonly Regex CanonicalXhtml11Declaration = new(
        "<!DOCTYPE[ \\t\\r\\n]+html[ \\t\\r\\n]+PUBLIC[ \\t\\r\\n]+(?:\"-//W3C//DTD XHTML 1\\.1//EN\"|'-//W3C//DTD XHTML 1\\.1//EN')[ \\t\\r\\n]+(?:\"http://www\\.w3\\.org/TR/xhtml11/DTD/xhtml11\\.dtd\"|'http://www\\.w3\\.org/TR/xhtml11/DTD/xhtml11\\.dtd')[ \\t\\r\\n]*>",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static byte[] XhtmlWithoutCanonicalDeclaration(byte[] data)
    {
        // Preserve encoding and content bytes; inspect ASCII markup at its original byte offsets.
        bool littleEndian = data.Length >= 2 && ((data[0] == 0xff && data[1] == 0xfe) || (data[0] == '<' && data[1] == 0));
        bool bigEndian = data.Length >= 2 && ((data[0] == 0xfe && data[1] == 0xff) || (data[0] == 0 && data[1] == '<'));
        int width = littleEndian || bigEndian ? 2 : 1;
        string prolog = littleEndian ? Encoding.Unicode.GetString(data)
            : bigEndian ? Encoding.BigEndianUnicode.GetString(data) : Encoding.Latin1.GetString(data);
        int position = width == 2 && prolog.StartsWith('\ufeff') ? 1
            : data.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? 3 : 0;
        while (position < prolog.Length)
        {
            while (position < prolog.Length && prolog[position] is ' ' or '\t' or '\r' or '\n') position++;
            string? terminator = prolog.AsSpan(position).StartsWith("<!--", StringComparison.Ordinal) ? "-->"
                : prolog.AsSpan(position).StartsWith("<?", StringComparison.Ordinal) ? "?>" : null;
            if (terminator is null) break;
            int end = prolog.IndexOf(terminator, position, StringComparison.Ordinal);
            if (end < 0) return data;
            position = end + terminator.Length;
        }
        if (!prolog.AsSpan(position).StartsWith("<!DOCTYPE", StringComparison.Ordinal)) return data;
        var declaration = CanonicalXhtml11Declaration.Match(prolog, position);
        if (!declaration.Success || declaration.Index != position) return data;

        // This grammar cannot include an internal subset. The XML reader still prohibits DTDs.
        int bytePosition = position * width;
        int byteLength = declaration.Length * width;
        var sanitized = new byte[data.Length - byteLength];
        data.AsSpan(0, bytePosition).CopyTo(sanitized);
        data.AsSpan(bytePosition + byteLength).CopyTo(sanitized.AsSpan(bytePosition));
        return sanitized;
    }
}
