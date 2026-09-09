namespace KillerPdf.Engine.Rendering;

internal static class PdfImageAreaSampler
{
    internal static uint Sample(byte[] samples, int width, int height, int components,
        double centerX, double centerY, double footprintWidth, double footprintHeight,
        CancellationToken cancellationToken = default)
    {
        double left = Math.Max(0, centerX - footprintWidth / 2);
        double right = Math.Min(width, centerX + footprintWidth / 2);
        double top = Math.Max(0, centerY - footprintHeight / 2);
        double bottom = Math.Min(height, centerY + footprintHeight / 2);
        double red = 0, green = 0, blue = 0;
        for (int y = (int)top; y < (int)Math.Ceiling(bottom); y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double vertical = Math.Min(y + 1, bottom) - Math.Max(y, top);
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
        double area = (right - left) * (bottom - top);
        uint r = (uint)Math.Clamp(Math.Round(red / area), 0, 255);
        uint g = components == 1 ? r : (uint)Math.Clamp(Math.Round(green / area), 0, 255);
        uint b = components == 1 ? r : (uint)Math.Clamp(Math.Round(blue / area), 0, 255);
        return r << 16 | g << 8 | b;
    }
}
