namespace KillerPdf.Engine.Authoring;

internal static class PdfPngPredictorEncoder
{
    internal static byte[] Encode(
        int width, int height, int colors, ReadOnlySpan<byte> pixels)
    {
        int rowBytes = checked(width * colors);
        byte[] output = new byte[checked((rowBytes + 1) * height)];
        byte[] candidate = new byte[rowBytes];
        for (int row = 0; row < height; row++)
        {
            ReadOnlySpan<byte> source = pixels.Slice(row * rowBytes, rowBytes);
            ReadOnlySpan<byte> previous = row == 0
                ? ReadOnlySpan<byte>.Empty
                : pixels.Slice((row - 1) * rowBytes, rowBytes);
            int bestFilter = 0;
            long bestScore = Score(source);
            Span<byte> best = output.AsSpan(row * (rowBytes + 1) + 1, rowBytes);
            source.CopyTo(best);
            for (int filter = 1; filter <= 4; filter++)
            {
                Filter(source, previous, colors, filter, candidate);
                long score = Score(candidate);
                if (score >= bestScore) continue;
                bestFilter = filter;
                bestScore = score;
                candidate.AsSpan().CopyTo(best);
            }
            output[row * (rowBytes + 1)] = (byte)bestFilter;
        }
        return output;
    }

    private static void Filter(ReadOnlySpan<byte> row, ReadOnlySpan<byte> previous,
        int bytesPerPixel, int filter, Span<byte> output)
    {
        for (int index = 0; index < row.Length; index++)
        {
            byte left = index >= bytesPerPixel ? row[index - bytesPerPixel] : (byte)0;
            byte above = previous.IsEmpty ? (byte)0 : previous[index];
            byte upperLeft = previous.IsEmpty || index < bytesPerPixel
                ? (byte)0 : previous[index - bytesPerPixel];
            int prediction = filter switch
            {
                1 => left,
                2 => above,
                3 => (left + above) / 2,
                4 => Paeth(left, above, upperLeft),
                _ => 0
            };
            output[index] = unchecked((byte)(row[index] - prediction));
        }
    }

    private static int Paeth(int left, int above, int upperLeft)
    {
        int estimate = left + above - upperLeft;
        int leftDistance = Math.Abs(estimate - left);
        int aboveDistance = Math.Abs(estimate - above);
        int upperLeftDistance = Math.Abs(estimate - upperLeft);
        return leftDistance <= aboveDistance && leftDistance <= upperLeftDistance
            ? left : aboveDistance <= upperLeftDistance ? above : upperLeft;
    }

    private static long Score(ReadOnlySpan<byte> values)
    {
        long score = 0;
        foreach (byte value in values) score += Math.Abs((int)(sbyte)value);
        return score;
    }
}
