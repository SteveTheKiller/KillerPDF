using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>An operator and its direct operands in a decoded page-content stream.</summary>
public sealed class PdfContentInstruction : IReadOnlyList<PdfObject>
{
    private readonly PdfObject[] _operands;

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
        _operands = operands.ToArray();
        if (inlineImageData.HasValue)
            InlineImageData = new ReadOnlyMemory<byte>(inlineImageData.Value.ToArray());
    }

    private PdfContentInstruction(PdfContentInstruction source, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Operator = source.Operator;
        Offset = offset;
        _operands = source._operands;
        InlineImageData = source.InlineImageData;
    }

    internal PdfContentInstruction WithOffset(int offset) => new(this, offset);

    /// <summary>Gets the case-sensitive PDF operator name.</summary>
    public string Operator { get; }

    /// <summary>Gets the operator's byte offset within the decoded source.</summary>
    public int Offset { get; }

    /// <summary>Gets the immutable operand list in source order.</summary>
    public IReadOnlyList<PdfObject> Operands => this;

    int IReadOnlyCollection<PdfObject>.Count => _operands.Length;

    PdfObject IReadOnlyList<PdfObject>.this[int index] => _operands[index];

    IEnumerator<PdfObject> IEnumerable<PdfObject>.GetEnumerator() =>
        ((IEnumerable<PdfObject>)_operands).GetEnumerator();

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() =>
        _operands.GetEnumerator();

    /// <summary>Gets encoded inline-image bytes for BI, or null for another operator.</summary>
    public ReadOnlyMemory<byte>? InlineImageData { get; }
}
