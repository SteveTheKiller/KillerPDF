using System.Text;
using System.Text.RegularExpressions;

namespace KillerPdf.Engine.Formats.Epub;

public sealed partial class EpubDocument
{
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
        if (prolog.AsSpan(position).StartsWith("<!DOCTYPE", StringComparison.Ordinal))
        {
            // EP-BF-015: a declaration that only names an external DTD (XHTML 1.0, 1.1, HTML5) is dropped; its
            // DTD is never fetched. One with an internal subset ('[') stays, and the XML reader prohibits it.
            int close = prolog.IndexOf('>', position);
            if (close < 0) return data;
            string declaration = prolog[position..(close + 1)];
            if (declaration.Contains('[') || declaration.IndexOf('<', 1) >= 0) return data;
            int bytePosition = position * width;
            int byteLength = declaration.Length * width;
            var sanitized = new byte[data.Length - byteLength];
            data.AsSpan(0, bytePosition).CopyTo(sanitized);
            data.AsSpan(bytePosition + byteLength).CopyTo(sanitized.AsSpan(bytePosition));
            data = sanitized;
        }
        return WithHtmlEntitiesAsReferences(data, littleEndian ? Encoding.Unicode
            : bigEndian ? Encoding.BigEndianUnicode : Encoding.Latin1);
    }

    private static readonly Regex NamedEntity = new("&([A-Za-z][A-Za-z0-9]{1,31});",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    // XHTML documents use the HTML 4 named entities their DTD would declare (&nbsp;, &mdash;, ...). Without
    // processing that DTD, each known name becomes its numeric reference. UTF-16 keeps its detected byte order;
    // Latin-1 preserves the other bytes in ASCII-compatible encodings.
    private static byte[] WithHtmlEntitiesAsReferences(byte[] data, Encoding encoding)
    {
        if (data.AsSpan().IndexOf((byte)'&') < 0) return data;
        string text = encoding.GetString(data);
        bool changed = false;
        string result = NamedEntity.Replace(text, match =>
        {
            string name = match.Groups[1].Value;
            if (name is "amp" or "lt" or "gt" or "quot" or "apos") return match.Value;
            string decoded = System.Net.WebUtility.HtmlDecode(match.Value);
            if (decoded == match.Value || decoded.Length == 0 || !Rune.TryGetRuneAt(decoded, 0, out Rune rune)
                || rune.Utf16SequenceLength != decoded.Length) return match.Value;
            changed = true;
            return "&#" + rune.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";";
        });
        return changed ? encoding.GetBytes(result) : data;
    }
}
