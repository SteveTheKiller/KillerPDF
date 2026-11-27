using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Presentation;

/// <summary>
/// Everything the native viewport's window region is built from, reduced to numbers that are cheap to read and compare:
/// the occluder holes, the viewport's size, the pixel size and DPI, and for every ancestor that clips the viewport its
/// position relative to the viewport, size and clip rectangle. A layout pass that leaves this key equal cannot have
/// changed the region, so the host can skip combining and describing geometry. A clip shape the key cannot reduce to
/// numbers makes the key never equal, which keeps the full rebuild for it.
/// </summary>
internal sealed class ViewportClipKey
{
    readonly double[] values;
    readonly bool comparable;

    ViewportClipKey(double[] values, bool comparable)
    {
        this.values = values;
        this.comparable = comparable;
    }

    /// <summary>Reads the key. This does no geometry combining, so it is cheap enough to run on every layout pass.</summary>
    internal static ViewportClipKey Build(FrameworkElement viewport, IReadOnlyList<Rect> holes, uint dpi, int pixelWidth, int pixelHeight)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(holes);
        var values = new List<double>(48 + holes.Count * 4) { dpi, pixelWidth, pixelHeight,
            viewport.RenderSize.Width, viewport.RenderSize.Height, viewport.IsVisible ? 1 : 0, holes.Count };
        foreach (Rect hole in holes) Add(values, hole);
        bool comparable = true;
        int depth = 0;
        for (Visual? ancestor = viewport; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
        {
            if (++depth > 256) { comparable = false; break; }
            FrameworkElement? element = ancestor as FrameworkElement;
            Geometry? visualClip = VisualTreeHelper.GetClip(ancestor);
            Geometry? explicitClip = element?.Clip;
            bool clipsToBounds = element?.ClipToBounds == true;
            if (visualClip is null && explicitClip is null && !clipsToBounds) continue;
            values.Add(depth);
            if (ancestor.TransformToVisual(viewport) is Transform mapping)
            {
                Matrix matrix = mapping.Value;
                values.AddRange([matrix.M11, matrix.M12, matrix.M21, matrix.M22, matrix.OffsetX, matrix.OffsetY]);
            }
            else comparable = false;
            if (element is not null)
                values.AddRange([element.RenderSize.Width, element.RenderSize.Height, clipsToBounds ? 1 : 0]);
            comparable &= AddClip(values, visualClip);
            comparable &= AddClip(values, explicitClip);
        }
        return new(values.ToArray(), comparable);
    }

    /// <summary>True when the other key was read from the same state, so the installed region is still right.</summary>
    internal bool IsSameAs(ViewportClipKey? other) =>
        other is not null && comparable && other.comparable && values.AsSpan().SequenceEqual(other.values);

    static void Add(List<double> values, Rect rect) => values.AddRange([rect.X, rect.Y, rect.Width, rect.Height]);

    // A plain, untransformed rectangle reduces to its numbers; any other shape cannot, so it is reported as such.
    static bool AddClip(List<double> values, Geometry? clip)
    {
        if (clip is null) { values.Add(0); return true; }
        if (clip is not RectangleGeometry { } rectangle || !rectangle.Transform.Value.IsIdentity)
        {
            values.Add(-1);
            return false;
        }
        values.Add(1);
        Add(values, rectangle.Rect);
        values.AddRange([rectangle.RadiusX, rectangle.RadiusY]);
        return true;
    }
}
