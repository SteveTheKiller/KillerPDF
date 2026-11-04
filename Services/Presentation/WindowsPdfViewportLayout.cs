using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Presentation;

/// <summary>Captures a viewport's visible ancestor intersection in its local layout coordinates.</summary>
internal static class WindowsPdfViewportLayout
{
    internal static Geometry Capture(FrameworkElement viewport)
    {
        ArgumentNullException.ThrowIfNull(viewport);
        viewport.VerifyAccess();
        if (!viewport.IsArrangeValid || !viewport.IsVisible)
            throw new InvalidOperationException("Viewport layout is not visibly ready.");
        Geometry result = new RectangleGeometry(new Rect(viewport.RenderSize));
        int depth = 0;
        for (Visual? ancestor = viewport; ancestor is not null; ancestor = VisualTreeHelper.GetParent(ancestor) as Visual)
        {
            if (++depth > 256) throw new NotSupportedException("Viewport ancestor depth exceeds the supported limit.");
            GeneralTransform mapping = ancestor.TransformToVisual(viewport);
            if (mapping is not Transform affine || !affine.Value.HasInverse)
                throw new NotSupportedException("Viewport clipping requires an invertible two-dimensional transform.");
            if (VisualTreeHelper.GetClip(ancestor) is { } visualClip) Intersect(visualClip, affine);
            if (ancestor is FrameworkElement element)
            {
                if (!element.IsVisible) return Geometry.Empty;
                if (element.Clip is { } explicitClip) Intersect(explicitClip, affine);
                if (element.ClipToBounds) Intersect(new RectangleGeometry(new Rect(element.RenderSize)), affine);
            }
        }
        result.Freeze();
        return result;

        void Intersect(Geometry geometry, Transform mapping)
        {
            Geometry mapped = geometry.CloneCurrentValue();
            var transforms = new TransformGroup();
            if (mapped.Transform is { } existing) transforms.Children.Add(existing);
            transforms.Children.Add(mapping);
            mapped.Transform = transforms;
            result = Geometry.Combine(result, mapped, GeometryCombineMode.Intersect, null);
        }
    }
}
