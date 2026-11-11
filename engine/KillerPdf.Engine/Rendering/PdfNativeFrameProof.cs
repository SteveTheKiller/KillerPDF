using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.ColorManagement;

namespace KillerPdf.Engine.Rendering;

internal static class PdfNativeFrameProof
{
    // A region render passes its own pixel count; the whole page is never stored. retainedBytes is what the render
    // keeps alongside its output, such as a page's compact decode.
    internal static void Preflight(PdfRenderOptions options, long retainedBytes = 0, long? outputPixels = null)
    {
        if (options.Proof is not { Enabled: true } proof) return;
        PdfProofGate.Validate(proof, options.ForPrinting);
        long count = outputPixels ?? (long)options.Width * options.Height;
        // Source frame, mutable proof pixels and owned returned copy coexist.
        long bytes = checked(retainedBytes + count * (48 + (proof.GamutWarnings ? 2 : 0)));
        if (bytes > options.PixelByteLimit)
            throw new NotSupportedException("Native proof rendering exceeds the combined pixel storage limit.");
    }

    internal static PdfRenderedFrame Apply(PdfRenderedFrame source, PdfRenderOptions render, CancellationToken token)
    {
        if (render.Proof is not { Enabled: true } proof) return source;
        token.ThrowIfCancellationRequested();
        if (source.ColorEncoding != PdfFrameColorEncoding.LinearSrgbD65)
            throw new NotSupportedException("Native printer proof requires linear sRGB D65 channels.");
        var printer = proof.PrinterProfile!;
        var options = proof.Options;
        var chain = new PdfProofPixelChain(printer, options);
        // Warnings follow the proof's own black-point compensation.
        var gamut = proof.GamutWarnings ? PdfPrinterGamutCheck.For(printer, options.BlackPointCompensation
            && options.RenderingIntent != PdfRenderingIntent.AbsoluteColorimetric) : null;
        float[] pixels = source.Pixels.ToArray();
        byte[]? classifications = proof.GamutWarnings ? new byte[pixels.Length / 4] : null;
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[printer.ComponentCount];
        // Neighboring pixels of one fill share a color, so the last classification is reused for them.
        double lastX = double.NaN, lastY = double.NaN, lastZ = double.NaN;
        byte lastClassification = 0;
        for (int offset = 0; offset < pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) token.ThrowIfCancellationRequested();
            if (pixels[offset + 3] == 0)
            {
                if (classifications is not null) classifications[offset / 4] = (byte)PdfGamutClassification.NotEvaluated;
                continue;
            }
            double r = pixels[offset], g = pixels[offset + 1], b = pixels[offset + 2];
            xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
            xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
            xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
            if (gamut is not null)
            {
                if (xyz[0] != lastX || xyz[1] != lastY || xyz[2] != lastZ)
                {
                    (lastX, lastY, lastZ) = (xyz[0], xyz[1], xyz[2]);
                    lastClassification = (byte)gamut.ClassifyXyz(xyz);
                }
                classifications![offset / 4] = lastClassification;
            }
            chain.Apply(xyz, PdfPageRenderer.EncodeSrgb(Math.Min(r, Math.Min(g, b))), device);
            ToLinearRgb(xyz, pixels.AsSpan(offset, 3));
        }
        token.ThrowIfCancellationRequested();
        var mask = classifications is null ? null
            : new PdfGamutWarningMask(source.Width, source.Height, classifications, printer.ContentSha256);
        return new PdfRenderedFrame(source.Width, source.Height, pixels, source.ColorEncoding, render.PixelByteLimit, mask);
    }

    private static void ToLinearRgb(ReadOnlySpan<double> xyz, Span<float> rgb)
    {
        double x = xyz[0], y = xyz[1], z = xyz[2];
        // Bradford adaptation uses the same reference-white policy as precise PDF proof rendering.
        double l = (.8951 * x + .2664 * y - .1614 * z)
            * (.8951 * .95047 + .2664 - .1614 * 1.08883) / (.8951 * .9642 + .2664 - .1614 * .8249);
        double m = (-.7502 * x + 1.7135 * y + .0367 * z)
            * (-.7502 * .95047 + 1.7135 + .0367 * 1.08883) / (-.7502 * .9642 + 1.7135 + .0367 * .8249);
        double s = (.0389 * x - .0685 * y + 1.0296 * z)
            * (.0389 * .95047 - .0685 + 1.0296 * 1.08883) / (.0389 * .9642 - .0685 + 1.0296 * .8249);
        double ax = .9869929 * l - .1470543 * m + .1599627 * s;
        double ay = .4323053 * l + .5183603 * m + .0492912 * s;
        double az = -.0085287 * l + .0400428 * m + .9684867 * s;
        rgb[0] = (float)(3.2404542 * ax - 1.5371385 * ay - .4985314 * az);
        rgb[1] = (float)(-.969266 * ax + 1.8760108 * ay + .041556 * az);
        rgb[2] = (float)(.0556434 * ax - .2040259 * ay + 1.0572252 * az);
    }
}
