using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Rendering;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>An owned ICC profile for portable color conversion.</summary>
public sealed class PdfColorProfile
{
    private readonly PdfIccProfileTransform _transform;

    private PdfColorProfile(PdfIccProfileTransform transform, ReadOnlySpan<byte> header)
    {
        _transform = transform;
        ContentSha256 = transform.ContentSha256;
        ProfileClassSignature = System.Text.Encoding.ASCII.GetString(header.Slice(12, 4));
        DeviceColorSpaceSignature = System.Text.Encoding.ASCII.GetString(header.Slice(16, 4));
        ConnectionColorSpaceSignature = System.Text.Encoding.ASCII.GetString(header.Slice(20, 4));
        MajorVersion = header[8];
    }

    /// <summary>Gets the uppercase SHA256 identity of the validated declared ICC bytes.</summary>
    public string ContentSha256 { get; }

    /// <summary>Gets the ICC profile-class signature.</summary>
    public string ProfileClassSignature { get; }

    /// <summary>Gets the ICC device color-space signature.</summary>
    public string DeviceColorSpaceSignature { get; }

    /// <summary>Gets the ICC connection color-space signature.</summary>
    public string ConnectionColorSpaceSignature { get; }

    /// <summary>Gets the ICC major version.</summary>
    public int MajorVersion { get; }

    internal int ComponentCount => _transform.Components;
    internal bool HasExplicitValidMediaWhite => _transform.HasExplicitValidMediaWhite;
    internal bool HasD50MediaWhite => _transform.HasD50MediaWhite;
    internal bool HasColorimetricForward => _transform.ForIntent(3) is { HasColorimetricForward: true };
    internal bool HasColorimetricReverse => _transform.ForIntent(3) is { HasColorimetricReverse: true };
    internal void ReadPaperWhite(Span<double> xyz) => _transform.ReadPaperWhite(xyz);
    internal void ReadPaperBlack(Span<double> xyz) => _transform.ReadPaperBlack(xyz);

    internal PdfBlackPointCompensation ReferenceBlackToDeviceCompensation(PdfRenderingIntent intent)
    {
        // The unprofiled RGB page-group reference uses mathematical XYZ zero black.
        ValidateReferenceBlackRange(intent);
        if (!PdfBlackPointCompensation.TryCreate(null, _transform, IntentIndex(intent), out var compensation))
            throw new NotSupportedException("Printer proof source black-point compensation is unavailable for this profile.");
        return compensation;
    }

    internal PdfBlackPointCompensation DeviceToReferenceBlackCompensation(PdfRenderingIntent intent)
    {
        ValidateReferenceBlackRange(intent);
        if (!PdfBlackPointCompensation.TryCreate(_transform, null, IntentIndex(intent), out var compensation))
            throw new NotSupportedException("Printer proof black normalization is unavailable for this profile.");
        return compensation;
    }

    private void ValidateReferenceBlackRange(PdfRenderingIntent intent)
    {
        Span<double> device = stackalloc double[ComponentCount];
        Span<double> black = stackalloc double[3];
        Span<double> white = stackalloc double[3];
        FromXyz([0, 0, 0], device, intent);
        ToXyz(device, black, intent);
        FromXyz([.9642, 1, .8249], device, intent);
        ToXyz(device, white, intent);
        for (int channel = 0; channel < 3; channel++)
            if (!double.IsFinite(black[channel]) || !double.IsFinite(white[channel]))
                throw new NotSupportedException("Printer proof compensation has nonfinite reference endpoints.");
        double range = white[1] - black[1];
        if (!double.IsFinite(range) || range <= 1e-9)
            throw new NotSupportedException("Printer proof compensation has a collapsed reference range.");
    }

    /// <summary>Describes the transform selected for the requested intent and direction.</summary>
    public PdfColorTransformSelection GetTransformSelection(
        PdfRenderingIntent intent = PdfRenderingIntent.RelativeColorimetric,
        PdfColorTransformDirection direction = PdfColorTransformDirection.DeviceToConnection)
    {
        int index = IntentIndex(intent);
        if (direction is not (PdfColorTransformDirection.DeviceToConnection
            or PdfColorTransformDirection.ConnectionToDevice))
            throw new ArgumentOutOfRangeException(nameof(direction));
        var transform = PublicTransform(index);
        if (transform is null || direction == PdfColorTransformDirection.ConnectionToDevice
            && !transform.CanConvertFromXyz)
            return new(intent, direction, false, false, null, null, false, false);

        string? tag = direction == PdfColorTransformDirection.DeviceToConnection
            ? transform.ForwardTableSignature : transform.ReverseTableSignature;
        PdfRenderingIntent? tableIntent = tag is null ? null : tag[3] switch
        {
            '0' => PdfRenderingIntent.Perceptual,
            '1' => PdfRenderingIntent.RelativeColorimetric,
            '2' => PdfRenderingIntent.Saturation,
            _ => throw new InvalidOperationException("The selected ICC table signature is invalid.")
        };
        int expectedTableIndex = index == 3 ? 1 : index;
        return new(intent, direction, true, !transform.IsLutBased, tag, tableIntent,
            tag is not null && tag[3] - '0' != expectedTableIndex, transform.AppliesMediaWhiteScaling);
    }

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
        var transform = PublicTransform(index)
            ?? throw new NotSupportedException("The ICC profile does not support the requested rendering intent.");
        transform.FromXyz(xyz, device);
    }

    // Absolute colorimetric needs explicit valid media white; without it the variant would be relative in disguise.
    private PdfIccProfileTransform? PublicTransform(int index) =>
        index == 3 && !_transform.HasExplicitValidMediaWhite ? null : _transform.ForIntent(index);

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
        return new PdfColorProfile(PdfIccProfileTransform.ReadAvailable(profile.Data), profile.Data.Span);
    }

    /// <summary>Converts device components to D50 XYZ using the requested rendering intent.</summary>
    public void ToXyz(ReadOnlySpan<double> device, Span<double> xyz,
        PdfRenderingIntent intent = PdfRenderingIntent.RelativeColorimetric)
    {
        int index = IntentIndex(intent);
        var transform = PublicTransform(index)
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
