using System.Globalization;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Parsing;

/// <summary>Builds typed PDF objects from the lexical token stream.</summary>
/// <remarks>Creates a parser positioned at a specified byte offset.</remarks>
public sealed class PdfObjectParser(
    ReadOnlyMemory<byte> source,
    int startOffset,
    Func<PdfIndirectReference, long>? streamLengthResolver = null,
    bool allowDuplicateDictionaryKeys = false)
{
    /// <summary>Maximum nested array and dictionary depth accepted by the object parser.</summary>
    public const int MaximumNestingDepth = 256;

    private static readonly PdfName LengthName = new("Length"u8);

    private readonly ReadOnlyMemory<byte> _source = source;
    private readonly PdfTokenizer _tokenizer = new(source, startOffset);
    private readonly Func<PdfIndirectReference, long>? _streamLengthResolver = streamLengthResolver;
    private readonly bool _allowDuplicateDictionaryKeys = allowDuplicateDictionaryKeys;
    private readonly List<PdfToken> _lookahead = [];
    private bool _allowIndirectReferences = true;

    internal static PdfObjectParser ForContent(ReadOnlyMemory<byte> source) =>
        new(source) { _allowIndirectReferences = false };

    internal PdfToken PeekContentToken() => Peek();
    internal PdfToken TakeContentToken() => Take();
    internal int ContentPosition => _lookahead.Count == 0 ? _tokenizer.Position
        : throw new InvalidOperationException("Content lookahead must be consumed before reading raw bytes.");
    internal void SetContentPosition(int position)
    {
        _lookahead.Clear();
        _tokenizer.SetRawPosition(position);
    }

    /// <summary>Creates a parser positioned at the beginning of a PDF byte sequence.</summary>
    public PdfObjectParser(
        ReadOnlyMemory<byte> source,
        Func<PdfIndirectReference, long>? streamLengthResolver = null,
        bool allowDuplicateDictionaryKeys = false)
        : this(source, 0, streamLengthResolver, allowDuplicateDictionaryKeys)
    {
    }

    /// <summary>Parses one direct or indirect-reference PDF object.</summary>
    public PdfObject ParseObject() => ParseObject(0);

    /// <summary>Parses exactly one direct object and rejects any trailing non-trivia bytes.</summary>
    public PdfObject ParseSingleObject()
    {
        PdfObject value = ParseObject(0);
        PdfToken trailing = Take();
        if (trailing.Kind != PdfTokenKind.EndOfInput)
            throw Error("Unexpected data follows the PDF object", trailing.Offset);
        return value;
    }

    /// <summary>Parses one complete indirect object declaration.</summary>
    public PdfIndirectObject ParseIndirectObject() =>
        ParseIndirectObjectCore(out _);

    internal PdfIndirectObject ParseIndirectDictionaryObject(
        out int dictionaryEndOffset)
    {
        PdfIndirectObject result = ParseIndirectObjectCore(out int valueEndOffset);
        dictionaryEndOffset = result.Value is PdfDictionary
            ? valueEndOffset : -1;
        return result;
    }

    private PdfIndirectObject ParseIndirectObjectCore(out int valueEndOffset)
    {
        PdfToken objectNumberToken = Take();
        PdfToken generationToken = Take();
        PdfToken objToken = Take();

        long objectNumber = ParseRequiredInteger(objectNumberToken, "An indirect object must begin with an object number");
        long generation = ParseRequiredInteger(generationToken, "An indirect object must include a generation number");
        RequireKeyword(objToken, "obj", "An indirect object header must end with the obj keyword");
        ValidateReference(objectNumber, generation, objectNumberToken.Offset);

        PdfObject value = ParseObject(0);
        valueEndOffset = _tokenizer.Position;
        PdfToken endToken = Take();
        if (IsKeyword(endToken, "stream"))
        {
            if (value is not PdfDictionary dictionary)
                throw Error("A stream must be preceded by a dictionary", endToken.Offset);

            value = ParseStream(dictionary, endToken.Offset);
            endToken = Take();
        }

        RequireKeyword(endToken, "endobj", "An indirect object must end with the endobj keyword");

        return new PdfIndirectObject((int)objectNumber, (int)generation, value, objectNumberToken.Offset);
    }

    private PdfObject ParseObject(int depth)
    {
        if (depth >= MaximumNestingDepth)
            throw Error("The PDF object nesting limit was exceeded", Peek().Offset);

        PdfToken token = Take();
        return token.Kind switch
        {
            PdfTokenKind.Null => PdfNull.Instance,
            PdfTokenKind.Boolean => new PdfBoolean(token.Value.Span.SequenceEqual("true"u8)),
            PdfTokenKind.Integer => ParseIntegerOrReference(token),
            PdfTokenKind.Real => ParseReal(token),
            PdfTokenKind.Name => new PdfName(token.Value.Span),
            PdfTokenKind.LiteralString => new PdfString(token.Value.Span, PdfStringForm.Literal),
            PdfTokenKind.HexString => new PdfString(token.Value.Span, PdfStringForm.Hexadecimal),
            PdfTokenKind.ArrayStart => ParseArray(depth + 1, token.Offset),
            PdfTokenKind.DictionaryStart => ParseDictionary(depth + 1, token.Offset),
            PdfTokenKind.EndOfInput => throw Error("Expected a PDF object but reached the end of input", token.Offset),
            _ => throw Error($"Token {token.Kind} cannot begin a PDF object", token.Offset)
        };
    }

    private PdfObject ParseIntegerOrReference(PdfToken first)
    {
        long value = ParseInteger(first);
        if (!_allowIndirectReferences) return new PdfInteger(value);
        if (Peek().Kind != PdfTokenKind.Integer
            || Peek(1).Kind != PdfTokenKind.Keyword
            || !Peek(1).Value.Span.SequenceEqual("R"u8))
            return new PdfInteger(value);

        PdfToken generationToken = Take();
        Take(); // R
        long generation = ParseInteger(generationToken);
        ValidateReference(value, generation, first.Offset);
        return new PdfIndirectReference((int)value, (int)generation);
    }

    private static PdfReal ParseReal(PdfToken token)
    {
        if (!double.TryParse(token.Value.Span, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                             CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value))
            throw Error("The real number is outside the supported finite range", token.Offset);

        return new PdfReal(value);
    }

    private PdfArray ParseArray(int depth, int start)
    {
        var items = new List<PdfObject>();
        while (Peek().Kind != PdfTokenKind.ArrayEnd)
        {
            if (Peek().Kind == PdfTokenKind.EndOfInput)
                throw Error("Unterminated PDF array", start);
            items.Add(ParseObject(depth));
        }

        Take();
        return new PdfArray(items);
    }

    private PdfDictionary ParseDictionary(int depth, int start)
    {
        var entries = new Dictionary<PdfName, PdfObject>();
        while (Peek().Kind != PdfTokenKind.DictionaryEnd)
        {
            PdfToken keyToken = Take();
            if (keyToken.Kind == PdfTokenKind.EndOfInput)
                throw Error("Unterminated PDF dictionary", start);
            if (keyToken.Kind != PdfTokenKind.Name)
                throw Error("A PDF dictionary key must be a name", keyToken.Offset);

            var key = new PdfName(keyToken.Value.Span);
            PdfObject value = ParseObject(depth);
            if (!entries.TryAdd(key, value) && !_allowDuplicateDictionaryKeys)
                throw Error($"The PDF dictionary contains the duplicate key {key}", keyToken.Offset);
            entries[key] = value;
        }

        Take();
        return new PdfDictionary(entries);
    }

    private PdfStream ParseStream(PdfDictionary dictionary, int streamKeywordOffset)
    {
        if (_lookahead.Count != 0)
            throw Error("Internal parser lookahead crossed a stream boundary", streamKeywordOffset);

        ConsumeStreamOpeningLineEnding(streamKeywordOffset);
        int dataOffset = _tokenizer.Position;
        int length = ResolveStreamLength(dictionary, streamKeywordOffset);
        if (_tokenizer.RemainingByteCount < length)
            throw Error("The stream payload is shorter than its Length entry", dataOffset);

        ReadOnlyMemory<byte> encodedData = _tokenizer.ReadRawBytes(length);
        ConsumeStreamClosingLineEnding(dataOffset + length);

        PdfToken endStream = Take();
        if (!IsKeyword(endStream, "endstream"))
            return RecoverStream(dictionary, dataOffset, length, streamKeywordOffset);
        return new PdfStream(dictionary, encodedData.Span);
    }

    private PdfStream RecoverStream(
        PdfDictionary dictionary,
        int dataOffset,
        int declaredLength,
        int streamKeywordOffset)
    {
        ReadOnlySpan<byte> marker = "endstream"u8;
        ReadOnlySpan<byte> source = _source.Span;
        int searchStart = dataOffset;
        int searchEnd = Math.Min(source.Length, checked(dataOffset + Math.Max(declaredLength, 0) + 1_048_576));
        int expectedEnd = Math.Min(source.Length, checked(dataOffset + declaredLength));
        int best = -1;
        int bestDistance = int.MaxValue;

        while (searchStart <= searchEnd - marker.Length)
        {
            int relative = source[searchStart..searchEnd].IndexOf(marker);
            if (relative < 0) break;
            int candidate = searchStart + relative;
            int after = candidate + marker.Length;
            bool leftDelimited = candidate == dataOffset || IsPdfWhitespace(source[candidate - 1]);
            bool rightDelimited = after == source.Length
                || IsPdfWhitespace(source[after])
                || source[after] is (byte)'/' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                    or (byte)'(' or (byte)')';
            if (leftDelimited && rightDelimited)
            {
                int distance = Math.Abs(candidate - expectedEnd);
                if (distance < bestDistance)
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }
            searchStart = candidate + 1;
        }

        if (best < 0)
            throw Error("A stream payload must end with the endstream keyword", streamKeywordOffset);

        int dataEnd = best;
        if (dataEnd > dataOffset && source[dataEnd - 1] == (byte)'\n') dataEnd--;
        if (dataEnd > dataOffset && source[dataEnd - 1] == (byte)'\r') dataEnd--;
        _lookahead.Clear();
        _tokenizer.SetRawPosition(best);
        PdfToken recoveredEnd = Take();
        RequireKeyword(recoveredEnd, "endstream",
            "A recovered stream payload must end with the endstream keyword");
        return new PdfStream(dictionary, source[dataOffset..dataEnd]);
    }

    private static bool IsPdfWhitespace(byte value) =>
        value is 0 or 9 or 10 or 12 or 13 or 32;

    private int ResolveStreamLength(PdfDictionary dictionary, int offset)
    {
        if (!dictionary.TryGetValue(LengthName, out PdfObject lengthObject))
            throw Error("A stream dictionary must contain a Length entry", offset);

        long length = lengthObject switch
        {
            PdfInteger integer => integer.Value,
            PdfIndirectReference reference when _streamLengthResolver is not null =>
                _streamLengthResolver(reference),
            PdfIndirectReference => throw Error(
                "The stream Length is indirect and no length resolver was supplied", offset),
            _ => throw Error("A stream Length must be an integer or an indirect reference", offset)
        };

        if (length is < 0 or > int.MaxValue)
            throw Error("A stream Length must be between 0 and 2,147,483,647 bytes", offset);
        return (int)length;
    }

    private void ConsumeStreamOpeningLineEnding(int offset)
    {
        if (!_tokenizer.TryReadRawByte(out byte first))
            throw Error("The stream keyword must be followed by a line ending", offset);

        if (first == (byte)'\n')
            return;
        if (first == (byte)'\r')
        {
            if (_tokenizer.TryPeekRawByte(out byte second) && second == (byte)'\n')
                _tokenizer.TryReadRawByte(out _);
            return;
        }
        if (first is 0 or 9 or 12 or 32)
        {
            while (_tokenizer.TryPeekRawByte(out byte horizontal)
                && horizontal is 0 or 9 or 12 or 32)
                _tokenizer.TryReadRawByte(out _);
            if (_tokenizer.TryPeekRawByte(out byte lineEnding)
                && lineEnding is (byte)'\r' or (byte)'\n')
            {
                _tokenizer.TryReadRawByte(out byte consumed);
                if (consumed == (byte)'\r'
                    && _tokenizer.TryPeekRawByte(out byte lf)
                    && lf == (byte)'\n')
                    _tokenizer.TryReadRawByte(out _);
            }
            return;
        }
        _tokenizer.RewindRawByte();
    }

    private void ConsumeStreamClosingLineEnding(int offset)
    {
        if (!_tokenizer.TryPeekRawByte(out byte first))
            throw Error("The stream payload must be followed by endstream", offset);
        if (first == (byte)'\n')
        {
            _tokenizer.TryReadRawByte(out _);
            return;
        }
        if (first == (byte)'\r')
        {
            _tokenizer.TryReadRawByte(out _);
            // CR, LF, and CRLF are all PDF line endings here. If this is CRLF, consume the LF.
            if (_tokenizer.TryPeekRawByte(out byte second) && second == (byte)'\n')
                _tokenizer.TryReadRawByte(out _);
        }
        // qpdf and other widely used producers can include the closing EOL in /Length. In that
        // case the tokenizer is already positioned at endstream; its exact keyword is still
        // required immediately below, so accepting the omitted separator is unambiguous.
    }

    private static long ParseRequiredInteger(PdfToken token, string message)
    {
        if (token.Kind != PdfTokenKind.Integer)
            throw Error(message, token.Offset);
        return ParseInteger(token);
    }

    private static long ParseInteger(PdfToken token)
    {
        if (!long.TryParse(token.Value.Span, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long value))
            throw Error("The integer is outside the supported 64-bit range", token.Offset);
        return value;
    }

    private static void ValidateReference(long objectNumber, long generation, int offset)
    {
        if (objectNumber is < 0 or > int.MaxValue)
            throw Error("A PDF object number must be between 0 and 2,147,483,647", offset);
        if (generation is < 0 or > 65_535)
            throw Error("A PDF generation number must be between 0 and 65,535", offset);
    }

    private static void RequireKeyword(PdfToken token, string keyword, string message)
    {
        if (!IsKeyword(token, keyword))
            throw Error(message, token.Offset);
    }

    private static bool IsKeyword(PdfToken token, string keyword) =>
        token.Kind == PdfTokenKind.Keyword
        && token.Value.Span.SequenceEqual(System.Text.Encoding.ASCII.GetBytes(keyword));

    private PdfToken Peek(int distance = 0)
    {
        while (_lookahead.Count <= distance)
            _lookahead.Add(_tokenizer.Read());
        return _lookahead[distance];
    }

    private PdfToken Take()
    {
        PdfToken token = Peek();
        _lookahead.RemoveAt(0);
        return token;
    }

    private static PdfSyntaxException Error(string message, int offset) => new(message, offset);

}
