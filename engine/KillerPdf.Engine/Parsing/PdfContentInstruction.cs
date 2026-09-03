using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Parsing;

/// <summary>An operator and its direct operands in a decoded page-content stream.</summary>
public sealed class PdfContentInstruction
{
    internal PdfContentInstruction(string operation, int offset, IEnumerable<PdfObject> operands)
    {
        Operator = operation;
        Offset = offset;
        Operands = Array.AsReadOnly(operands.ToArray());
    }

    /// <summary>Gets the case-sensitive PDF operator name.</summary>
    public string Operator { get; }

    /// <summary>Gets the operator's byte offset within the decoded source.</summary>
    public int Offset { get; }

    /// <summary>Gets the immutable operand list in source order.</summary>
    public IReadOnlyList<PdfObject> Operands { get; }
}
