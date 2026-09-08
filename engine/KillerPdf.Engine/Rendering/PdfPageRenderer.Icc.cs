using KillerPdf.Engine.Objects;
using KillerPdf.Engine.Filters;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private readonly Lazy<PdfColorTransform?> _outputProfile;
    private readonly BoundedCache<PdfObject, (PdfColorTransform? Transform, int Bytes)> _colorProfiles =
        new(8, ReferenceEqualityComparer.Instance, 4 * 1024 * 1024, entry => entry.Bytes);
    private static readonly Func<double, double, double, Color> IccXyzToDisplay =
        CreateXyzConverter([0.9642, 1, 0.8249]);

    private PdfColorTransform? ReadOutputProfile()
    {
        if (!_document.Trailer.TryGetValue(Name("Root"), out PdfObject? root)
            || Resolve(root) is not PdfDictionary catalog
            || !catalog.TryGetValue(Name("OutputIntents"), out PdfObject? intentsValue)
            || Resolve(intentsValue) is not PdfArray intents) return null;
        PdfColorTransform? fallback = null;
        foreach (PdfObject value in intents)
        {
            if (Resolve(value) is not PdfDictionary intent
                || !intent.TryGetValue(Name("DestOutputProfile"), out PdfObject? profileValue)
                || Resolve(profileValue) is not PdfStream stream
                || ReadIccProfile(stream) is not { Components: 4 } profile) continue;
            if (intent.TryGetValue(Name("S"), out PdfObject? subtype)
                && Resolve(subtype) is PdfName name && name.ValueAsLatin1() == "GTS_PDFX") return profile;
            fallback ??= profile;
        }
        return fallback;
    }

    private PdfColorTransform? ReadGroupProfile(PdfDictionary page, PdfDictionary resources,
        HashSet<string> diagnostics, PdfColorTransform? inherited = null)
    {
        if (!page.TryGetValue(Name("Group"), out PdfObject? groupValue)
            || Resolve(groupValue) is not PdfDictionary group
            || !group.TryGetValue(Name("CS"), out PdfObject? spaceValue)) return inherited;
        if (page.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            && Resolve(resourcesValue) is PdfDictionary ownResources) resources = ownResources;
        PdfObject space = Resolve(spaceValue);
        if (space is PdfArray { Count: 1 } deviceArray) space = Resolve(deviceArray[0]);
        for (int depth = 0; space is PdfName name && depth < 16; depth++)
        {
            if (name.ValueAsLatin1() is "DeviceCMYK" or "CMYK" or "DeviceRGB" or "RGB" or "DeviceGray" or "G")
            {
                ImageColorSpace mapped = ReadColorSpace(name, resources, 0);
                if (mapped.IsDefault && mapped.Profile is { } defaultProfile)
                {
                    if (defaultProfile.SupportsBlending
                        && (defaultProfile.Components == 4 || defaultProfile.CanConvertFromXyz)) return defaultProfile;
                    diagnostics.Add("The default color profile could not be used for group blending.");
                    return null;
                }
            }
            if (name.ValueAsLatin1() is "DeviceCMYK" or "CMYK")
                return inherited is { Components: 4 } ? inherited : _outputProfile.Value;
            if (name.ValueAsLatin1() is "DeviceRGB" or "RGB" or "DeviceGray" or "G") return null;
            if (!resources.TryGetValue(Name("ColorSpace"), out PdfObject? spacesValue)
                || Resolve(spacesValue) is not PdfDictionary spaces
                || !spaces.TryGetValue(name, out PdfObject? namedValue)) return null;
            space = Resolve(namedValue);
        }
        if (space is PdfArray { Count: 2 } calibrated && Resolve(calibrated[0]) is PdfName calibratedKind
            && calibratedKind.ValueAsLatin1() is "CalRGB" or "CalGray")
        {
            PdfColorTransform? transform = ReadColorSpace(space, resources, 0).Profile;
            if (transform is { CanConvertFromXyz: true }) return transform;
            diagnostics.Add("The calibrated group matrix could not be used for blending.");
            return null;
        }
        if (space is not PdfArray { Count: 2 } array || Resolve(array[0]) is not PdfName kind
            || kind.ValueAsLatin1() != "ICCBased" || Resolve(array[1]) is not PdfStream stream) return null;
        if (!stream.Dictionary.TryGetValue(Name("N"), out PdfObject? components)
            || Resolve(components) is not PdfInteger { Value: 1 or 3 or 4 } count) return null;
        PdfColorTransform? profile = ReadIccProfile(stream);
        if (profile is null || profile.Components != count.Value || !profile.SupportsBlending
            || profile.Components != 4 && !profile.CanConvertFromXyz)
        {
            diagnostics.Add("The group ICC profile could not be used; its alternate color space was used.");
            return null;
        }
        return profile;
    }

    private PdfColorTransform? ReadIccProfile(PdfStream stream) =>
        _colorProfiles.GetOrAdd(stream, value =>
        {
            try
            {
                byte[] bytes = _document.DecodeStream((PdfStream)value, 16 * 1024 * 1024);
                var transform = new PdfIccProfileTransform(bytes);
                return (transform, bytes.Length);
            }
            catch (Exception exception) when (exception is FormatException or NotSupportedException
                or InvalidDataException or PdfFilterException)
            {
                return (null, 0);
            }
        }).Transform;

    private PdfColorTransform ReadCalibratedProfile(PdfArray space, double[] white,
        double[] gamma, double[]? matrix = null) => _colorProfiles.GetOrAdd(space,
            _ => (new PdfCalibratedColorTransform(white, gamma, matrix), 512)).Transform!;

    private static Color ConvertProfileColor(PdfColorTransform profile,
        double first, double second, double third, double fourth)
    {
        Span<double> device = stackalloc double[4] { first, second, third, fourth };
        Span<double> xyz = stackalloc double[3];
        profile.ToXyz(device[..profile.Components], xyz);
        Color color = IccXyzToDisplay(xyz[0], xyz[1], xyz[2]) with { Connection = (xyz[0], xyz[1], xyz[2]) };
        return profile.Components == 4 ? color with
        {
            Ink = Color.Cmyk(first, second, third, fourth).Ink,
            OverprintComponents = Color.ZeroInkComponents(first, second, third, fourth),
            InkProfile = profile
        } : color;
    }

    private static Color ProfileInkToDisplay(uint ink, PdfColorTransform profile)
    {
        Span<double> device = stackalloc double[4];
        for (int channel = 0; channel < 4; channel++) device[channel] = (byte)(ink >> (channel * 8)) / 255d;
        Span<double> xyz = stackalloc double[3];
        profile.ToXyz(device, xyz);
        return IccXyzToDisplay(xyz[0], xyz[1], xyz[2]) with { Connection = (xyz[0], xyz[1], xyz[2]) };
    }

    private static uint DisplayToProfileInk(Color color, PdfColorTransform profile)
    {
        Span<double> xyz = stackalloc double[3];
        DisplayToXyz(color, xyz);
        Span<double> device = stackalloc double[4];
        profile.FromXyz(xyz, device);
        return Color.Cmyk(device[0], device[1], device[2], device[3]).Ink!.Value;
    }

    private static Color ProfileRgbToDisplay(Color samples, PdfColorTransform profile) =>
        ConvertProfileColor(profile, samples.Red / 255d, samples.Green / 255d, samples.Blue / 255d, 0);

    private static Color ColorRgb(in Color color, PdfColorTransform? profile)
    {
        if (profile is null) return color;
        Span<double> xyz = stackalloc double[3];
        if (color.Connection is { } connection)
        {
            xyz[0] = connection.X;
            xyz[1] = connection.Y;
            xyz[2] = connection.Z;
        }
        else DisplayToXyz(color, xyz);
        Span<double> samples = stackalloc double[3];
        profile.FromXyz(xyz, samples[..profile.Components]);
        return profile.Components == 1 ? Color.Rgb(samples[0], samples[0], samples[0])
            : Color.Rgb(samples[0], samples[1], samples[2]);
    }

    private static void DisplayToXyz(in Color color, Span<double> xyz)
    {
        static double Linear(byte value)
        {
            double normalized = value / 255d;
            return normalized <= 0.04045 ? normalized / 12.92 : Math.Pow((normalized + 0.055) / 1.055, 2.4);
        }
        double r = Linear(color.Red), g = Linear(color.Green), b = Linear(color.Blue);
        xyz[0] = 0.4360747 * r + 0.3850649 * g + 0.1430804 * b;
        xyz[1] = 0.2225045 * r + 0.7168786 * g + 0.0606169 * b;
        xyz[2] = 0.0139322 * r + 0.0971045 * g + 0.7141733 * b;
    }
}
