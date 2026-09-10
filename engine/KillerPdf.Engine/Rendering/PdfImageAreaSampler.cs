using System.Runtime.CompilerServices;

namespace KillerPdf.Engine.Rendering;

internal static class PdfImageAreaSampler
{
    internal readonly struct Row
    {
        internal double Top { get; }
        internal double Bottom { get; }
        internal int First { get; }
        internal int End { get; }
        private readonly double _firstWeight;
        private readonly double _lastWeight;

        internal Row(int height, double center, double footprint)
        {
            Top = Math.Max(0, center - footprint / 2);
            Bottom = Math.Min(height, center + footprint / 2);
            First = (int)Top;
            End = (int)Math.Ceiling(Bottom);
            _firstWeight = Math.Min(First + 1, Bottom) - Math.Max(First, Top);
            _lastWeight = Math.Min(End, Bottom) - Math.Max(End - 1, Top);
        }

        internal double Weight(int y) => y == First ? _firstWeight : y == End - 1 ? _lastWeight : 1;
    }

    internal static uint Sample(byte[] samples, int width, int height, int components,
        double centerX, double centerY, double footprintWidth, double footprintHeight,
        CancellationToken cancellationToken = default)
        => Sample(samples, width, components, centerX, footprintWidth,
            new Row(height, centerY, footprintHeight), cancellationToken);

    internal static uint Sample(byte[] samples, int width, int components,
        double centerX, double footprintWidth, Row row,
        CancellationToken cancellationToken = default)
    {
        double left = Math.Max(0, centerX - footprintWidth / 2);
        double right = Math.Min(width, centerX + footprintWidth / 2);
        double top = row.Top;
        double bottom = row.Bottom;
        double red = 0, green = 0, blue = 0;
        int first = (int)left, columns = (int)Math.Ceiling(right) - first;
        if (components == 3 && columns is > 0 and <= 3)
        {
            // Reuse horizontal weights without changing sample accumulation order.
            double w0 = Math.Min(first + 1, right) - Math.Max(first, left);
            double w1 = columns > 1 ? Math.Min(first + 2, right) - Math.Max(first + 1, left) : 0;
            double w2 = columns > 2 ? Math.Min(first + 3, right) - Math.Max(first + 2, left) : 0;
            for (int y = row.First; y < row.End; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double vertical = row.Weight(y);
                int offset = (y * width + first) * 3;
                AddRgb(samples, offset, vertical * w0, ref red, ref green, ref blue);
                if (columns > 1) AddRgb(samples, offset + 3, vertical * w1, ref red, ref green, ref blue);
                if (columns > 2) AddRgb(samples, offset + 6, vertical * w2, ref red, ref green, ref blue);
            }
        }
        else
        {
            for (int y = row.First; y < row.End; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                double vertical = row.Weight(y);
                for (int x = (int)left; x < (int)Math.Ceiling(right); x++)
                {
                    double weight = vertical * (Math.Min(x + 1, right) - Math.Max(x, left));
                    int offset = (y * width + x) * components;
                    red += samples[offset] * weight;
                    if (components == 3)
                    {
                        green += samples[offset + 1] * weight;
                        blue += samples[offset + 2] * weight;
                    }
                }
            }
        }
        double area = (right - left) * (bottom - top);
        uint r = (uint)Math.Clamp(Math.Round(red / area), 0, 255);
        uint g = components == 1 ? r : (uint)Math.Clamp(Math.Round(green / area), 0, 255);
        uint b = components == 1 ? r : (uint)Math.Clamp(Math.Round(blue / area), 0, 255);
        return r << 16 | g << 8 | b;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void AddRgb(byte[] samples, int offset, double weight, ref double red,
        ref double green, ref double blue)
    {
        red += samples[offset] * weight;
        green += samples[offset + 1] * weight;
        blue += samples[offset + 2] * weight;
    }
}
