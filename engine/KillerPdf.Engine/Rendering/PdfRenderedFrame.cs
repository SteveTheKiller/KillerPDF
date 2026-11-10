namespace KillerPdf.Engine.Rendering;

/// <summary>Identifies the channel encoding and reference white of a floating-point frame.</summary>
public enum PdfFrameColorEncoding
{
    /// <summary>sRGB primaries, D65 white and the sRGB transfer function.</summary>
    SrgbD65,
    /// <summary>sRGB primaries and D65 white with linear channels, including extended values.</summary>
    LinearSrgbD65
}

/// <summary>An owned floating-point RGBA frame with straight alpha and a top-left origin.</summary>
/// <remarks>
/// RGB channels are relative to document white at one, without an implied absolute luminance.
/// Finite extended RGB is retained; alpha stays between zero and one. Zero-alpha RGB is retained.
/// Storage is copied, managed and never pooled. The frame can be read across threads and requires no disposal.
/// </remarks>
public sealed class PdfRenderedFrame
{
    private readonly float[] _pixels;

    /// <summary>Gets the hard limit for the owned float pixel buffer, independent of other render buffers.</summary>
    public const long MaximumPixelBytes = 512L * 1024 * 1024;

    /// <summary>Copies tightly packed RGBA32F pixels into an independently owned frame.</summary>
    public PdfRenderedFrame(int width, int height, ReadOnlySpan<float> pixels,
        PdfFrameColorEncoding colorEncoding, long maximumPixelBytes = MaximumPixelBytes)
        : this(width, height, pixels, colorEncoding, maximumPixelBytes, null) { }

    /// <summary>Copies color pixels and retains an immutable, independently owned printer-gamut mask.</summary>
    public PdfRenderedFrame(int width, int height, ReadOnlySpan<float> pixels,
        PdfFrameColorEncoding colorEncoding, long maximumPixelBytes, PdfGamutWarningMask? printerGamutWarnings)
    {
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        if (colorEncoding is not (PdfFrameColorEncoding.SrgbD65 or PdfFrameColorEncoding.LinearSrgbD65))
            throw new ArgumentOutOfRangeException(nameof(colorEncoding));
        if (maximumPixelBytes is <= 0 or > MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumPixelBytes));
        if (printerGamutWarnings is not null
            && (printerGamutWarnings.Width != width || printerGamutWarnings.Height != height))
            throw new ArgumentException("The printer gamut mask must match the color frame dimensions.", nameof(printerGamutWarnings));
        long pixelCount = (long)width * height;
        if (pixelCount > maximumPixelBytes / (4 * sizeof(float)))
            throw new ArgumentException("The floating-point frame exceeds its storage limit.");
        if (pixels.Length != pixelCount * 4)
            throw new ArgumentException("The pixel channels do not match the frame dimensions.", nameof(pixels));
        for (int channel = 0; channel < pixels.Length; channel++)
            if (!float.IsFinite(pixels[channel])
                || (channel & 3) == 3 && pixels[channel] is < 0 or > 1)
                throw new ArgumentException("Color channels must be finite and alpha must be between zero and one.",
                    nameof(pixels));
        _pixels = pixels.ToArray();
        Width = width;
        Height = height;
        ColorEncoding = colorEncoding;
        PrinterGamutWarnings = printerGamutWarnings;
    }

    /// <summary>Gets tightly packed float32 RGBA channels with straight alpha.</summary>
    public ReadOnlySpan<float> Pixels => _pixels;

    /// <summary>Gets separate printer-gamut classifications, or null when none were requested.</summary>
    public PdfGamutWarningMask? PrinterGamutWarnings { get; }

    /// <summary>
    /// Gets the SHA256 content identity of the working CMYK profile assumed for untagged
    /// DeviceCMYK paint, or null when the frame painted none.
    /// </summary>
    public string? AssumedCmykProfileSha256 { get; init; }

    private readonly IReadOnlyList<string> _diagnostics = [];

    /// <summary>
    /// Gets notes that did not prevent precise rendering, such as document repairs made by
    /// compatibility recovery and the working profile assumed for untagged CMYK.
    /// </summary>
    public IReadOnlyList<string> Diagnostics
    {
        get => _diagnostics;
        init => _diagnostics = Array.AsReadOnly((value ?? throw new ArgumentNullException(nameof(value))).ToArray());
    }

    /// <summary>Gets the flat neutral gray that marks out-of-gamut pixels, as sRGB-encoded relative RGB.</summary>
    /// <remarks>sRGB 128/255, the mid gray other soft-proofing tools paint over colors a printer cannot reproduce.</remarks>
    public const float GamutWarningGray = 128f / 255f;

    /// <summary>Returns a viewing copy with out-of-gamut pixels painted flat neutral gray.</summary>
    /// <remarks>
    /// Only pixels the printer gamut mask classifies as outside are painted; alpha and every other
    /// pixel keep their proofed values, and the mask is retained. A frame without a mask, or with no
    /// pixel outside, is returned as is without copying. The mark is for viewing only, never for output.
    /// </remarks>
    public PdfRenderedFrame WithGamutWarningsMarked(CancellationToken cancellationToken = default)
    {
        if (PrinterGamutWarnings is not { WarningPixelCount: > 0 } mask) return this;
        cancellationToken.ThrowIfCancellationRequested();
        float gray = ColorEncoding == PdfFrameColorEncoding.LinearSrgbD65
            ? (float)Math.Pow((GamutWarningGray + .055) / 1.055, 2.4) : GamutWarningGray;
        float[] pixels = (float[])_pixels.Clone();
        ReadOnlySpan<byte> classifications = mask.Classifications;
        for (int index = 0; index < classifications.Length; index++)
        {
            if ((index & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (classifications[index] != (byte)KillerPdf.Engine.ColorManagement.PdfGamutClassification.Outside) continue;
            int offset = index * 4;
            pixels[offset] = gray;
            pixels[offset + 1] = gray;
            pixels[offset + 2] = gray;
        }
        return new PdfRenderedFrame(this, pixels);
    }

    // Wraps already validated pixels of the same size, keeping the source frame's metadata.
    private PdfRenderedFrame(PdfRenderedFrame source, float[] pixels)
    {
        _pixels = pixels;
        Width = source.Width;
        Height = source.Height;
        ColorEncoding = source.ColorEncoding;
        PrinterGamutWarnings = source.PrinterGamutWarnings;
        AssumedCmykProfileSha256 = source.AssumedCmykProfileSha256;
        _diagnostics = source._diagnostics;
    }

    /// <summary>Gets the pixel width.</summary>
    public int Width { get; }
    /// <summary>Gets the pixel height.</summary>
    public int Height { get; }
    /// <summary>Gets the tightly packed row stride measured in bytes.</summary>
    public int StrideBytes => checked(Width * 4 * sizeof(float));
    /// <summary>Gets the primaries, transfer function and relative white of the RGB channels.</summary>
    public PdfFrameColorEncoding ColorEncoding { get; }

    /// <summary>Copies linear RGBA to owned premultiplied IEEE half channels with scaled reference white.</summary>
    /// <remarks>
    /// RGB is rounded to half precision at this output boundary without clipping to zero through one.
    /// Alpha is not white-scaled. The storage budget counts input/output pixel buffers and any owned gamut mask.
    /// Nonfinite half results and meaningful color whose positive alpha rounds to zero are rejected.
    /// </remarks>
    public Half[] ToPremultipliedLinearHalfRgba(double referenceWhiteScale = 1,
        long maximumCombinedPixelBytes = MaximumPixelBytes, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (ColorEncoding != PdfFrameColorEncoding.LinearSrgbD65)
            throw new NotSupportedException("Half output requires a linear sRGB D65 frame.");
        if (!double.IsFinite(referenceWhiteScale) || referenceWhiteScale <= 0)
            throw new ArgumentOutOfRangeException(nameof(referenceWhiteScale));
        if (maximumCombinedPixelBytes is <= 0 or > MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCombinedPixelBytes));
        long combinedBytes = _pixels.LongLength * (sizeof(float) + sizeof(ushort))
            + (PrinterGamutWarnings is null ? 0 : (long)Width * Height);
        if (combinedBytes > maximumCombinedPixelBytes)
            throw new ArgumentException("The input frame and half output exceed their combined pixel storage limit.",
                nameof(maximumCombinedPixelBytes));
        var output = new Half[_pixels.Length];
        for (int offset = 0; offset < _pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (_pixels[offset + 3] == 0) continue;
            double factor = referenceWhiteScale * _pixels[offset + 3];
            for (int channel = 0; channel < 3; channel++)
            {
                output[offset + channel] = (Half)(_pixels[offset + channel] * factor);
                if (!Half.IsFinite(output[offset + channel]))
                    throw new NotSupportedException("A premultiplied color channel exceeds finite half precision.");
            }
            output[offset + 3] = (Half)_pixels[offset + 3];
            if (output[offset + 3] == (Half)0
                && (output[offset] != (Half)0 || output[offset + 1] != (Half)0 || output[offset + 2] != (Half)0))
                throw new NotSupportedException("Half precision would lose positive alpha while retaining premultiplied color.");
        }
        return output;
    }

    /// <summary>Copies the frame to straight-alpha BGRA32 in an RGB monitor profile.</summary>
    /// <remarks>
    /// Converts through D50 XYZ with relative colorimetric intent before final byte clipping.
    /// Output channels are monitor device values, not sRGB. Use only when the application owns
    /// monitor conversion. The combined budget counts input floats, output bytes and any retained gamut mask.
    /// </remarks>
    public byte[] ToDisplayBgra32(KillerPdf.Engine.ColorManagement.PdfColorProfile monitor,
        CancellationToken cancellationToken = default, long maximumCombinedPixelBytes = MaximumPixelBytes)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        cancellationToken.ThrowIfCancellationRequested();
        if (monitor.ProfileClassSignature != "mntr")
            throw new NotSupportedException("Display output requires a monitor profile.");
        if (monitor.DeviceColorSpaceSignature != "RGB ")
            throw new NotSupportedException("BGRA display output requires an RGB monitor profile.");
        var reverse = monitor.GetTransformSelection(KillerPdf.Engine.Authoring.PdfRenderingIntent.RelativeColorimetric,
            KillerPdf.Engine.ColorManagement.PdfColorTransformDirection.ConnectionToDevice);
        if (!reverse.IsAvailable || reverse.UsesIntentFallback)
            throw new NotSupportedException("Display output requires an actual relative colorimetric monitor reverse transform.");
        if (maximumCombinedPixelBytes is <= 0 or > MaximumPixelBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumCombinedPixelBytes));
        long combinedBytes = _pixels.LongLength * (sizeof(float) + sizeof(byte))
            + (PrinterGamutWarnings is null ? 0 : (long)Width * Height);
        if (combinedBytes > maximumCombinedPixelBytes)
            throw new ArgumentException("The input frame and display bytes exceed their combined pixel storage limit.",
                nameof(maximumCombinedPixelBytes));
        var output = new byte[_pixels.Length];
        Span<double> xyz = stackalloc double[3];
        Span<double> device = stackalloc double[3];
        for (int offset = 0; offset < _pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            double r = Linear(_pixels[offset]), g = Linear(_pixels[offset + 1]), b = Linear(_pixels[offset + 2]);
            xyz[0] = .4360747 * r + .3850649 * g + .1430804 * b;
            xyz[1] = .2225045 * r + .7168786 * g + .0606169 * b;
            xyz[2] = .0139322 * r + .0971045 * g + .7141733 * b;
            monitor.FromXyz(xyz, device);
            output[offset] = Quantize(device[2]);
            output[offset + 1] = Quantize(device[1]);
            output[offset + 2] = Quantize(device[0]);
            output[offset + 3] = Quantize(_pixels[offset + 3]);
        }
        return output;

        double Linear(double value) => ColorEncoding == PdfFrameColorEncoding.LinearSrgbD65 ? value
            : value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
        static byte Quantize(double value) =>
            (byte)Math.Round(Math.Clamp(value, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }

    /// <summary>Copies the frame to straight-alpha BGRA32 in sRGB, clipping at this output boundary.</summary>
    public byte[] ToSrgbBgra32(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var output = new byte[_pixels.Length];
        bool linear = ColorEncoding == PdfFrameColorEncoding.LinearSrgbD65;
        // A channel's byte depends on its value alone, and a page repeats few values (paper white, ink), so the last
        // value encoded is kept: a repeat costs a comparison of the value's bits instead of a power function.
        int lastBits = BitConverter.SingleToInt32Bits(float.NaN);
        byte lastByte = 0;
        for (int offset = 0; offset < _pixels.Length; offset += 4)
        {
            if ((offset & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            output[offset] = Remembered(_pixels[offset + 2]);
            output[offset + 1] = Remembered(_pixels[offset + 1]);
            output[offset + 2] = Remembered(_pixels[offset]);
            output[offset + 3] = Quantize(_pixels[offset + 3]);
        }
        return output;

        byte Remembered(float channel)
        {
            int bits = BitConverter.SingleToInt32Bits(channel);
            if (bits == lastBits) return lastByte;
            lastBits = bits;
            return lastByte = Encode(channel);
        }

        byte Encode(double channel)
        {
            channel = Math.Clamp(channel, 0, 1);
            if (linear)
                channel = channel <= 0.0031308 ? 12.92 * channel
                    : 1.055 * Math.Pow(channel, 1 / 2.4) - 0.055;
            return Quantize(channel);
        }

        static byte Quantize(double channel) =>
            (byte)Math.Round(Math.Clamp(channel, 0, 1) * 255, MidpointRounding.AwayFromZero);
    }
}
