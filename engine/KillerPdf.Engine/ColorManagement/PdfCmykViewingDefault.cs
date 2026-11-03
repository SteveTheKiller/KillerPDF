using System.Buffers.Binary;
using KillerPdf.Engine.Authoring;

namespace KillerPdf.Engine.ColorManagement;

/// <summary>Provides the bundled source viewing assumption for untagged CMYK content.</summary>
/// <remarks>
/// This perceptual forward-only assumption is not a printer profile selection.
/// </remarks>
public static class PdfCmykViewingDefault
{
    /// <summary>Gets the bundled profile name.</summary>
    public const string ProfileName = "CGATS001Compat-v2-micro";

    /// <summary>Gets the printing condition represented by the source viewing assumption.</summary>
    public const string PrintingCondition = "CGATS TR 001-1995 (North American web printing)";

    /// <summary>Gets the supported perceptual forward rendering intent.</summary>
    public const PdfRenderingIntent EffectiveIntent = PdfRenderingIntent.Perceptual;

    /// <summary>Loads an owned copy of the bundled source viewing profile.</summary>
    public static PdfColorProfile LoadProfile()
    {
        const int profileLength = 8464;
        using var stream = typeof(PdfCmykViewingDefault).Assembly.GetManifestResourceStream(
            "KillerPdf.Engine.ColorProfiles.CGATS001Compat-v2-micro.icc")
            ?? throw new InvalidOperationException("The bundled CMYK viewing profile is unavailable.");
        if (stream.Length != profileLength)
            throw new InvalidDataException("The bundled CMYK viewing profile has an unexpected size.");
        var data = new byte[profileLength];
        stream.ReadExactly(data);
        if (BinaryPrimitives.ReadUInt32BigEndian(data) != profileLength)
            throw new InvalidDataException("The bundled CMYK viewing profile has an unexpected declared size.");
        return PdfColorProfile.Load(data);
    }
}
