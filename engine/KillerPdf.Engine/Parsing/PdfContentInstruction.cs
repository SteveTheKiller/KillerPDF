using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>An operator and its direct operands in a decoded page-content stream.</summary>
public sealed class PdfContentInstruction
{
    private static readonly IReadOnlyList<PdfObject> EmptyOperands =
        Array.AsReadOnly(Array.Empty<PdfObject>());

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
        PdfObject[] values = operands.ToArray();
        Operands = values.Length == 0 ? EmptyOperands : Array.AsReadOnly(values);
        if (inlineImageData.HasValue)
            InlineImageData = new ReadOnlyMemory<byte>(inlineImageData.Value.ToArray());
    }

    private PdfContentInstruction(PdfContentInstruction source, int offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        Operator = source.Operator;
        Offset = offset;
        Operands = source.Operands;
        InlineImageData = source.InlineImageData;
    }

    internal PdfContentInstruction WithOffset(int offset) => new(this, offset);

    /// <summary>Gets the case-sensitive PDF operator name.</summary>
    public string Operator { get; }

    /// <summary>Gets the operator's byte offset within the decoded source.</summary>
    public int Offset { get; }

    /// <summary>Gets the immutable operand list in source order.</summary>
    public IReadOnlyList<PdfObject> Operands { get; }

    /// <summary>Gets encoded inline-image bytes for BI, or null for another operator.</summary>
    public ReadOnlyMemory<byte>? InlineImageData { get; }
}
