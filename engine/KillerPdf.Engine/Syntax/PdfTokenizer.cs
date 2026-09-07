namespace KillerPdf.Engine.Syntax;

/// <summary>
/// Reads PDF lexical tokens directly from bytes. It does not interpret indirect objects or
/// streams; those belong to the object parser above this layer.
/// </summary>
public sealed class PdfTokenizer
{
    private readonly ReadOnlyMemory<byte> _source;
    private int _position;

    /// <summary>Creates a tokenizer positioned at the beginning of a PDF byte sequence.</summary>
    public PdfTokenizer(ReadOnlyMemory<byte> source) : this(source, 0) { }

    /// <summary>Creates a tokenizer positioned at the specified byte offset.</summary>
    public PdfTokenizer(ReadOnlyMemory<byte> source, int startOffset)
    {
        if (startOffset < 0 || startOffset > source.Length)
            throw new ArgumentOutOfRangeException(nameof(startOffset));

        _source = source;
        _position = startOffset;
    }

    /// <summary>Gets the current zero-based byte position.</summary>
    public int Position => _position;

    internal int RemainingByteCount => _source.Length - _position;

    internal bool TryPeekRawByte(out byte value)
    {
        if (_position >= _source.Length)
        {
            value = 0;
            return false;
        }

        value = _source.Span[_position];
        return true;
    }

    internal bool TryReadRawByte(out byte value)
    {
        if (_position >= _source.Length)
        {
            value = 0;
            return false;
        }

        value = _source.Span[_position++];
        return true;
    }

    internal void RewindRawByte()
    {
        if (_position == 0)
            throw new InvalidOperationException("The tokenizer is already at the beginning of the source.");
        _position--;
    }

    internal void SetRawPosition(int position)
    {
        if (position < 0 || position > _source.Length)
            throw new ArgumentOutOfRangeException(nameof(position));
        _position = position;
    }

    internal ReadOnlyMemory<byte> ReadRawBytes(int length)
    {
        if (length < 0 || length > RemainingByteCount)
            throw new ArgumentOutOfRangeException(nameof(length));

        ReadOnlyMemory<byte> value = _source.Slice(_position, length);
        _position += length;
        return value;
    }

    /// <summary>Reads the next lexical token after skipping PDF whitespace and comments.</summary>
    public PdfToken Read()
    {
        SkipTrivia();

        ReadOnlySpan<byte> source = _source.Span;
        if (_position >= source.Length)
            return Token(PdfTokenKind.EndOfInput, _position, 0);

        int start = _position;
        byte current = source[_position++];

        return current switch
        {
            (byte)'[' => Token(PdfTokenKind.ArrayStart, start, 1),
            (byte)']' => Token(PdfTokenKind.ArrayEnd, start, 1),
            (byte)'{' => Token(PdfTokenKind.BraceStart, start, 1),
            (byte)'}' => Token(PdfTokenKind.BraceEnd, start, 1),
            (byte)'/' => ReadName(start),
            (byte)'(' => ReadLiteralString(start),
            (byte)'<' => ReadLessThan(start),
            (byte)'>' => ReadGreaterThan(start),
            (byte)')' => throw Error("Unexpected closing parenthesis", start),
            _ => ReadRegular(start)
        };
    }

    private PdfToken ReadName(int start)
    {
        ReadOnlySpan<byte> source = _source.Span;
        int valueStart = _position;
        while (_position < source.Length && !IsWhitespace(source[_position]) && !IsDelimiter(source[_position]))
            _position++;

        ReadOnlySpan<byte> raw = source[valueStart.._position];
        int escape = raw.IndexOf((byte)'#');
        if (escape < 0)
            return Token(PdfTokenKind.Name, start, _position - start, _source.Slice(valueStart, raw.Length));

        byte[] decoded = new byte[raw.Length];
        int written = 0;
        for (int i = 0; i < raw.Length; i++)
        {
            if (raw[i] != (byte)'#')
            {
                decoded[written++] = raw[i];
                continue;
            }

            if (i + 2 >= raw.Length || !TryHex(raw[i + 1], out int high) || !TryHex(raw[i + 2], out int low))
                throw Error("A name escape must contain two hexadecimal digits", valueStart + i);

            decoded[written++] = (byte)((high << 4) | low);
            i += 2;
        }

        return Token(PdfTokenKind.Name, start, _position - start, decoded.AsMemory(0, written));
    }

    private PdfToken ReadLiteralString(int start)
    {
        ReadOnlySpan<byte> source = _source.Span;
        var decoded = new List<byte>();
        int depth = 1;

        while (_position < source.Length)
        {
            byte current = source[_position++];
            if (current == (byte)'(')
            {
                depth++;
                decoded.Add(current);
                continue;
            }

            if (current == (byte)')')
            {
                depth--;
                if (depth == 0)
                    return Token(PdfTokenKind.LiteralString, start, _position - start, decoded.ToArray());

                decoded.Add(current);
                continue;
            }

            if (current == (byte)'\\')
            {
                ReadEscape(decoded, start);
                continue;
            }

            if (current == (byte)'\r')
            {
                if (_position < source.Length && source[_position] == (byte)'\n')
                    _position++;
                decoded.Add((byte)'\n');
                continue;
            }

            decoded.Add(current);
        }

        throw Error("Unterminated literal string", start);
    }

    private void ReadEscape(List<byte> decoded, int stringStart)
    {
        ReadOnlySpan<byte> source = _source.Span;
        if (_position >= source.Length)
            throw Error("Unterminated escape in literal string", stringStart);

        byte escaped = source[_position++];
        switch (escaped)
        {
            case (byte)'n': decoded.Add((byte)'\n'); return;
            case (byte)'r': decoded.Add((byte)'\r'); return;
            case (byte)'t': decoded.Add((byte)'\t'); return;
            case (byte)'b': decoded.Add((byte)'\b'); return;
            case (byte)'f': decoded.Add((byte)'\f'); return;
            case (byte)'(':
            case (byte)')':
            case (byte)'\\': decoded.Add(escaped); return;
            case (byte)'\n': return;
            case (byte)'\r':
                if (_position < source.Length && source[_position] == (byte)'\n')
                    _position++;
                return;
        }

        if (escaped is >= (byte)'0' and <= (byte)'7')
        {
            int value = escaped - (byte)'0';
            int digits = 1;
            while (digits < 3 && _position < source.Length
                   && source[_position] is >= (byte)'0' and <= (byte)'7')
            {
                value = (value << 3) + source[_position] - (byte)'0';
                _position++;
                digits++;
            }

            decoded.Add((byte)value);
            return;
        }

        // For an unrecognized escape the backslash is ignored and the character is retained.
        decoded.Add(escaped);
    }

    private PdfToken ReadLessThan(int start)
    {
        ReadOnlySpan<byte> source = _source.Span;
        if (_position < source.Length && source[_position] == (byte)'<')
        {
            _position++;
            return Token(PdfTokenKind.DictionaryStart, start, 2);
        }

        var decoded = new List<byte>();
        int? high = null;
        while (_position < source.Length)
        {
            byte current = source[_position++];
            if (current == (byte)'>')
            {
                if (high.HasValue)
                    decoded.Add((byte)(high.Value << 4));
                return Token(PdfTokenKind.HexString, start, _position - start, decoded.ToArray());
            }

            if (IsWhitespace(current))
                continue;

            if (!TryHex(current, out int nibble))
                throw Error("A hexadecimal string contains a non-hexadecimal character", _position - 1);

            if (high.HasValue)
            {
                decoded.Add((byte)((high.Value << 4) | nibble));
                high = null;
            }
            else
            {
                high = nibble;
            }
        }

        throw Error("Unterminated hexadecimal string", start);
    }

    private PdfToken ReadGreaterThan(int start)
    {
        ReadOnlySpan<byte> source = _source.Span;
        if (_position < source.Length && source[_position] == (byte)'>')
        {
            _position++;
            return Token(PdfTokenKind.DictionaryEnd, start, 2);
        }
        if (CompatibilityRecovery)
            return Token(PdfTokenKind.DictionaryEnd, start, 1);

        throw Error("A single greater-than sign is not a valid PDF token", start);
    }

    /// <summary>
    /// Gets or sets whether a lone greater-than sign closes a dictionary, as mainstream
    /// viewers accept.
    /// </summary>
    internal bool CompatibilityRecovery { get; set; }

    private PdfToken ReadRegular(int start)
    {
        ReadOnlySpan<byte> source = _source.Span;
        while (_position < source.Length && !IsWhitespace(source[_position]) && !IsDelimiter(source[_position]))
            _position++;

        int length = _position - start;
        ReadOnlySpan<byte> raw = source.Slice(start, length);
        if (raw.SequenceEqual("true"u8) || raw.SequenceEqual("false"u8))
            return Token(PdfTokenKind.Boolean, start, length, _source.Slice(start, length));
        if (raw.SequenceEqual("null"u8))
            return Token(PdfTokenKind.Null, start, length);

        if (IsNumberCandidate(raw[0]))
        {
            PdfTokenKind kind = ClassifyNumber(raw, start);
            return Token(kind, start, length, _source.Slice(start, length));
        }

        return Token(PdfTokenKind.Keyword, start, length, _source.Slice(start, length));
    }

    private static PdfTokenKind ClassifyNumber(ReadOnlySpan<byte> raw, int offset)
    {
        int position = raw[0] is (byte)'+' or (byte)'-' ? 1 : 0;
        bool hasDigit = false;
        bool hasDecimalPoint = false;

        for (; position < raw.Length; position++)
        {
            byte current = raw[position];
            if (current is >= (byte)'0' and <= (byte)'9')
            {
                hasDigit = true;
                continue;
            }

            if (current == (byte)'.' && !hasDecimalPoint)
            {
                hasDecimalPoint = true;
                continue;
            }

            throw new PdfSyntaxException("Malformed numeric token", offset);
        }

        if (!hasDigit)
            throw new PdfSyntaxException("Malformed numeric token", offset);

        return hasDecimalPoint ? PdfTokenKind.Real : PdfTokenKind.Integer;
    }

    private void SkipTrivia()
    {
        ReadOnlySpan<byte> source = _source.Span;
        while (_position < source.Length)
        {
            if (IsWhitespace(source[_position]))
            {
                _position++;
                continue;
            }

            if (source[_position] != (byte)'%')
                return;

            _position++;
            while (_position < source.Length && source[_position] is not (byte)'\r' and not (byte)'\n')
                _position++;
        }
    }

    private static PdfToken Token(PdfTokenKind kind, int offset, int length, ReadOnlyMemory<byte> value = default) =>
        new(kind, offset, length, value);

    private static PdfSyntaxException Error(string message, int offset) => new(message, offset);

    private static bool IsNumberCandidate(byte value) =>
        value is (byte)'+' or (byte)'-' or (byte)'.' or >= (byte)'0' and <= (byte)'9';

    private static bool IsWhitespace(byte value) =>
        value is 0x00 or 0x09 or 0x0A or 0x0C or 0x0D or 0x20;

    private static bool IsDelimiter(byte value) => value is
        (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or
        (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or
        (byte)'/' or (byte)'%';

    private static bool TryHex(byte value, out int nibble)
    {
        if (value is >= (byte)'0' and <= (byte)'9')
        {
            nibble = value - (byte)'0';
            return true;
        }

        if (value is >= (byte)'A' and <= (byte)'F')
        {
            nibble = value - (byte)'A' + 10;
            return true;
        }

        if (value is >= (byte)'a' and <= (byte)'f')
        {
            nibble = value - (byte)'a' + 10;
            return true;
        }

        nibble = 0;
        return false;
    }
}
