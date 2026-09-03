using System.Text;
using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Syntax;

namespace KillerPdf.Engine.Parsing;

// T.4 run codes and T.6 row modes delimit the data without searching compressed bytes for EI.
internal static class PdfInlineFaxBoundary
{
    private static readonly Dictionary<int, int> White = Codes(
        "00110101 000111 0111 1000 1011 1100 1110 1111 10011 10100 00111 01000 001000 000011 110100 110101 101010 101011 0100111 0001100 0001000 0010111 0000011 0000100 0101000 0101011 0010011 0100100 0011000 00000010 00000011 00011010 00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000 00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010 00001011 01010010 01010011 01010100 01010101 00100100 00100101 01011000 01011001 01011010 01011011 01001010 01001011 00110010 00110011 00110100",
        "11011 10010 010111 0110111 00110110 00110111 01100100 01100101 01101000 01100111 011001100 011001101 011010010 011010011 011010100 011010101 011010110 011010111 011011000 011011001 011011010 011011011 010011000 010011001 010011010 011000 010011011");
    private static readonly Dictionary<int, int> Black = Codes(
        "0000110111 010 11 10 011 0011 0010 00011 000101 000100 0000100 0000101 0000111 00000100 00000111 000011000 0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000 00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001 000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111 000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111 000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111 000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111",
        "0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100 0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 0000001110100 0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 0000001010101 0000001011010 0000001011011 0000001100100 0000001100101");

    internal static int Find(ReadOnlyMemory<byte> source, PdfDictionary? parameters, CancellationToken cancellationToken)
    {
        int columns = Integer(parameters, "Columns", 1728);
        int rows = Integer(parameters, "Rows", 0);
        int k = Integer(parameters, "K", 0);
        bool endOfBlock = Boolean(parameters, "EndOfBlock", true);
        bool endOfLine = Boolean(parameters, "EndOfLine", false);
        bool aligned = Boolean(parameters, "EncodedByteAlign", false);
        if (columns is <= 0 or > 1_048_576 || rows < 0 || (!endOfBlock && rows == 0))
            throw new NotSupportedException("Inline CCITT requires bounded Columns and Rows or an end-of-block marker.");
        if (Integer(parameters, "DamagedRowsBeforeError", 0) != 0)
            throw new NotSupportedException("Damaged CCITT rows cannot provide a reliable inline image boundary.");
        var bits = new Bits(source, cancellationToken);
        var reference = new bool[columns];
        var current = new bool[columns];
        int row = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (endOfBlock && TryEnd(bits, k)) return bits.FinishByte();
            if (!endOfBlock && row == rows) return bits.FinishByte();
            if ((long)(row + 1) * columns > (long)PdfContentStreamReader.MaximumSourceBytes * 8)
                throw new PdfSyntaxException("Inline CCITT exceeds the decoded size limit", bits.Offset);
            bool oneDimensional = k == 0;
            if (k >= 0)
            {
                bool eol = bits.TryEol();
                if ((endOfLine || k > 0) && !eol)
                    throw new PdfSyntaxException("CCITT row requires EOL", bits.Offset);
                if (k > 0) oneDimensional = bits.Read() == 1;
            }
            Array.Clear(current);
            if (oneDimensional) ReadOneDimensional(bits, current);
            else ReadTwoDimensional(bits, reference, current);
            (current, reference) = (reference, current);
            row++;
            if (aligned) bits.Align();
        }
    }

    private static void ReadOneDimensional(Bits bits, bool[] row)
    {
        int x = 0, operations = 0;
        bool black = false;
        while (x < row.Length)
        {
            if (++operations > row.Length * 2 + 2) throw new PdfSyntaxException("CCITT row has excessive zero runs", bits.Offset);
            int run = Run(bits, black, row.Length - x);
            if (black) Array.Fill(row, true, x, run);
            x += run;
            black = !black;
        }
    }

    private static void ReadTwoDimensional(Bits bits, bool[] reference, bool[] row)
    {
        int x = 0, operations = 0;
        bool black = false, initial = true;
        while (x < row.Length)
        {
            if (++operations > row.Length * 2 + 2) throw new PdfSyntaxException("CCITT row has excessive transitions", bits.Offset);
            int mode = Mode(bits);
            int end;
            if (mode == 10)
            {
                int first = Run(bits, black, row.Length - x);
                int second = Run(bits, !black, row.Length - x - first);
                if (first + second == 0) throw new PdfSyntaxException("CCITT horizontal mode makes no progress", bits.Offset);
                if (black) Array.Fill(row, true, x, first);
                else Array.Fill(row, true, x + first, second);
                x += first + second;
                initial = false;
                continue;
            }
            int b1 = row.Length;
            for (int i = initial ? x : x + 1; i < reference.Length; i++)
            {
                bool before = i > 0 && reference[i - 1];
                if (before != reference[i] && reference[i] != black) { b1 = i; break; }
            }
            if (mode == 11)
            {
                end = row.Length;
                for (int i = b1 + 1; i < reference.Length; i++)
                    if (reference[i] != reference[i - 1]) { end = i; break; }
                if (end <= x) throw new PdfSyntaxException("CCITT pass mode makes no progress", bits.Offset);
            }
            else end = b1 + mode;
            if (end < x || end > row.Length)
                throw new PdfSyntaxException("CCITT vertical transition is outside the row", bits.Offset);
            if (black) Array.Fill(row, true, x, end - x);
            x = end;
            if (mode != 11) black = !black;
            initial = false;
        }
    }

    private static int Mode(Bits bits)
    {
        int code = 1;
        for (int length = 1; length <= 7; length++)
        {
            code = (code << 1) | bits.Read();
            int mode = code switch { 3 => 0, 11 => 1, 10 => -1, 9 => 10, 17 => 11,
                67 => 2, 66 => -2, 131 => 3, 130 => -3, _ => int.MinValue };
            if (mode != int.MinValue) return mode;
        }
        throw new NotSupportedException("CCITT uncompressed or invalid two-dimensional mode is not supported.");
    }

    private static int Run(Bits bits, bool black, int remaining)
    {
        var table = black ? Black : White;
        int total = 0;
        while (true)
        {
            int code = 1, run = -1;
            for (int length = 1; length <= 13; length++)
            {
                code = (code << 1) | bits.Read();
                if (table.TryGetValue(code, out run)) break;
                run = -1;
            }
            if (run < 0) throw new PdfSyntaxException("Invalid CCITT run code", bits.Offset);
            total += run;
            if (total > remaining) throw new PdfSyntaxException("CCITT run exceeds row width", bits.Offset);
            if (run < 64) return total;
        }
    }

    private static bool TryEnd(Bits bits, int k)
    {
        int start = bits.Position;
        int markers = k < 0 ? 2 : 6;
        for (int i = 0; i < markers; i++)
        {
            if (!bits.TryEol() || (k > 0 && bits.Read() != 1)) { bits.Position = start; return false; }
        }
        return true;
    }

    private static Dictionary<int, int> Codes(string terminating, string makeup)
    {
        var result = new Dictionary<int, int>();
        Add(terminating, 0, 1);
        Add(makeup, 64, 64);
        Add("00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110 000000010111 000000011100 000000011101 000000011110 000000011111", 1792, 64);
        return result;
        void Add(string codes, int value, int step)
        {
            foreach (string code in codes.Split(' '))
            {
                result.Add((1 << code.Length) | Convert.ToInt32(code, 2), value);
                value += step;
            }
        }
    }

    private static int Integer(PdfDictionary? dictionary, string key, int fallback) =>
        dictionary?.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out var value) == true
            ? value is PdfInteger { Value: >= int.MinValue and <= int.MaxValue } integer ? (int)integer.Value
                : throw new PdfSyntaxException($"CCITT {key} must be an integer", 0) : fallback;
    private static bool Boolean(PdfDictionary? dictionary, string key, bool fallback) =>
        dictionary?.TryGetValue(new PdfName(Encoding.ASCII.GetBytes(key)), out var value) == true
            ? value is PdfBoolean boolean ? boolean.Value
                : throw new PdfSyntaxException($"CCITT {key} must be boolean", 0) : fallback;

    private sealed class Bits(ReadOnlyMemory<byte> source, CancellationToken cancellationToken)
    {
        internal int Position { get; set; }
        internal int Offset => Position / 8;
        internal int Read()
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position >= source.Length * 8) throw new PdfSyntaxException("Truncated inline CCITT data", Offset);
            int value = (source.Span[Position / 8] >> (7 - Position % 8)) & 1;
            Position++;
            return value;
        }
        internal bool TryEol()
        {
            int start = Position, zeros = 0;
            while (Position < source.Length * 8)
            {
                if (Read() == 0) { zeros++; continue; }
                if (zeros >= 11) return true;
                break;
            }
            Position = start;
            return false;
        }
        internal void Align()
        {
            while (Position % 8 != 0)
                if (Read() != 0) throw new PdfSyntaxException("CCITT padding must be zero", Offset);
        }
        internal int FinishByte() { Align(); return Offset; }
    }
}
