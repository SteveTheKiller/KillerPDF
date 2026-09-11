using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>An operator and its direct operands in a decoded page-content stream.</summary>
public sealed class PdfContentInstruction : IReadOnlyList<PdfObject>
{
    private readonly object _operandStorage;
    private readonly byte _integerMask;

    /// <summary>Creates an instruction for inspection or content-stream rewriting.</summary>
    public PdfContentInstruction(string operation, int offset, IEnumerable<PdfObject> operands,
        ReadOnlyMemory<byte>? inlineImageData = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(operands);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (operation == "BI" && !inlineImageData.HasValue)
            throw new ArgumentException("An inline-image instruction requires image data.", nameof(inlineImageData));
        if (operation != "BI" && inlineImageData.HasValue)
            throw new ArgumentException("Only an inline-image instruction can contain image data.", nameof(inlineImageData));
        Operator = operation;
        Offset = offset;
        if (UsesCompactNumbers(operation) && operands is IReadOnlyList<PdfObject> source
            && source.Count is >= 1 and <= 6 && TryCompact(source, out double[]? numbers, out byte integerMask))
        {
            _operandStorage = numbers;
            _integerMask = integerMask;
        }
        else
        {
            _operandStorage = operands.ToArray();
        }
        if (inlineImageData.HasValue)
            InlineImageData = new ReadOnlyMemory<byte>(inlineImageData.Value.ToArray());
    }

    private PdfContentInstruction(PdfContentInstruction source, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Operator = source.Operator;
        Offset = offset;
        _operandStorage = source._operandStorage;
        _integerMask = source._integerMask;
        InlineImageData = source.InlineImageData;
    }

    private PdfContentInstruction(string operation, int offset,
        IReadOnlyList<PdfContentNumber> operands)
    {
        Operator = operation;
        Offset = offset;
        var numbers = GC.AllocateUninitializedArray<double>(operands.Count);
        for (int index = 0; index < operands.Count; index++)
        {
            PdfContentNumber operand = operands[index];
            numbers[index] = operand.Value;
            if (operand.IsInteger) _integerMask |= (byte)(1 << index);
        }
        _operandStorage = numbers;
    }

    internal PdfContentInstruction WithOffset(int offset) => new(this, offset);

    internal static bool TryCreateCompact(string operation, int offset,
        IReadOnlyList<PdfContentNumber> operands, out PdfContentInstruction? instruction)
    {
        if (!UsesCompactNumbers(operation) || operands.Count is < 1 or > 6
            || operands.Any(value => value.IsInteger
                && value.Integer is < -9_007_199_254_740_992 or > 9_007_199_254_740_992))
        {
            instruction = null;
            return false;
        }
        instruction = new PdfContentInstruction(operation, offset, operands);
        return true;
    }

    /// <summary>Gets the case-sensitive PDF operator name.</summary>
    public string Operator { get; }

    /// <summary>Gets the operator's byte offset within the decoded source.</summary>
    public int Offset { get; }

    /// <summary>Gets the immutable operand list in source order.</summary>
    public IReadOnlyList<PdfObject> Operands => this;

    int IReadOnlyCollection<PdfObject>.Count => _operandStorage switch
    {
        PdfObject[] values => values.Length,
        double[] values => values.Length,
        _ => 0
    };

    PdfObject IReadOnlyList<PdfObject>.this[int index] => _operandStorage is PdfObject[] values
        ? values[index]
        : ((_integerMask & 1 << index) != 0
            ? new PdfInteger((long)((double[])_operandStorage)[index])
            : new PdfReal(((double[])_operandStorage)[index]));

    IEnumerator<PdfObject> IEnumerable<PdfObject>.GetEnumerator()
    {
        IReadOnlyList<PdfObject> values = this;
        for (int index = 0; index < values.Count; index++) yield return values[index];
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        ((IEnumerable<PdfObject>)this).GetEnumerator();

    internal bool TryGetNumber(int index, out double value)
    {
        if (_operandStorage is double[] numbers && (uint)index < (uint)numbers.Length)
        {
            value = numbers[index];
            return true;
        }
        value = 0;
        return false;
    }

    private static bool TryCompact(IReadOnlyList<PdfObject> source,
        out double[] numbers, out byte integerMask)
    {
        numbers = GC.AllocateUninitializedArray<double>(source.Count);
        integerMask = 0;
        for (int index = 0; index < source.Count; index++)
        {
            if (source[index] is PdfInteger integer
                && integer.Value is >= -9_007_199_254_740_992 and <= 9_007_199_254_740_992)
            {
                numbers[index] = integer.Value;
                integerMask |= (byte)(1 << index);
            }
            else if (source[index] is PdfReal real) numbers[index] = real.Value;
            else return false;
        }
        return true;
    }

    private static bool UsesCompactNumbers(string operation) => operation is
        "cm" or "w" or "J" or "j" or "M" or "i"
        or "m" or "l" or "c" or "v" or "y" or "re"
        or "Tc" or "Tw" or "Tz" or "TL" or "Tr" or "Ts"
        or "Td" or "TD" or "Tm" or "d0" or "d1";

    /// <summary>Gets encoded inline-image bytes for BI, or null for another operator.</summary>
    public ReadOnlyMemory<byte>? InlineImageData { get; }
}

internal readonly record struct PdfContentNumber(double Value, long Integer, bool IsInteger)
{
    internal PdfObject ToObject() => IsInteger ? new PdfInteger(Integer) : new PdfReal(Value);
}
