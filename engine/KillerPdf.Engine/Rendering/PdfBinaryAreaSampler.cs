using System.Buffers.Binary;
using System.Numerics;

namespace KillerPdf.Engine.Rendering;

internal static class PdfBinaryAreaSampler
{
    internal readonly record struct Row(long Top, long Bottom, int First, int Last)
    {
        internal static Row Create(int y, int height, int outputHeight)
        {
            long top = (long)y * height, bottom = (long)(y + 1) * height;
            return new(top, bottom, (int)(top / outputHeight), (int)((bottom - 1) / outputHeight));
        }
    }

    internal static uint Sample(byte[] samples, int rowBytes, int width, int height,
        int x, int y, int outputWidth, int outputHeight, uint zero, uint one,
        CancellationToken cancellationToken) => Sample(samples, rowBytes, width, height,
            x, Row.Create(y, height, outputHeight), outputWidth, outputHeight, zero, one, cancellationToken);

    internal static uint Sample(byte[] samples, int rowBytes, int width, int height,
        int x, in Row bounds, int outputWidth, int outputHeight, uint zero, uint one,
        CancellationToken cancellationToken)
    {
        // Integer coordinates retain exact fractional coverage on the output grid.
        long left = (long)x * width, right = (long)(x + 1) * width;
        int firstX = (int)(left / outputWidth), lastX = (int)((right - 1) / outputWidth);
        long selected = 0;
        for (int sy = bounds.First; sy <= bounds.Last; sy++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int row = sy * rowBytes;
            long horizontal = 0;
            if (Bit(samples, row, firstX) != 0)
                horizontal = Math.Min((long)(firstX + 1) * outputWidth, right) - left;
            if (lastX > firstX)
            {
                if (Bit(samples, row, lastX) != 0)
                    horizontal += right - (long)lastX * outputWidth;
                horizontal += (long)Count(samples, row, firstX + 1, lastX) * outputWidth;
            }
            long vertical = Math.Min((long)(sy + 1) * outputHeight, bounds.Bottom)
                - Math.Max((long)sy * outputHeight, bounds.Top);
            selected += horizontal * vertical;
        }
        long area = (long)width * height;
        if (selected == 0) return zero;
        if (selected == area) return one;
        uint first = Mix((byte)zero, (byte)one, selected, area);
        uint fourth = Mix((byte)(zero >> 24), (byte)(one >> 24), selected, area);
        if ((zero & 0xFFFFFF) == (uint)(byte)zero * 0x010101
            && (one & 0xFFFFFF) == (uint)(byte)one * 0x010101)
            return first * 0x010101 | fourth << 24;
        return first | Mix((byte)(zero >> 8), (byte)(one >> 8), selected, area) << 8
            | Mix((byte)(zero >> 16), (byte)(one >> 16), selected, area) << 16 | fourth << 24;
    }

    private static uint Mix(byte zero, byte one, long selected, long area)
    {
        if (zero == one) return zero;
        long numerator = zero * (area - selected) + one * selected;
        long value = Math.DivRem(numerator, area, out long remainder);
        if (remainder * 2 > area || remainder * 2 == area && (value & 1) != 0) value++;
        return (uint)value;
    }

    private static int Bit(byte[] samples, int row, int x) =>
        (samples[row + (x >> 3)] >> (7 - (x & 7))) & 1;

    private static int Count(byte[] samples, int row, int start, int end)
    {
        int count = 0;
        if (start < end && (start & 7) != 0)
        {
            int length = Math.Min(end - start, 8 - (start & 7));
            uint mask = ((1u << length) - 1) << (8 - (start & 7) - length);
            count += BitOperations.PopCount(samples[row + (start >> 3)] & mask);
            start += length;
        }
        while (end - start >= 64)
        {
            count += BitOperations.PopCount(BinaryPrimitives.ReadUInt64LittleEndian(samples.AsSpan(row + (start >> 3), 8)));
            start += 64;
        }
        while (end - start >= 8)
        {
            count += BitOperations.PopCount((uint)samples[row + (start >> 3)]);
            start += 8;
        }
        if (start < end)
            count += BitOperations.PopCount((uint)samples[row + (start >> 3)] >> (8 - (end - start)));
        return count;
    }
}
