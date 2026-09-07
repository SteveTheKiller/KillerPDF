using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Fonts;

internal static class PdfCMapMetadata
{
    internal static ReadOnlyMemory<byte> WithoutDictionaries(ReadOnlyMemory<byte> source,
        bool compatibilityRecovery = false)
    {
        // CMaps are PostScript programs. Their metadata dictionaries can contain def operators,
        // which are not valid inside a PDF object dictionary and do not affect character mappings.
        var tokenizer = new PdfTokenizer(source);
        byte[]? cleaned = null;
        int depth = 0, start = 0;
        bool mappingsStarted = false;
        while (true)
        {
            PdfToken token = tokenizer.Read();
            if (token.Kind == PdfTokenKind.EndOfInput) break;
            if (token.Kind == PdfTokenKind.Keyword && token.ValueAsLatin1() is
                "begincodespacerange" or "beginbfchar" or "beginbfrange"
                or "begincidchar" or "begincidrange")
                mappingsStarted = true;
            if (compatibilityRecovery && depth == 0 && !mappingsStarted
                && token.Kind == PdfTokenKind.Name && token.Value.Span.SequenceEqual("CIDSystemInfo"u8))
            {
                var probe = new PdfTokenizer(source, tokenizer.Position);
                PdfToken objectNumber = probe.Read(), generationNumber = probe.Read();
                PdfToken reference = probe.Read(), definition = probe.Read();
                if (objectNumber.Kind == PdfTokenKind.Integer
                    && int.TryParse(objectNumber.Value.Span, out int number) && number > 0
                    && generationNumber.Kind == PdfTokenKind.Integer
                    && int.TryParse(generationNumber.Value.Span, out int generation)
                    && generation is >= 0 and <= 65535
                    && reference.Kind == PdfTokenKind.Keyword && reference.Value.Span.SequenceEqual("R"u8)
                    && definition.Kind == PdfTokenKind.Keyword && definition.Value.Span.SequenceEqual("def"u8))
                {
                    cleaned ??= source.ToArray();
                    cleaned.AsSpan(token.Offset, probe.Position - token.Offset).Fill((byte)' ');
                    tokenizer.SetRawPosition(probe.Position);
                }
            }
            if (compatibilityRecovery && depth == 0 && !mappingsStarted
                && token.Kind == PdfTokenKind.Name && token.Value.Span.SequenceEqual("CMapName"u8))
            {
                var probe = new PdfTokenizer(source, tokenizer.Position);
                PdfToken name = probe.Read();
                int delimiter = probe.Position;
                if (name.Kind == PdfTokenKind.Name
                    && TryNameDefinitionEnd(source.Span, delimiter, out int definitionEnd))
                {
                    cleaned ??= source.ToArray();
                    cleaned.AsSpan(token.Offset, definitionEnd - token.Offset).Fill((byte)' ');
                    tokenizer.SetRawPosition(definitionEnd);
                }
                else if (name.Kind == PdfTokenKind.Name && delimiter + 1 < source.Length
                    && source.Span[delimiter] == (byte)'>' && source.Span[delimiter + 1] != (byte)'>')
                {
                    probe.SetRawPosition(delimiter + 1);
                    PdfToken definition = probe.Read();
                    if (definition.Kind == PdfTokenKind.Keyword
                        && definition.Value.Span.SequenceEqual("def"u8))
                    {
                        cleaned ??= source.ToArray();
                        cleaned[delimiter] = (byte)' ';
                        tokenizer.SetRawPosition(probe.Position);
                    }
                }
            }
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

    private static bool TryNameDefinitionEnd(ReadOnlySpan<byte> source, int position, out int end)
    {
        end = position;
        int limit = position + Math.Min(4096, source.Length - position);
        while (position < limit)
        {
            if (source[position] is not ((byte)' ' or (byte)'\t')) return false;
            while (position < limit && source[position] is (byte)' ' or (byte)'\t') position++;
            int start = position;
            while (position < limit && IsNameFragment(source[position])) position++;
            if (position == start) return false;
            ReadOnlySpan<byte> fragment = source[start..position];
            if (fragment.SequenceEqual("def"u8)
                && (position == source.Length || source[position] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                end = position;
                return true;
            }
            if (fragment.StartsWith("begin"u8) || fragment.StartsWith("end"u8)
                || fragment.SequenceEqual("usecmap"u8)) return false;
        }
        return false;
    }

    private static bool IsNameFragment(byte value) => value is
        >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z'
        or >= (byte)'0' and <= (byte)'9' or (byte)'-' or (byte)'+' or (byte)'_'
        or (byte)'.' or (byte)',';
}
