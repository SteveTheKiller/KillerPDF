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

    private static uint ColorInk(Color color)
    {
        if (color.Ink is uint ink) return ink;
        int light = Math.Max(color.Red, Math.Max(color.Green, color.Blue));
        if (light == 0) return 0xFF000000;
        int cyan = (int)Math.Round((light - color.Red) * 255d / light);
        int magenta = (int)Math.Round((light - color.Green) * 255d / light);
        int yellow = (int)Math.Round((light - color.Blue) * 255d / light);
        return (uint)(cyan | magenta << 8 | yellow << 16 | (255 - light) << 24);
    }

    private static Color InkColor(uint ink)
    {
        int light = 255 - (int)(ink >> 24);
        return new Color((byte)(((255 - (byte)ink) * light + 127) / 255),
            (byte)(((255 - (byte)(ink >> 8)) * light + 127) / 255),
            (byte)(((255 - (byte)(ink >> 16)) * light + 127) / 255)) { Ink = ink };
    }

    private static void SetInkPixel(RasterSurface surface, int offset, Color color,
        double sourceAlpha, RendererBlendMode mode)
    {
        double backdropAlpha = surface.InkAlpha![offset / 4] / 255d;
        double outputAlpha = sourceAlpha + backdropAlpha * (1 - sourceAlpha);
        if (outputAlpha <= 0) return;
        uint source = ColorInk(color);
        if (sourceAlpha == 1 && mode is RendererBlendMode.Normal or RendererBlendMode.Compatible)
        {
            WriteInk(surface.Ink!, offset, source);
            surface.InkAlpha[offset / 4] = 255;
            return;
        }
        uint backdrop = ReadInk(surface.Ink!, offset);
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
            double value = sourceAlpha == 1 && backdropAlpha == 1 ? mixed
                : ((1 - backdropAlpha) * sourceAlpha * s
                + (1 - sourceAlpha) * backdropAlpha * b
                + sourceAlpha * backdropAlpha * mixed) / outputAlpha;
            output |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
        }
        WriteInk(surface.Ink!, offset, output);
        surface.InkAlpha[offset / 4] = (byte)Math.Round(outputAlpha * 255);
    }
}
