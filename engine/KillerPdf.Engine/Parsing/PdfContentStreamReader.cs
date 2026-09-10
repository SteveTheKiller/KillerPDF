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
    /// <param name="compatibilityRecovery">Allows bounded recovery for malformed content accepted by common viewers.</param>
    /// <returns>Instructions in source order. No partial result is returned on failure.</returns>
    public static IReadOnlyList<PdfContentInstruction> Read(
        ReadOnlyMemory<byte> source,
        int maximumInstructions = 1_000_000,
        int maximumOperands = 4096,
        Func<PdfName, int?>? resolveColorComponents = null,
        CancellationToken cancellationToken = default,
        bool compatibilityRecovery = false)
        => ReadCore(source, true, out _, maximumInstructions, maximumOperands,
            resolveColorComponents, cancellationToken, compatibilityRecovery, true);

    // A non-final buffer commits only complete instructions. Its unconsumed suffix
    // must be retained verbatim, including comments and pending operands.
    internal static IReadOnlyList<PdfContentInstruction> ReadPrefix(
        ReadOnlyMemory<byte> source, bool isFinal, out int consumed,
        int maximumInstructions = 1_000_000, int maximumOperands = 4096,
        Func<PdfName, int?>? resolveColorComponents = null,
        CancellationToken cancellationToken = default,
        bool compatibilityRecovery = false)
        => ReadCore(source, isFinal, out consumed, maximumInstructions, maximumOperands,
            resolveColorComponents, cancellationToken, compatibilityRecovery, false);

    internal static IEnumerable<PdfContentInstruction> Enumerate(
        Stream source, int initialBufferBytes = 64 * 1024,
        int maximumBufferedBytes = MaximumSourceBytes,
        int maximumInstructions = 1_000_000,
        Func<PdfName, int?>? resolveColorComponents = null,
        CancellationToken cancellationToken = default,
        bool compatibilityRecovery = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(initialBufferBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInstructions);
        if (maximumBufferedBytes < initialBufferBytes || maximumBufferedBytes > MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumBufferedBytes));
        byte[] buffer = new byte[initialBufferBytes];
        int count = 0, offset = 0, instructionCount = 0;
        bool final = false;
        while (true)
        {
            while (count < buffer.Length && !final)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = source.Read(buffer, count, buffer.Length - count);
                if (read == 0) final = true;
                count += read;
            }

            var instructions = ReadPrefix(buffer.AsMemory(0, count), final, out int consumed,
                resolveColorComponents: resolveColorComponents,
                cancellationToken: cancellationToken,
                compatibilityRecovery: compatibilityRecovery);
            foreach (var instruction in instructions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++instructionCount > maximumInstructions)
                    throw new PdfSyntaxException("Content instruction limit exceeded", offset);
                yield return offset == 0 ? instruction
                    : instruction.WithOffset(checked(offset + instruction.Offset));
            }
            if (final) yield break;
            if (consumed > 0)
            {
                offset = checked(offset + consumed);
                count -= consumed;
                Buffer.BlockCopy(buffer, consumed, buffer, 0, count);
            }
            else
            {
                if (buffer.Length == maximumBufferedBytes)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (source.ReadByte() == -1)
                    {
                        final = true;
                        continue;
                    }
                    throw new PdfSyntaxException("Content instruction exceeds the buffered size limit", offset);
                }
                Array.Resize(ref buffer, Math.Min(maximumBufferedBytes, checked(buffer.Length * 2)));
            }
        }
    }

    private static IReadOnlyList<PdfContentInstruction> ReadCore(
        ReadOnlyMemory<byte> source, bool isFinal, out int consumed,
        int maximumInstructions, int maximumOperands,
        Func<PdfName, int?>? resolveColorComponents,
        CancellationToken cancellationToken, bool compatibilityRecovery, bool allowInstructionTruncation)
    {
        consumed = 0;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumInstructions);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOperands);
        if (source.Length > MaximumSourceBytes)
            throw new ArgumentOutOfRangeException(nameof(source), "Decoded content exceeds the size limit.");

        var parser = PdfObjectParser.ForContent(source, compatibilityRecovery && isFinal);
        var instructions = new List<PdfContentInstruction>();
        var operands = new List<PdfObject>();
        int recoveries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PdfToken token;
            try
            {
                token = parser.PeekContentToken();
            }
            catch (PdfSyntaxException) when (!isFinal)
            {
                return instructions.AsReadOnly();
            }
            catch (PdfSyntaxException error) when (compatibilityRecovery)
            {
                Resynchronize(error.Offset);
                continue;
            }
            if (token.Kind == PdfTokenKind.EndOfInput)
            {
                if (!isFinal) return instructions.AsReadOnly();
                if (operands.Count != 0 && !compatibilityRecovery)
                    throw new PdfSyntaxException("Content ends with operands but no operator", token.Offset);
                consumed = source.Length;
                return instructions.AsReadOnly();
            }

            if (!isFinal && token.Offset + token.Length == source.Length)
                return instructions.AsReadOnly();

            if (instructions.Count >= maximumInstructions)
            {
                if (compatibilityRecovery && allowInstructionTruncation) return instructions.AsReadOnly();
                throw new PdfSyntaxException("Content instruction limit exceeded", token.Offset);
            }

            if (token.Kind != PdfTokenKind.Keyword)
            {
                if (operands.Count >= maximumOperands)
                    throw new PdfSyntaxException("Content operand limit exceeded", token.Offset);
                try
                {
                    operands.Add(parser.ParseObject());
                }
                catch (PdfSyntaxException) when (!isFinal)
                {
                    return instructions.AsReadOnly();
                }
                catch (PdfSyntaxException error) when (compatibilityRecovery)
                {
                    Resynchronize(Math.Max(error.Offset, token.Offset));
                }
                continue;
            }

            string operation = parser.TakeContentToken().ValueAsLatin1();
            if (operation == "BI")
            {
                if (operands.Count != 0)
                {
                    if (!compatibilityRecovery)
                        throw new PdfSyntaxException("BI cannot follow operands", token.Offset);
                    operands.Clear();
                }
                try
                {
                    var image = PdfInlineImageReader.Read(parser, source, token.Offset,
                        maximumOperands, resolveColorComponents, cancellationToken,
                        compatibilityRecovery);
                    if (!isFinal && parser.ContentPosition == source.Length)
                        return instructions.AsReadOnly();
                    instructions.Add(image);
                    consumed = parser.ContentPosition;
                }
                catch (PdfSyntaxException) when (!isFinal)
                {
                    return instructions.AsReadOnly();
                }
                catch (PdfSyntaxException error) when (compatibilityRecovery)
                {
                    Resynchronize(Math.Max(error.Offset, token.Offset));
                }
                continue;
            }
            if (operation is "R" or "obj" or "endobj" or "stream" or "endstream" or "ID" or "EI")
            {
                if (!compatibilityRecovery)
                    throw new PdfSyntaxException("Object or inline-image syntax is invalid here", token.Offset);
                continue;
            }

            instructions.Add(new PdfContentInstruction(operation, token.Offset, operands));
            consumed = token.Offset + token.Length;
            operands.Clear();
        }

        // Common viewers skip a malformed token and keep interpreting the rest of the
        // content. Drop the pending operands and continue after the next delimiter.
        void Resynchronize(int failureOffset)
        {
            if (++recoveries > 10_000)
                throw new PdfSyntaxException("Content recovery limit exceeded", failureOffset);
            ReadOnlySpan<byte> bytes = source.Span;
            int position = Math.Clamp(failureOffset, 0, bytes.Length);
            if (position < bytes.Length) position++;
            while (position < bytes.Length && !IsDelimiterOrWhitespace(bytes[position]))
                position++;
            operands.Clear();
            parser.SetContentPosition(position);
        }

        static bool IsDelimiterOrWhitespace(byte value) =>
            value is 0 or 9 or 10 or 12 or 13 or 32
                or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']'
                or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';
    }
}
