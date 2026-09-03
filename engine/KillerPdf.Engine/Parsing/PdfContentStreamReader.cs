using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Parsing;

/// <summary>Reads instructions from decoded PDF content without interpreting graphics or text.</summary>
/// <remarks>
/// Inline images use exact sample lengths or encoded end markers. Unknown operators
/// are retained for the interpreter to handle, including compatibility sections. This reader
/// does not resolve fonts, validate operator arity, or extract Unicode text.
/// </remarks>
public static class PdfContentStreamReader
{
    /// <summary>Maximum decoded source size accepted by this reader.</summary>
    public const int MaximumSourceBytes = 64 * 1024 * 1024;

    /// <summary>Reads a complete decoded stream, rejecting unfinished operands and invalid syntax.</summary>
    /// <param name="source">Decoded content bytes, not a PDF file or compressed stream.</param>
    /// <param name="maximumInstructions">Maximum number of instructions to collect.</param>
    /// <param name="maximumOperands">Maximum direct operands preceding any one operator.</param>
    /// <param name="resolveColorComponents">Resolves component counts for resource-named inline image color spaces.</param>
    /// <param name="cancellationToken">Cancellation checked between operands and instructions.</param>
    /// <returns>Instructions in source order. No partial result is returned on failure.</returns>
    public static IReadOnlyList<PdfContentInstruction> Read(
        ReadOnlyMemory<byte> source,
        int maximumInstructions = 1_000_000,
        int maximumOperands = 4096,
        Func<PdfName, int?>? resolveColorComponents = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInstructions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperands);
        if (source.Length > MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(source), "Decoded content exceeds the size limit.");

        var parser = PdfObjectParser.ForContent(source);
        var instructions = new List<PdfContentInstruction>();
        var operands = new List<PdfObject>();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfToken token = parser.PeekContentToken();
            if (token.Kind == PdfTokenKind.EndOfInput)
            {
                if (operands.Count != 0)
                    throw new PdfSyntaxException("Content ends with operands but no operator", token.Offset);
                return instructions.AsReadOnly();
            }

            if (instructions.Count >= maximumInstructions)
                throw new PdfSyntaxException("Content instruction limit exceeded", token.Offset);

            if (token.Kind != PdfTokenKind.Keyword)
            {
                if (operands.Count >= maximumOperands)
                    throw new PdfSyntaxException("Content operand limit exceeded", token.Offset);
                operands.Add(parser.ParseObject());
                continue;
            }

            string operation = parser.TakeContentToken().ValueAsLatin1();
            if (operation == "BI")
            {
                if (operands.Count != 0)
                    throw new PdfSyntaxException("BI cannot follow operands", token.Offset);
                instructions.Add(PdfInlineImageReader.Read(parser, source, token.Offset,
                    maximumOperands, resolveColorComponents, cancellationToken));
                continue;
            }
            if (operation is "R" or "obj" or "endobj" or "stream" or "endstream" or "ID" or "EI")
                throw new PdfSyntaxException("Object or inline-image syntax is invalid here", token.Offset);

            instructions.Add(new PdfContentInstruction(operation, token.Offset, operands));
            operands.Clear();
        }
    }
}
