using System.Buffers.Binary;
using System.Numerics;

namespace KillerPdf.Engine.Rendering;

internal static class PdfBinaryAreaSampler
{
    internal static uint Sample(byte[] samples, int rowBytes, int width, int height,
        int x, int y, int outputWidth, int outputHeight, uint zero, uint one,
        CancellationToken cancellationToken)
    {
        // Integer coordinates retain exact fractional coverage on the output grid.
        long left = (long)x * width, right = (long)(x + 1) * width;
        long top = (long)y * height, bottom = (long)(y + 1) * height;
        int firstX = (int)(left / outputWidth), lastX = (int)((right - 1) / outputWidth);
        int firstY = (int)(top / outputHeight), lastY = (int)((bottom - 1) / outputHeight);
        long selected = 0;
        for (int sy = firstY; sy <= lastY; sy++)
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
            long vertical = Math.Min((long)(sy + 1) * outputHeight, bottom)
                - Math.Max((long)sy * outputHeight, top);
            selected += horizontal * vertical;
        }
        long area = (long)width * height;
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
        while (start < end && (start & 7) != 0) count += Bit(samples, row, start++);
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
        while (start < end) count += Bit(samples, row, start++);
        return count;
    }
}
