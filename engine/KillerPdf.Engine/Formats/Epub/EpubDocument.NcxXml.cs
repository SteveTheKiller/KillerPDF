using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
    private static readonly Regex CanonicalNcxDeclaration = new(
        "<!DOCTYPE[ \\t\\r\\n]+ncx[ \\t\\r\\n]+(?:PUBLIC[ \\t\\r\\n]+(?:\"-//NISO//DTD ncx 2005-1//EN\"|'-//NISO//DTD ncx 2005-1//EN')[ \\t\\r\\n]+|SYSTEM[ \\t\\r\\n]+)(?:\"http://www\\.daisy\\.org/z3986/2005/ncx-2005-1\\.dtd\"|'http://www\\.daisy\\.org/z3986/2005/ncx-2005-1\\.dtd')[ \\t\\r\\n]*>",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static XDocument NcxXml(byte[] data)
    {
        // Inspect the prolog without changing the original document's encoding or content bytes.
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
            if (end < 0) return Xml(data);
            position = end + terminator.Length;
        }
        if (!prolog.AsSpan(position).StartsWith("<!DOCTYPE", StringComparison.Ordinal)) return Xml(data);
        var declaration = CanonicalNcxDeclaration.Match(prolog, position);
        if (!declaration.Success || declaration.Index != position) return Xml(data);

        // No internal subset can match this grammar. The XML reader never processes a DTD.
        int bytePosition = position * width;
        int byteLength = declaration.Length * width;
        var sanitized = new byte[data.Length - byteLength];
        data.AsSpan(0, bytePosition).CopyTo(sanitized);
        data.AsSpan(bytePosition + byteLength).CopyTo(sanitized.AsSpan(bytePosition));
        return Xml(sanitized);
    }
}
