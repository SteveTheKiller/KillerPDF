using System.Buffers.Binary;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
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
        return PdfDeviceCmyk.FromRgb(color.Red, color.Green, color.Blue);
    }

    // Device-space luminosity uses the ink components, independently of display conversion.
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
        uint rgb = PdfDeviceCmyk.ToRgb(ink);
        return new Color((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb) { Ink = ink };
    }

    private static Color InkLuminosityColor(uint ink)
    {
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
        if (backdropAlpha == 0)
        {
            // Preserve the original arithmetic, including very small source alpha.
            // Every backdrop and blend contribution has zero weight.
            uint transparent = 0;
            for (int channel = 0; channel < 4; channel++)
            {
                double s = (byte)(source >> (channel * 8)) / 255d;
                double value = sourceAlpha * s / outputAlpha;
                transparent |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
            }
            WriteInk(surface.Ink!, offset, transparent);
            surface.SetAlpha(offset, (byte)Math.Round(outputAlpha * 255));
            return;
        }
        uint backdrop = ReadInk(surface.Ink!, offset);
        if (!overprint && backdropAlpha == 1 && mode is RendererBlendMode.Normal or RendererBlendMode.Compatible)
        {
            // Normal blending over an opaque backdrop. The terms that the general loop
            // multiplies by zero or one are dropped; the remaining operations and their
            // order are the same, so the rounded bytes match the general path exactly.
            uint blended = BlendOpaqueInk(source, backdrop, sourceAlpha, outputAlpha);
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

    internal static uint BlendOpaqueInk(uint source, uint backdrop, double sourceAlpha, double outputAlpha)
    {
        double backdropWeight = 1 - sourceAlpha;
        if (Avx.IsSupported && Sse2.IsSupported)
        {
            var scale = Vector256.Create(255d);
            var one = Vector256.Create(1d);
            var s = Vector256.Create((double)(byte)source, (double)(byte)(source >> 8),
                (double)(byte)(source >> 16), (double)(byte)(source >> 24)) / scale;
            var b = Vector256.Create((double)(byte)backdrop, (double)(byte)(backdrop >> 8),
                (double)(byte)(backdrop >> 16), (double)(byte)(backdrop >> 24)) / scale;
            var mixed = one - (one - s);
            var value = (Vector256.Create(backdropWeight) * b + Vector256.Create(sourceAlpha) * mixed)
                / Vector256.Create(outputAlpha);
            value = Vector256.Min(Vector256.Max(value, Vector256<double>.Zero), one) * scale;
            // Round each channel to even before narrowing, independently of conversion rounding mode.
            var integers = Avx.ConvertToVector128Int32WithTruncation(Avx.RoundToNearestInteger(value));
            var shorts = Sse2.PackSignedSaturate(integers, Vector128<int>.Zero);
            return Sse2.PackUnsignedSaturate(shorts, Vector128<short>.Zero).AsUInt32()[0];
        }
        uint blended = 0;
        for (int channel = 0; channel < 4; channel++)
        {
            double s = (byte)(source >> (channel * 8)) / 255d;
            double b = (byte)(backdrop >> (channel * 8)) / 255d;
            double mixed = 1 - (1 - s);
            double value = (backdropWeight * b + sourceAlpha * mixed) / outputAlpha;
            blended |= (uint)(byte)Math.Round(Math.Clamp(value, 0, 1) * 255) << (channel * 8);
        }
        return blended;
    }
}
