namespace KillerPdf.Engine.Formats.Djvu;

// Original implementation of November 2005 Appendix 3 and Appendix 4.
// The April 1999 Table 9 supplies nine damaged fields in the 2005 printing.
internal static class DjvuBzzDecoder
{
    internal static byte[] Decode(ReadOnlySpan<byte> input, int decodedByteLimit, int blockByteLimit,
        int blockCountLimit, CancellationToken cancellationToken)
    {
        if (decodedByteLimit < 0 || blockByteLimit < 2 || blockCountLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(decodedByteLimit));
        if (input.IsEmpty) throw new InvalidDataException("Missing BZZ stream.");
        var arithmetic = new Arithmetic(input.ToArray(), cancellationToken);
        using var output = new MemoryStream();
        var blockCount = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = arithmetic.Raw(24);
            // Zero block size is the stream-termination hypothesis tested by the directory vectors.
            if (size == 0) return output.ToArray();
            if (++blockCount > blockCountLimit || size < 2 || size > blockByteLimit ||
                size - 1 > decodedByteLimit - output.Length)
                throw new InvalidDataException("BZZ decoded data exceeds the directory resource limits.");
            var block = DecodeBlock(arithmetic, size, cancellationToken);
            output.Write(block, 0, size - 1);
        }
    }

    private static byte[] DecodeBlock(Arithmetic arithmetic, int size, CancellationToken token)
    {
        var speed = arithmetic.Bit() == 0 ? 0 : arithmetic.Bit() == 0 ? 1 : 2;
        var symbols = new byte[size];
        var alphabet = new byte[256];
        for (var i = 0; i < alphabet.Length; i++) alphabet[i] = (byte)i;
        var frequencies = new int[4];
        var increment = 4;
        var previous = 3;
        var marker = -1;
        for (var i = 0; i < size; i++)
        {
            token.ThrowIfCancellationRequested();
            var context = Math.Min(previous, 2);
            int rank;
            if (arithmetic.Bit(context) != 0) rank = 0;
            else if (arithmetic.Bit(context + 3) != 0) rank = 1;
            else
            {
                rank = 256;
                for (var bits = 1; bits <= 7; bits++)
                {
                    var first = 1 << bits;
                    if (arithmetic.Bit(first + 4) == 0) continue;
                    rank = first + arithmetic.Binary(first + 5, bits);
                    break;
                }
            }
            previous = rank;
            if (rank == 256)
            {
                if (marker >= 0) throw new InvalidDataException("BZZ block contains multiple end markers.");
                marker = i;
                continue;
            }
            var symbol = alphabet[rank];
            symbols[i] = symbol;
            increment += increment >> speed;
            if (increment > 0x10000000)
            {
                increment >>= 24;
                for (var j = 0; j < 4; j++) frequencies[j] >>= 24;
            }
            var frequency = increment + (rank < 4 ? frequencies[rank] : 0);
            var destination = rank;
            while (destination > 3)
            {
                token.ThrowIfCancellationRequested();
                alphabet[destination] = alphabet[destination - 1];
                destination--;
            }
            while (destination > 0 && frequency >= frequencies[destination - 1])
            {
                token.ThrowIfCancellationRequested();
                alphabet[destination] = alphabet[destination - 1];
                frequencies[destination] = frequencies[destination - 1];
                destination--;
            }
            alphabet[destination] = symbol;
            frequencies[destination] = frequency;
        }
        if (marker <= 0) throw new InvalidDataException("BZZ block has no valid end marker.");

        var occurrence = new int[size];
        var starts = new int[256];
        for (var i = 0; i < size; i++)
        {
            token.ThrowIfCancellationRequested();
            if (i != marker) occurrence[i] = starts[symbols[i]]++;
        }
        var start = 1;
        for (var i = 0; i < starts.Length; i++)
        {
            var count = starts[i];
            starts[i] = start;
            start += count;
        }
        if (start != size) throw new InvalidDataException("Invalid BZZ symbol counts.");
        var decoded = new byte[size - 1];
        var cursor = 0;
        for (var i = decoded.Length - 1; i >= 0; i--)
        {
            token.ThrowIfCancellationRequested();
            if ((uint)cursor >= (uint)size || cursor == marker)
                throw new InvalidDataException("Invalid BZZ transform chain.");
            var symbol = symbols[cursor];
            decoded[i] = symbol;
            cursor = starts[symbol] + occurrence[cursor];
        }
        if (cursor != marker) throw new InvalidDataException("BZZ transform does not end at its marker.");
        return decoded;
    }

    private sealed class Arithmetic
    {
        private readonly byte[] _input;
        private readonly CancellationToken _token;
        private readonly byte[] _contexts = new byte[262];
        private int _bitPosition;
        private int _paddingBits;
        private int _a;
        private int _c;

        internal Arithmetic(byte[] input, CancellationToken token)
        {
            _input = input;
            _token = token;
            for (var i = 0; i < 16; i++) _c = (_c << 1) | NextBit();
        }

        private int NextBit()
        {
            _token.ThrowIfCancellationRequested();
            if (_bitPosition >= _input.Length * 8)
            {
                if (++_paddingBits > 32) throw new InvalidDataException("Truncated BZZ arithmetic stream.");
                return 1;
            }
            var value = (_input[_bitPosition / 8] >> (7 - _bitPosition % 8)) & 1;
            _bitPosition++;
            return value;
        }

        internal int Raw(int bits)
        {
            var value = 0;
            for (var i = 0; i < bits; i++) value = (value << 1) | Bit();
            return value;
        }

        internal int Binary(int context, int bits)
        {
            var node = 1;
            var end = 1 << bits;
            while (node < end) node = node * 2 + Bit(context + node - 1);
            return node - end;
        }

        internal int Bit(int context = -1)
        {
            _token.ThrowIfCancellationRequested();
            var state = context < 0 ? default : States[_contexts[context]];
            var split = context < 0 ? 0x8000 + ((_a * 3) >> 3) :
                Math.Min(_a + state.Delta, 0x6000 + ((_a * 2 + state.Delta) >> 2));
            var most = context < 0 ? 0 : _contexts[context] & 1;
            int result;
            if (_c > split || (context >= 0 && split < 0x8000 && _c == split))
            {
                result = most;
                // Independent DjVu bitstreams require MPS adaptation only when renormalizing.
                // The printed November 2005 pseudocode omits the split threshold.
                if (context >= 0 && split >= 0x8000 && _a >= state.Threshold) _contexts[context] = state.More;
                _a = split;
            }
            else
            {
                result = 1 - most;
                _a = (_a + 0x10000 - split) & 0xffff;
                _c = (_c + 0x10000 - split) & 0xffff;
                if (context >= 0) _contexts[context] = state.Less;
            }
            while (_a >= 0x8000)
            {
                _a = (_a << 1) & 0xffff;
                _c = ((_c << 1) | NextBit()) & 0xffff;
            }
            return result;
        }
    }

    private readonly record struct State(int Delta, int Threshold, byte More, byte Less);

    private static readonly State[] States =
    [
        new(0x8000, 0x0000, 84, 145),
        new(0x8000, 0x0000, 3, 4),
        new(0x8000, 0x0000, 4, 3),
        new(0x6BBD, 0x10A5, 5, 1),
        new(0x6BBD, 0x10A5, 6, 2),
        new(0x5D45, 0x1F28, 7, 3),
        new(0x5D45, 0x1F28, 8, 4),
        new(0x51B9, 0x2BD3, 9, 5),
        new(0x51B9, 0x2BD3, 10, 6),
        new(0x4813, 0x36E3, 11, 7),
        new(0x4813, 0x36E3, 12, 8),
        new(0x3FD5, 0x408C, 13, 9),
        new(0x3FD5, 0x408C, 14, 10),
        new(0x38B1, 0x48FD, 15, 11),
        new(0x38B1, 0x48FD, 16, 12),
        new(0x3275, 0x505D, 17, 13),
        new(0x3275, 0x505D, 18, 14),
        new(0x2CFD, 0x56D0, 19, 15),
        new(0x2CFD, 0x56D0, 20, 16),
        new(0x2825, 0x5C71, 21, 17),
        new(0x2825, 0x5C71, 22, 18),
        new(0x23AB, 0x615B, 23, 19),
        new(0x23AB, 0x615B, 24, 20),
        new(0x1F87, 0x65A5, 25, 21),
        new(0x1F87, 0x65A5, 26, 22),
        new(0x1BBB, 0x6962, 27, 23),
        new(0x1BBB, 0x6962, 28, 24),
        new(0x1845, 0x6CA2, 29, 25),
        new(0x1845, 0x6CA2, 30, 26),
        new(0x1523, 0x6F74, 31, 27),
        new(0x1523, 0x6F74, 32, 28),
        new(0x1253, 0x71E6, 33, 29),
        new(0x1253, 0x71E6, 34, 30),
        new(0x0FCF, 0x7404, 35, 31),
        new(0x0FCF, 0x7404, 36, 32),
        new(0x0D95, 0x75D6, 37, 33),
        new(0x0D95, 0x75D6, 38, 34),
        new(0x0B9D, 0x7768, 39, 35),
        new(0x0B9D, 0x7768, 40, 36),
        new(0x09E3, 0x78C2, 41, 37),
        new(0x09E3, 0x78C2, 42, 38),
        new(0x0861, 0x79EA, 43, 39),
        new(0x0861, 0x79EA, 44, 40),
        new(0x0711, 0x7AE7, 45, 41),
        new(0x0711, 0x7AE7, 46, 42),
        new(0x05F1, 0x7BBE, 47, 43),
        new(0x05F1, 0x7BBE, 48, 44),
        new(0x04F9, 0x7C75, 49, 45),
        new(0x04F9, 0x7C75, 50, 46),
        new(0x0425, 0x7D0F, 51, 47),
        new(0x0425, 0x7D0F, 52, 48),
        new(0x0371, 0x7D91, 53, 49),
        new(0x0371, 0x7D91, 54, 50),
        new(0x02D9, 0x7DFE, 55, 51),
        new(0x02D9, 0x7DFE, 56, 52),
        new(0x0259, 0x7E5A, 57, 53),
        new(0x0259, 0x7E5A, 58, 54),
        new(0x01ED, 0x7EA6, 59, 55),
        new(0x01ED, 0x7EA6, 60, 56),
        new(0x0193, 0x7EE6, 61, 57),
        new(0x0193, 0x7EE6, 62, 58),
        new(0x0149, 0x7F1A, 63, 59),
        new(0x0149, 0x7F1A, 64, 60),
        new(0x010B, 0x7F45, 65, 61),
        new(0x010B, 0x7F45, 66, 62),
        new(0x00D5, 0x7F6B, 67, 63),
        new(0x00D5, 0x7F6B, 68, 64),
        new(0x00A5, 0x7F8D, 69, 65),
        new(0x00A5, 0x7F8D, 70, 66),
        new(0x007B, 0x7FAA, 71, 67),
        new(0x007B, 0x7FAA, 72, 68),
        new(0x0057, 0x7FC3, 73, 69),
        new(0x0057, 0x7FC3, 74, 70),
        new(0x003B, 0x7FD7, 75, 71),
        new(0x003B, 0x7FD7, 76, 72),
        new(0x0023, 0x7FE7, 77, 73),
        new(0x0023, 0x7FE7, 78, 74),
        new(0x0013, 0x7FF2, 79, 75),
        new(0x0013, 0x7FF2, 80, 76),
        new(0x0007, 0x7FFA, 81, 77),
        new(0x0007, 0x7FFA, 82, 78),
        new(0x0001, 0x7FFF, 81, 79),
        new(0x0001, 0x7FFF, 82, 80),
        new(0x5695, 0x0000, 9, 85),
        new(0x24EE, 0x0000, 86, 226),
        new(0x8000, 0x0000, 5, 6),
        new(0x0D30, 0x0000, 88, 176),
        new(0x481A, 0x0000, 89, 143),
        new(0x0481, 0x0000, 90, 138),
        new(0x3579, 0x0000, 91, 141),
        new(0x017A, 0x0000, 92, 112),
        new(0x24EF, 0x0000, 93, 135),
        new(0x007B, 0x0000, 94, 104),
        new(0x1978, 0x0000, 95, 133),
        new(0x0028, 0x0000, 96, 100),
        new(0x10CA, 0x0000, 97, 129),
        new(0x000D, 0x0000, 82, 98),
        new(0x0B5D, 0x0000, 99, 127),
        new(0x0034, 0x0000, 76, 72),
        new(0x078A, 0x0000, 101, 125),
        new(0x00A0, 0x0000, 70, 102),
        new(0x050F, 0x0000, 103, 123),
        new(0x0117, 0x0000, 66, 60),
        new(0x0358, 0x0000, 105, 121),
        new(0x01EA, 0x0000, 106, 110),
        new(0x0234, 0x0000, 107, 119),
        new(0x0144, 0x0000, 66, 108),
        new(0x0173, 0x0000, 109, 117),
        new(0x0234, 0x0000, 60, 54),
        new(0x00F5, 0x0000, 111, 115),
        new(0x0353, 0x0000, 56, 48),
        new(0x00A1, 0x0000, 69, 113),
        new(0x05C5, 0x0000, 114, 134),
        new(0x011A, 0x0000, 65, 59),
        new(0x03CF, 0x0000, 116, 132),
        new(0x01AA, 0x0000, 61, 55),
        new(0x0285, 0x0000, 118, 130),
        new(0x0286, 0x0000, 57, 51),
        new(0x01AB, 0x0000, 120, 128),
        new(0x03D3, 0x0000, 53, 47),
        new(0x011A, 0x0000, 122, 126),
        new(0x05C5, 0x0000, 49, 41),
        new(0x00BA, 0x0000, 124, 62),
        new(0x08AD, 0x0000, 43, 37),
        new(0x007A, 0x0000, 72, 66),
        new(0x0CCC, 0x0000, 39, 31),
        new(0x01EB, 0x0000, 60, 54),
        new(0x1302, 0x0000, 33, 25),
        new(0x02E6, 0x0000, 56, 50),
        new(0x1B81, 0x0000, 29, 131),
        new(0x045E, 0x0000, 52, 46),
        new(0x24EF, 0x0000, 23, 17),
        new(0x0690, 0x0000, 48, 40),
        new(0x2865, 0x0000, 23, 15),
        new(0x09DE, 0x0000, 42, 136),
        new(0x3987, 0x0000, 137, 7),
        new(0x0DC8, 0x0000, 38, 32),
        new(0x2C99, 0x0000, 21, 139),
        new(0x10CA, 0x0000, 140, 172),
        new(0x3B5F, 0x0000, 15, 9),
        new(0x0B5D, 0x0000, 142, 170),
        new(0x5695, 0x0000, 9, 85),
        new(0x078A, 0x0000, 144, 168),
        new(0x8000, 0x0000, 141, 248),
        new(0x050F, 0x0000, 146, 166),
        new(0x24EE, 0x0000, 147, 247),
        new(0x0358, 0x0000, 148, 164),
        new(0x0D30, 0x0000, 149, 197),
        new(0x0234, 0x0000, 150, 162),
        new(0x0481, 0x0000, 151, 95),
        new(0x0173, 0x0000, 152, 160),
        new(0x017A, 0x0000, 153, 173),
        new(0x00F5, 0x0000, 154, 158),
        new(0x007B, 0x0000, 155, 165),
        new(0x00A1, 0x0000, 70, 156),
        new(0x0028, 0x0000, 157, 161),
        new(0x011A, 0x0000, 66, 60),
        new(0x000D, 0x0000, 81, 159),
        new(0x01AA, 0x0000, 62, 56),
        new(0x0034, 0x0000, 75, 71),
        new(0x0286, 0x0000, 58, 52),
        new(0x00A0, 0x0000, 69, 163),
        new(0x03D3, 0x0000, 54, 48),
        new(0x0117, 0x0000, 65, 59),
        new(0x05C5, 0x0000, 50, 42),
        new(0x01EA, 0x0000, 167, 171),
        new(0x08AD, 0x0000, 44, 38),
        new(0x0144, 0x0000, 65, 169),
        new(0x0CCC, 0x0000, 40, 32),
        new(0x0234, 0x0000, 59, 53),
        new(0x1302, 0x0000, 34, 26),
        new(0x0353, 0x0000, 55, 47),
        new(0x1B81, 0x0000, 30, 174),
        new(0x05C5, 0x0000, 175, 193),
        new(0x24EF, 0x0000, 24, 18),
        new(0x03CF, 0x0000, 177, 191),
        new(0x2B74, 0x0000, 178, 222),
        new(0x0285, 0x0000, 179, 189),
        new(0x201D, 0x0000, 180, 218),
        new(0x01AB, 0x0000, 181, 187),
        new(0x1715, 0x0000, 182, 216),
        new(0x011A, 0x0000, 183, 185),
        new(0x0FB7, 0x0000, 184, 214),
        new(0x00BA, 0x0000, 69, 61),
        new(0x0A67, 0x0000, 186, 212),
        new(0x01EB, 0x0000, 59, 53),
        new(0x06E7, 0x0000, 188, 210),
        new(0x02E6, 0x0000, 55, 49),
        new(0x0496, 0x0000, 190, 208),
        new(0x045E, 0x0000, 51, 45),
        new(0x030D, 0x0000, 192, 206),
        new(0x0690, 0x0000, 47, 39),
        new(0x0206, 0x0000, 194, 204),
        new(0x09DE, 0x0000, 41, 195),
        new(0x0155, 0x0000, 196, 202),
        new(0x0DC8, 0x0000, 37, 31),
        new(0x00E1, 0x0000, 198, 200),
        new(0x2B74, 0x0000, 199, 243),
        new(0x0094, 0x0000, 72, 64),
        new(0x201D, 0x0000, 201, 239),
        new(0x0188, 0x0000, 62, 56),
        new(0x1715, 0x0000, 203, 237),
        new(0x0252, 0x0000, 58, 52),
        new(0x0FB7, 0x0000, 205, 235),
        new(0x0383, 0x0000, 54, 48),
        new(0x0A67, 0x0000, 207, 233),
        new(0x0547, 0x0000, 50, 44),
        new(0x06E7, 0x0000, 209, 231),
        new(0x07E2, 0x0000, 46, 38),
        new(0x0496, 0x0000, 211, 229),
        new(0x0BC0, 0x0000, 40, 34),
        new(0x030D, 0x0000, 213, 227),
        new(0x1178, 0x0000, 36, 28),
        new(0x0206, 0x0000, 215, 225),
        new(0x19DA, 0x0000, 30, 22),
        new(0x0155, 0x0000, 217, 223),
        new(0x24EF, 0x0000, 26, 16),
        new(0x00E1, 0x0000, 219, 221),
        new(0x320E, 0x0000, 20, 220),
        new(0x0094, 0x0000, 71, 63),
        new(0x432A, 0x0000, 14, 8),
        new(0x0188, 0x0000, 61, 55),
        new(0x447D, 0x0000, 14, 224),
        new(0x0252, 0x0000, 57, 51),
        new(0x5ECE, 0x0000, 8, 2),
        new(0x0383, 0x0000, 53, 47),
        new(0x8000, 0x0000, 228, 87),
        new(0x0547, 0x0000, 49, 43),
        new(0x481A, 0x0000, 230, 246),
        new(0x07E2, 0x0000, 45, 37),
        new(0x3579, 0x0000, 232, 244),
        new(0x0BC0, 0x0000, 39, 33),
        new(0x24EF, 0x0000, 234, 238),
        new(0x1178, 0x0000, 35, 27),
        new(0x1978, 0x0000, 138, 236),
        new(0x19DA, 0x0000, 29, 21),
        new(0x2865, 0x0000, 24, 16),
        new(0x24EF, 0x0000, 25, 15),
        new(0x3987, 0x0000, 240, 8),
        new(0x320E, 0x0000, 19, 241),
        new(0x2C99, 0x0000, 22, 242),
        new(0x432A, 0x0000, 13, 7),
        new(0x3B5F, 0x0000, 16, 10),
        new(0x447D, 0x0000, 13, 245),
        new(0x5695, 0x0000, 10, 2),
        new(0x5ECE, 0x0000, 7, 1),
        new(0x8000, 0x0000, 244, 83),
        new(0x8000, 0x0000, 249, 250),
        new(0x5695, 0x0000, 10, 2),
        new(0x481A, 0x0000, 89, 143),
        new(0x481A, 0x0000, 230, 246),
    ];
}
