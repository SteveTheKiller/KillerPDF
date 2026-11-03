using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>An owned ICC profile for portable color conversion.</summary>
public sealed class PdfColorProfile
{
    private readonly PdfIccProfileTransform _transform;

    private PdfColorProfile(PdfIccProfileTransform transform)
    {
        _transform = transform;
        ContentSha256 = transform.ContentSha256;
    }

    /// <summary>Gets the uppercase SHA256 identity of the validated declared ICC bytes.</summary>
    public string ContentSha256 { get; }

    internal int ComponentCount => _transform.Components;
    internal bool HasExplicitValidMediaWhite => _transform.HasExplicitValidMediaWhite;
    internal bool HasD50MediaWhite => _transform.HasD50MediaWhite;
    internal bool HasColorimetricForward => _transform.ForIntent(3) is { HasColorimetricForward: true };
    internal bool HasColorimetricReverse => _transform.ForIntent(3) is { HasColorimetricReverse: true };
    internal void ReadPaperWhite(Span<double> xyz) => _transform.ReadPaperWhite(xyz);
    internal void ReadPaperBlack(Span<double> xyz) => _transform.ReadPaperBlack(xyz);

    /// <summary>Converts device components into another profile through D50 XYZ.</summary>
    public void ConvertTo(PdfColorProfile destination, ReadOnlySpan<double> source, Span<double> result,
        PdfRenderingIntent intent = PdfRenderingIntent.RelativeColorimetric, bool blackPointCompensation = false)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Span<double> xyz = stackalloc double[3];
        ToXyz(source, xyz, intent);
        if (blackPointCompensation)
        {
            int index = IntentIndex(intent);
            if (!PdfBlackPointCompensation.TryCreate(_transform, destination._transform, index, out var compensation))
                throw new NotSupportedException("Black-point compensation is unavailable for this profile pair and intent.");
            compensation.Apply(xyz);
        }
        destination.FromXyz(xyz, result, intent);
    }

    /// <summary>Converts D50 XYZ connection values to device components.</summary>
    public void FromXyz(ReadOnlySpan<double> xyz, Span<double> device,
        PdfRenderingIntent intent = PdfRenderingIntent.RelativeColorimetric)
    {
        int index = IntentIndex(intent);
        var transform = _transform.ForIntent(index)
            ?? throw new NotSupportedException("The ICC profile does not support the requested rendering intent.");
        transform.FromXyz(xyz, device);
    }

    /// <summary>Loads an ICC profile of at most 32 MiB without accessing external resources.</summary>
    public static PdfColorProfile Load(ReadOnlyMemory<byte> data)
    {
        if (data.Length > 32 * 1024 * 1024)
            throw new FormatException("The ICC profile exceeds the 32 MiB input limit.");
        var profile = PdfIccProfile.Load(data);
        if (profile.Data.Span[8] is not (2 or 4))
            throw new NotSupportedException("Only ICC version 2 and version 4 profiles are supported.");
        var profileClass = profile.Data.Span.Slice(12, 4);
        if (!profileClass.SequenceEqual("scnr"u8) && !profileClass.SequenceEqual("mntr"u8)
            && !profileClass.SequenceEqual("prtr"u8) && !profileClass.SequenceEqual("spac"u8))
            throw new NotSupportedException("Only ICC input, display, output and color-space profiles are supported.");
        return new PdfColorProfile(PdfIccProfileTransform.ReadAvailable(profile.Data));
    }

    /// <summary>Converts device components to D50 XYZ using the requested rendering intent.</summary>
    public void ToXyz(ReadOnlySpan<double> device, Span<double> xyz,
        PdfRenderingIntent intent = PdfRenderingIntent.RelativeColorimetric)
    {
        int index = IntentIndex(intent);
        var transform = _transform.ForIntent(index)
            ?? throw new NotSupportedException("The ICC profile does not support the requested rendering intent.");
        transform.ToXyz(device, xyz);
    }

    private static int IntentIndex(PdfRenderingIntent intent) => intent switch
    {
        PdfRenderingIntent.Perceptual => 0,
        PdfRenderingIntent.RelativeColorimetric => 1,
        PdfRenderingIntent.Saturation => 2,
        PdfRenderingIntent.AbsoluteColorimetric => 3,
        _ => throw new ArgumentOutOfRangeException(nameof(intent))
    };
}
