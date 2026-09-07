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
                if (name.Kind == PdfTokenKind.Name && delimiter + 1 < source.Length
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
}
