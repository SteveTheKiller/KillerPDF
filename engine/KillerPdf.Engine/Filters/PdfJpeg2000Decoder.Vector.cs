using System.Numerics;

namespace KillerPdf.Engine.Filters;

internal static partial class PdfJpeg2000Decoder
{
    // Each vector lane is an independent column. Boundary extension and the
    // normalization order match the existing 9/7 synthesis filter exactly.
    internal static void SynthesizeFloatColumns(float[] input, float[] output, int length, bool even)
    {
        int lanes = Vector<float>.Count;
        if (length == 1)
        {
            (new Vector<float>(input) * new Vector<float>(even ? 1f : .5f)).CopyTo(output);
            return;
        }
        int lowCount = (length + (even ? 1 : 0)) / 2;
        int highCount = length - lowCount;
        const float inverseLow = 1.2301741f, inverseHigh = .8128931f;
        const float delta = .44350687f, deltaOverHigh = .36052367f;
        for (int i = 0; i < lowCount; i++)
        {
            int a = Math.Clamp(even ? i - 1 : i, 0, highCount - 1);
            int b = Math.Clamp(even ? i : i + 1, 0, highCount - 1);
            var first = new Vector<float>(input, (lowCount + a) * lanes);
            var second = new Vector<float>(input, (lowCount + b) * lanes);
            Vector<float> correction;
            if (even)
                correction = a == b ? first * new Vector<float>(2 * deltaOverHigh)
                    : (first + second) * new Vector<float>(deltaOverHigh);
            else
            {
                first *= new Vector<float>(inverseHigh);
                second *= new Vector<float>(inverseHigh);
                correction = a == b ? first * new Vector<float>(2 * delta)
                    : (first + second) * new Vector<float>(delta);
            }
            (new Vector<float>(input, i * lanes) * new Vector<float>(inverseLow) - correction)
                .CopyTo(output, (2 * i + (even ? 0 : 1)) * lanes);
        }
        for (int i = 0; i < highCount; i++)
        {
            int y = 2 * i + (even ? 1 : 0);
            (new Vector<float>(input, (lowCount + i) * lanes) * new Vector<float>(inverseHigh)
                - FloatColumnCorrection(output, length, y, .8829111f)).CopyTo(output, y * lanes);
        }
        for (int i = 0; i < lowCount; i++)
        {
            int y = 2 * i + (even ? 0 : 1);
            (new Vector<float>(output, y * lanes) - FloatColumnCorrection(output, length, y, -.052980117f))
                .CopyTo(output, y * lanes);
        }
        for (int i = 0; i < highCount; i++)
        {
            int y = 2 * i + (even ? 1 : 0);
            (new Vector<float>(output, y * lanes) - FloatColumnCorrection(output, length, y, -1.5861343f))
                .CopyTo(output, y * lanes);
        }
    }

    private static Vector<float> FloatColumnCorrection(float[] samples, int length, int y, float coefficient)
    {
        int a = y == 0 ? 1 : y - 1;
        int b = y == length - 1 ? y - 1 : y + 1;
        int lanes = Vector<float>.Count;
        return a == b ? new Vector<float>(samples, a * lanes) * new Vector<float>(2 * coefficient)
            : (new Vector<float>(samples, a * lanes) + new Vector<float>(samples, b * lanes))
                * new Vector<float>(coefficient);
    }
}
