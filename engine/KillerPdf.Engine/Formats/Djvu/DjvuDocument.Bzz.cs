namespace KillerPdf.Engine.Formats.Djvu;

// Original implementation of November 2005 Appendix 4.
internal static class DjvuBzzDecoder
{
    internal static byte[] Decode(ReadOnlySpan<byte> input, int decodedByteLimit, int blockByteLimit,
        int blockCountLimit, CancellationToken cancellationToken)
    {
        if (decodedByteLimit < 0 || blockByteLimit < 2 || blockCountLimit < 1)
            throw new ArgumentOutOfRangeException(nameof(decodedByteLimit));
        if (input.IsEmpty) throw new InvalidDataException("Missing BZZ stream.");
        // Each nonempty block emits at least one byte. Headers use 24 decisions,
        // speed uses at most two, and each transformed symbol uses at most 16.
        // Include the extra marker per block and the final zero-size header.
        var blocks = Math.Min((long)blockCountLimit, decodedByteLimit);
        var decisions = checked(24L + 42L * blocks + 16L * decodedByteLimit);
        var arithmetic = new DjvuZpDecoder(input.ToArray(), new byte[262], cancellationToken,
            decisionLimit: decisions, ordinaryUnmodeled: true);
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

    private static byte[] DecodeBlock(DjvuZpDecoder arithmetic, int size, CancellationToken token)
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

}
