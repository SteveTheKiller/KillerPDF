using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Fonts;

internal static class PdfCMapMetadata
{
    internal static ReadOnlyMemory<byte> WithoutDictionaries(ReadOnlyMemory<byte> source)
    {
        // CMaps are PostScript programs. Their metadata dictionaries can contain def operators,
        // which are not valid inside a PDF object dictionary and do not affect character mappings.
        var tokenizer = new PdfTokenizer(source);
        byte[]? cleaned = null;
        int depth = 0, start = 0;
        while (true)
        {
            PdfToken token = tokenizer.Read();
            if (token.Kind == PdfTokenKind.EndOfInput) break;
            if (token.Kind == PdfTokenKind.DictionaryStart)
            {
                if (depth++ == 0) start = token.Offset;
                if (depth > 256) throw new FormatException("CMap metadata nesting limit exceeded.");
            }
            if (token.Kind == PdfTokenKind.DictionaryEnd && depth > 0 && --depth == 0)
            {
                cleaned ??= source.ToArray();
                cleaned.AsSpan(start, token.Offset + token.Length - start).Fill((byte)' ');
            }
        }
        if (depth != 0) throw new FormatException("Unterminated CMap metadata dictionary.");
        return cleaned is null ? source : cleaned;
    }
}
