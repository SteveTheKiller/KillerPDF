using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Writing;

namespace KillerPdf.Engine.Parsing;

/// <summary>Writes decoded PDF content instructions while retaining unknown operators.</summary>
public static class PdfContentStreamWriter
{
    /// <summary>Serializes instructions as one decoded page-content stream.</summary>
    public static byte[] Write(IEnumerable<PdfContentInstruction> instructions)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        using var output = new MemoryStream();
        foreach (PdfContentInstruction instruction in instructions)
        {
            ArgumentNullException.ThrowIfNull(instruction);
            if (instruction.Operator == "BI")
            {
                WriteInlineImage(output, instruction);
                continue;
            }
            foreach (PdfObject operand in instruction.Operands)
            {
                output.Write(PdfObjectWriter.Write(operand));
                output.WriteByte((byte)' ');
            }
            WriteOperator(output, instruction.Operator);
            output.WriteByte((byte)'\n');
        }
        if (output.Length > PdfContentStreamReader.MaximumSourceBytes)
            throw new InvalidOperationException("Written page content exceeds the size limit.");
        return output.ToArray();
    }

    private static void WriteInlineImage(Stream output, PdfContentInstruction instruction)
    {
        if (instruction.Operands.Count != 1 || instruction.Operands[0] is not PdfDictionary dictionary
            || instruction.InlineImageData is not ReadOnlyMemory<byte> data)
            throw new InvalidOperationException("An inline-image instruction requires one dictionary and image data.");
        output.Write("BI\n"u8);
        foreach (KeyValuePair<PdfName, PdfObject> entry in dictionary)
        {
            output.Write(PdfObjectWriter.Write(entry.Key));
            output.WriteByte((byte)' ');
            output.Write(PdfObjectWriter.Write(entry.Value));
            output.WriteByte((byte)'\n');
        }
        output.Write("ID\n"u8);
        output.Write(data.Span);
        output.Write("\nEI\n"u8);
    }

    private static void WriteOperator(Stream output, string operation)
    {
        if (operation.Any(value => value <= 0x20 || value >= 0x7f
            || value is '(' or ')' or '<' or '>' or '[' or ']' or '{' or '}' or '/' or '%'))
            throw new InvalidOperationException("A content operator contains an invalid character.");
        output.Write(Encoding.ASCII.GetBytes(operation));
    }
}
