namespace KillerPdf.Engine.Rendering;

internal static class FloatRgbCompositor
{
    internal static void SourceOver(float[] pixels, int offset,
        double red, double green, double blue, double opacity)
    {
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        double targetWeight = pixels[offset + 3] * (1 - sourceAlpha);
        double outputAlpha = sourceAlpha + targetWeight;
        if (outputAlpha == 0) return;
        pixels[offset] = (float)((red * sourceAlpha + pixels[offset] * targetWeight) / outputAlpha);
        pixels[offset + 1] = (float)((green * sourceAlpha + pixels[offset + 1] * targetWeight) / outputAlpha);
        pixels[offset + 2] = (float)((blue * sourceAlpha + pixels[offset + 2] * targetWeight) / outputAlpha);
        pixels[offset + 3] = (float)outputAlpha;
    }

    internal static void Multiply(float[] pixels, int offset,
        double red, double green, double blue, double opacity)
    {
        double sourceAlpha = Math.Clamp(opacity, 0, 1);
        double backdropAlpha = pixels[offset + 3];
        double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        if (outputAlpha == 0) return;
        Span<double> source = [red, green, blue];
        for (int channel = 0; channel < 3; channel++)
        {
            double backdrop = pixels[offset + channel];
            double premultiplied = (1 - sourceAlpha) * backdropAlpha * backdrop
                + (1 - backdropAlpha) * sourceAlpha * source[channel]
                + sourceAlpha * backdropAlpha * backdrop * source[channel];
            pixels[offset + channel] = (float)(premultiplied / outputAlpha);
        }
        pixels[offset + 3] = (float)outputAlpha;
    }

    internal static void InterpolateBackdrop(float[] pixels, int offset,
        float[] completed, int completedOffset, double opacity)
    {
        double weight = Math.Clamp(opacity, 0, 1);
        double originalWeight = pixels[offset + 3] * (1 - weight);
        double completedWeight = completed[completedOffset + 3] * weight;
        double outputAlpha = originalWeight + completedWeight;
        if (outputAlpha == 0) return;
        for (int channel = 0; channel < 3; channel++)
            pixels[offset + channel] = (float)((pixels[offset + channel] * originalWeight
                + completed[completedOffset + channel] * completedWeight) / outputAlpha);
        pixels[offset + 3] = (float)outputAlpha;
    }

    internal static void DecodeSrgbInPlace(float[] pixels, CancellationToken cancellationToken)
    {
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            for (int channel = 0; channel < 3; channel++)
                pixels[offset + channel] = (float)DecodeSrgb(pixels[offset + channel]);
        }
    }

    private static double DecodeSrgb(double value) => value <= 0.04045
        ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
}
