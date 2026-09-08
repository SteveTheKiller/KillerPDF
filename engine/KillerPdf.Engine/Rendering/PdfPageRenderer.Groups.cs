namespace KillerPdf.Engine.Rendering;

public sealed partial class PdfPageRenderer
{
    private static Color RemoveGroupBackdrop(RasterSurface group, int offset,
        RasterSurface backdrop, int backdropOffset, double groupAlpha)
    {
        if (groupAlpha == 1) return group.ReadColor(offset, backdrop);
        // The group result includes its initial backdrop. Remove that contribution
        // before applying the completed group as one source to its parent.
        double backdropWeight = backdrop.Alpha(backdropOffset) / 255d * (1 - groupAlpha);
        double resultAlpha = groupAlpha + backdropWeight;
        byte Component(byte result, byte initial) => (byte)Math.Round(Math.Clamp(
            (result * resultAlpha - initial * backdropWeight) / groupAlpha, 0, 255));
        if (group.Ink is not null)
        {
            uint ink = 0;
            for (int channel = 0; channel < 4; channel++)
                ink |= (uint)Component(group.Ink[offset + channel],
                    backdrop.Ink![backdropOffset + channel]) << (channel * 8);
            return group.ColorFromInk(ink, backdrop);
        }
        return group.ColorFromRgb(new Color(Component(group[offset + 2], backdrop[backdropOffset + 2]),
            Component(group[offset + 1], backdrop[backdropOffset + 1]),
            Component(group[offset], backdrop[backdropOffset])));
    }
}
