namespace KillerPdf.Engine.Authoring;

internal static class PdfPngPredictorEncoder
{
    // About 25 ms of a 600 dpi letter photo row by row through the predictor and Flate.
    internal const int CancellationRowBand = 32;

    internal static byte[] Encode(
        int width, int height, int colors, ReadOnlySpan<byte> pixels)
    {
        int rowBytes = checked(width * colors);
        byte[] output = new byte[checked((rowBytes + 1) * height)];
        byte[] candidate = new byte[rowBytes];
        for (int row = 0; row < height; row++)
            EncodeRow(pixels, rowBytes, colors, row, output.AsSpan(row * (rowBytes + 1), rowBytes + 1), candidate);
        return output;
    }

    /// <summary>
    /// Writes the bytes <see cref="Encode"/> returns to <paramref name="destination"/> one row at a time, holding two
    /// rows of working storage instead of a second copy of the image. The token is read before the first row and then
    /// once per <see cref="CancellationRowBand"/> rows, so a canceled encode writes at most that many more rows.
    /// </summary>
    internal static void EncodeTo(Stream destination, int width, int height, int colors, ReadOnlySpan<byte> pixels,
        CancellationToken cancellationToken = default)
    {
        int rowBytes = checked(width * colors);
        byte[] encoded = new byte[rowBytes + 1];
        byte[] candidate = new byte[rowBytes];
        for (int row = 0; row < height; row++)
        {
            if (row % CancellationRowBand == 0) cancellationToken.ThrowIfCancellationRequested();
            EncodeRow(pixels, rowBytes, colors, row, encoded, candidate);
            destination.Write(encoded);
        }
    }

    // One row as its filter type byte and the row filtered with the predictor whose output scores lowest.
    private static void EncodeRow(ReadOnlySpan<byte> pixels, int rowBytes, int colors, int row, Span<byte> encoded,
        byte[] candidate)
    {
        ReadOnlySpan<byte> source = pixels.Slice(row * rowBytes, rowBytes);
        ReadOnlySpan<byte> previous = row == 0
            ? []
            : pixels.Slice((row - 1) * rowBytes, rowBytes);
        int bestFilter = 0;
        long bestScore = Score(source);
        Span<byte> best = encoded.Slice(1, rowBytes);
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
        encoded[0] = (byte)bestFilter;
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
