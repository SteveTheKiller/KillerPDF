namespace KillerPdf.Engine.Authoring;

// Encodes T.6 rows with pass, vertical, and horizontal modes. The output ends with EOFB.
internal static class PdfCcittGroup4Encoder
{
    private static readonly string[] WhiteTerminating =
        "00110101 000111 0111 1000 1011 1100 1110 1111 10011 10100 00111 01000 001000 000011 110100 110101 101010 101011 0100111 0001100 0001000 0010111 0000011 0000100 0101000 0101011 0010011 0100100 0011000 00000010 00000011 00011010 00011011 00010010 00010011 00010100 00010101 00010110 00010111 00101000 00101001 00101010 00101011 00101100 00101101 00000100 00000101 00001010 00001011 01010010 01010011 01010100 01010101 00100100 00100101 01011000 01011001 01011010 01011011 01001010 01001011 00110010 00110011 00110100".Split(' ');
    private static readonly string[] BlackTerminating =
        "0000110111 010 11 10 011 0011 0010 00011 000101 000100 0000100 0000101 0000111 00000100 00000111 000011000 0000010111 0000011000 0000001000 00001100111 00001101000 00001101100 00000110111 00000101000 00000010111 00000011000 000011001010 000011001011 000011001100 000011001101 000001101000 000001101001 000001101010 000001101011 000011010010 000011010011 000011010100 000011010101 000011010110 000011010111 000001101100 000001101101 000011011010 000011011011 000001010100 000001010101 000001010110 000001010111 000001100100 000001100101 000001010010 000001010011 000000100100 000000110111 000000111000 000000100111 000000101000 000001011000 000001011001 000000101011 000000101100 000001011010 000001100110 000001100111".Split(' ');
    private static readonly string[] WhiteMakeup =
        "11011 10010 010111 0110111 00110110 00110111 01100100 01100101 01101000 01100111 011001100 011001101 011010010 011010011 011010100 011010101 011010110 011010111 011011000 011011001 011011010 011011011 010011000 010011001 010011010 011000 010011011 00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110 000000010111 000000011100 000000011101 000000011110 000000011111".Split(' ');
    private static readonly string[] BlackMakeup =
        "0000001111 000011001000 000011001001 000001011011 000000110011 000000110100 000000110101 0000001101100 0000001101101 0000001001010 0000001001011 0000001001100 0000001001101 0000001110010 0000001110011 0000001110100 0000001110101 0000001110110 0000001110111 0000001010010 0000001010011 0000001010100 0000001010101 0000001011010 0000001011011 0000001100100 0000001100101 00000001000 00000001100 00000001101 000000010010 000000010011 000000010100 000000010101 000000010110 000000010111 000000011100 000000011101 000000011110 000000011111".Split(' ');

    internal static byte[] Encode(int width, int height, ReadOnlySpan<byte> pixels)
    {
        var bits = new BitWriter();
        var reference = new bool[width];
        var current = new bool[width];
        for (int y = 0; y < height; y++)
        {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++) current[x] = pixels[rowOffset + x] < 128;
            EncodeRow(bits, current, reference);
            (current, reference) = (reference, current);
        }
        bits.Write("000000000001000000000001");
        return bits.ToArray();
    }

    private static void EncodeRow(BitWriter bits, bool[] row, bool[] reference)
    {
        int a0 = 0;
        bool black = false;
        bool initial = true;
        while (a0 < row.Length)
        {
            int a1 = Change(row, a0, black);
            int b1 = ReferenceChange(reference, a0, black, initial);
            int b2 = Change(reference, Math.Min(b1 + 1, reference.Length), !black);
            if (b2 < a1)
            {
                bits.Write("0001");
                a0 = b2;
            }
            else
            {
                int delta = a1 - b1;
                if (delta is >= -3 and <= 3)
                {
                    bits.Write(delta switch
                    {
                        0 => "1", 1 => "011", -1 => "010", 2 => "000011",
                        -2 => "000010", 3 => "0000011", -3 => "0000010",
                        _ => throw new InvalidOperationException()
                    });
                    a0 = a1;
                    black = !black;
                }
                else
                {
                    int a2 = Change(row, Math.Min(a1 + 1, row.Length), !black);
                    bits.Write("001");
                    WriteRun(bits, a1 - a0, black);
                    WriteRun(bits, a2 - a1, !black);
                    a0 = a2;
                }
            }
            initial = false;
        }
    }

    private static int Change(bool[] row, int start, bool color)
    {
        for (int index = start; index < row.Length; index++)
            if (row[index] != color) return index;
        return row.Length;
    }

    private static int ReferenceChange(bool[] row, int start, bool color, bool initial)
    {
        for (int index = initial ? start : start + 1; index < row.Length; index++)
        {
            bool before = index > 0 && row[index - 1];
            if (before != row[index] && row[index] != color) return index;
        }
        return row.Length;
    }

    private static void WriteRun(BitWriter bits, int run, bool black)
    {
        string[] makeup = black ? BlackMakeup : WhiteMakeup;
        while (run >= 2560)
        {
            bits.Write(makeup[^1]);
            run -= 2560;
        }
        if (run >= 64)
        {
            int multiple = run / 64;
            bits.Write(makeup[multiple - 1]);
            run -= multiple * 64;
        }
        bits.Write((black ? BlackTerminating : WhiteTerminating)[run]);
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private byte _current;
        private int _count;

        internal void Write(string code)
        {
            foreach (char bit in code)
            {
                _current |= (byte)((bit - '0') << (7 - _count));
                if (++_count != 8) continue;
                _bytes.Add(_current);
                _current = 0;
                _count = 0;
            }
        }

        internal byte[] ToArray()
        {
            if (_count != 0) _bytes.Add(_current);
            return [.. _bytes];
        }
    }
}
