using System.Buffers.Binary;
using KillerPdf.Engine.Objects;

namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private bool CmykGroup(PdfDictionary container, PdfDictionary resources, bool inherited)
    {
        if (!container.TryGetValue(Name("Group"), out PdfObject? groupValue)
            || Resolve(groupValue) is not PdfDictionary group
            || !group.TryGetValue(Name("CS"), out PdfObject? spaceValue)) return inherited;
        if (container.TryGetValue(Name("Resources"), out PdfObject? resourcesValue)
            && Resolve(resourcesValue) is PdfDictionary ownResources) resources = ownResources;
        ImageColorSpace space = ReadColorSpace(spaceValue, resources, 0);
        return space.Components == 4 && space.Palette is null
            && space.Converter is null && space.MultiConverter is null;
    }

    private static uint ReadInk(byte[] ink, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(ink.AsSpan(offset, 4));

    private static void WriteInk(byte[] ink, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(ink.AsSpan(offset, 4), value);

    private static uint ColorInk(in Color color, PdfColorTransform? profile = null)
    {
        if (color.Ink is uint ink)
        {
            if (color.InkProfile is null || ReferenceEquals(color.InkProfile, profile)) return ink;
            if (profile is { CanConvertFromXyz: true })
            {
                Span<double> source = stackalloc double[4];
                Span<double> destination = stackalloc double[4];
                for (int channel = 0; channel < 4; channel++) source[channel] = (byte)(ink >> (channel * 8)) / 255d;
                if (color.Connection is { } connection)
                {
                    ReadOnlySpan<double> xyz = stackalloc double[3] { connection.X, connection.Y, connection.Z };
                    profile.FromXyz(xyz, destination);
                }
                else color.InkProfile.ConvertTo(profile, source, destination);
                return Color.Cmyk(destination[0], destination[1], destination[2], destination[3]).Ink!.Value;
            }
        }
        if (profile is { CanConvertFromXyz: true })
        {
            if (color.Connection is { } connection)
            {
                Span<double> destination = stackalloc double[4];
                ReadOnlySpan<double> xyz = stackalloc double[3] { connection.X, connection.Y, connection.Z };
                profile.FromXyz(xyz, destination);
                return Color.Cmyk(destination[0], destination[1], destination[2], destination[3]).Ink!.Value;
            }
            return DisplayToProfileInk(color, profile);
        }
        int light = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        if (light == 0) return 0xFF000000;
        int cyan = (int)Math.Round((light - color.Red) * 255d / light);
        int magenta = (int)Math.Round((light - color.Green) * 255d / light);
        int yellow = (int)Math.Round((light - color.Blue) * 255d / light);
        return (uint)(cyan | magenta << 8 | yellow << 16 | (255 - light) << 24);
    }

    // Unprofiled ink to display: ((255 - ink) * (255 - black) + 127) / 255 for each of the
    // three chromatic inks, tabulated by ink and black so conversion is two lookups per channel.
    private static readonly byte[] InkDisplayTable = CreateInkDisplayTable();

    private static byte[] CreateInkDisplayTable()
    {
        var table = new byte[256 * 256];
        for (int ink = 0; ink < 256; ink++)
            for (int black = 0; black < 256; black++)
                table[black * 256 + ink] = (byte)(((255 - ink) * (255 - black) + 127) / 255);
        return table;
    }

    private static Color InkColor(uint ink, PdfColorTransform? profile = null)
    {
        if (profile is not null) return ProfileInkToDisplay(ink, profile) with { Ink = ink, InkProfile = profile };
        int blackRow = (int)(ink >> 24) * 256;
        return new Color(InkDisplayTable[blackRow + (byte)ink],
            InkDisplayTable[blackRow + (byte)(ink >> 8)],
            InkDisplayTable[blackRow + (byte)(ink >> 16)]) { Ink = ink };
    }

    private static void SetInkPixel(RasterSurface surface, int offset, in Color color,
        double sourceAlpha, RendererBlendMode mode)
    {
        double backdropAlpha = surface.Alpha(offset) / 255d;
        double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        if (outputAlpha <= 0) return;
        uint source = surface.GetInk(color);
        bool overprint = (color.OverprintComponents & 16) != 0
            && mode is RendererBlendMode.Normal or RendererBlendMode.Compatible;
        if (!overprint && sourceAlpha == 1 && mode is RendererBlendMode.Normal or RendererBlendMode.Compatible)
        {
            WriteInk(surface.Ink!, offset, source);
            surface.SetAlpha(offset, 255);
            return;
        }
        uint backdrop = ReadInk(surface.Ink!, offset);
        if (!overprint && backdropAlpha == 1 && mode is RendererBlendMode.Normal or RendererBlendMode.Compatible)
        {
            // Normal blending over an opaque backdrop. The terms that the general loop
            // multiplies by zero or one are dropped; the remaining operations and their
            // order are the same, so the rounded bytes match the general path exactly.
            double backdropWeight = 1 - sourceAlpha;
            uint blended = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                double s = (byte)(source >> (channel * 8)) / 255d;
                double b = (byte)(backdrop >> (channel * 8)) / 255d;
                double mixed = 1 - (1 - s);
                double value = (backdropWeight * b + sourceAlpha * mixed) / outputAlpha;
                blended |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
            }
            WriteInk(surface.Ink!, offset, blended);
            surface.SetAlpha(offset, (byte)Math.Round(outputAlpha * 255));
            return;
        }
        bool nonseparable = mode is RendererBlendMode.Hue or RendererBlendMode.Saturation
            or RendererBlendMode.Color or RendererBlendMode.Luminosity;
        (double Red, double Green, double Blue) blend = default;
        if (nonseparable)
            blend = BlendNonSeparable(1 - (byte)backdrop / 255d,
                1 - (byte)(backdrop >> 8) / 255d, 1 - (byte)(backdrop >> 16) / 255d,
                1 - (byte)source / 255d, 1 - (byte)(source >> 8) / 255d,
                1 - (byte)(source >> 16) / 255d, mode);
        uint output = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            double s = (byte)(source >> (channel * 8)) / 255d;
            double b = (byte)(backdrop >> (channel * 8)) / 255d;
            double mixed = nonseparable ? channel switch
            {
                0 => 1 - blend.Red,
                1 => 1 - blend.Green,
                2 => 1 - blend.Blue,
                _ => mode == RendererBlendMode.Luminosity ? s : b
            } : 1 - BlendChannel(1 - b, 1 - s, mode);
            if (overprint && (color.OverprintComponents & (1 << channel)) != 0) mixed = b;
            double value = sourceAlpha == 1 && backdropAlpha == 1 ? mixed
                : ((1 - backdropAlpha) * sourceAlpha * s
                + (1 - sourceAlpha) * backdropAlpha * b
                + sourceAlpha * backdropAlpha * mixed) / outputAlpha;
            output |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
        }
        WriteInk(surface.Ink!, offset, output);
        surface.SetAlpha(offset, (byte)Math.Round(outputAlpha * 255));
    }
}
