using System.Windows;
using System.Windows.Media;

namespace KillerPDF.Presentation;

/// <summary>
/// Main-window content that draws over the native page viewport. A child window covers whatever WPF draws in its
/// rectangle, so the viewport cuts every visible occluder out of its window region. The tile behind the viewport is
/// painted with the page's sRGB bitmap, so a cut shows the page under a bar, a badge or a scrim instead of the pane.
/// </summary>
internal static class ViewportOcclusion
{
    /// <summary>Marks an element that can draw over the page: a bar, notice, badge or full-window overlay.</summary>
    internal static readonly DependencyProperty OccludesProperty = DependencyProperty.RegisterAttached("Occludes",
        typeof(bool), typeof(ViewportOcclusion), new PropertyMetadata(false, OnOccludesChanged));

    internal static bool GetOccludes(DependencyObject element) => (bool)element.GetValue(OccludesProperty);

    internal static void SetOccludes(DependencyObject element, bool value) => element.SetValue(OccludesProperty, value);

    // Weak, so a bar that is thrown away never stays alive through this list.
    static readonly List<WeakReference<FrameworkElement>> occluders = [];

    /// <summary>Raised when an occluder appears, disappears or moves without a layout pass, such as a sliding badge.</summary>
    internal static event EventHandler? Changed;

    /// <summary>Tells every viewport to cut its region again, for changes layout does not report.</summary>
    internal static void Invalidate() => Changed?.Invoke(null, EventArgs.Empty);

    static void OnOccludesChanged(DependencyObject element, DependencyPropertyChangedEventArgs change)
    {
        if (element is not FrameworkElement occluder) return;
        occluders.RemoveAll(entry => !entry.TryGetTarget(out var target) || ReferenceEquals(target, occluder));
        occluder.IsVisibleChanged -= OnVisibilityChanged;
        if ((bool)change.NewValue)
        {
            occluders.Add(new(occluder));
            occluder.IsVisibleChanged += OnVisibilityChanged;
        }
        Invalidate();
    }

    static void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs change) => Invalidate();

    /// <summary>The bounds, in the viewport's own coordinates, of every visible occluder in the viewport's window.</summary>
    internal static List<Rect> Holes(FrameworkElement viewport)
    {
        var holes = new List<Rect>();
        if (PresentationSource.FromVisual(viewport)?.RootVisual is not { } root) return holes;
        occluders.RemoveAll(entry => !entry.TryGetTarget(out _));
        foreach (var entry in occluders)
        {
            if (!entry.TryGetTarget(out var occluder) || !occluder.IsVisible) continue;
            // An occluder in another window, or one that holds the viewport itself, never cuts it.
            if (!ReferenceEquals(PresentationSource.FromVisual(occluder)?.RootVisual, root)
                || occluder.IsAncestorOf(viewport)) continue;
            if (Bounds(occluder, viewport) is { IsEmpty: false } bounds) holes.Add(bounds);
        }
        return holes;
    }

    // Both where the occluder is drawn now and where layout put it, so a bar or badge that slides on a render
    // transform stays cut along its whole path. The drawn bounds take in children and shadows outside the box.
    static Rect Bounds(FrameworkElement occluder, Visual viewport)
    {
        try
        {
            Rect drawn = VisualTreeHelper.GetDescendantBounds(occluder);
            drawn.Union(new Rect(occluder.RenderSize));
            // Descendant bounds leave out an effect's spread, so a shadowed badge or bar keeps room for its shadow.
            if (CastsShadow(occluder, 3)) drawn.Inflate(ShadowSpread, ShadowSpread);
            // A clipped occluder, such as a minimized tool bar cut down to its strip, only covers what its clip keeps.
            if (occluder.Clip is { } clip) drawn.Intersect(clip.Bounds);
            if (drawn.IsEmpty) return Rect.Empty;
            Rect result = occluder.TransformToVisual(viewport).TransformBounds(drawn);
            if (VisualTreeHelper.GetParent(occluder) is Visual parent)
            {
                Vector offset = VisualTreeHelper.GetOffset(occluder);
                drawn.Offset(offset);
                result.Union(parent.TransformToVisual(viewport).TransformBounds(drawn));
            }
            return result;
        }
        catch (InvalidOperationException)
        {
            // Mid-teardown, an occluder can briefly lose its common ancestor with the viewport.
            return Rect.Empty;
        }
    }

    // The family's bar and badge shadows blur 7 and drop 2, so 10 covers them with a pixel to spare.
    const double ShadowSpread = 10;

    static bool CastsShadow(Visual visual, int depth)
    {
        if (visual is UIElement { Effect: not null }) return true;
        if (depth == 0) return false;
        for (int i = 0, count = VisualTreeHelper.GetChildrenCount(visual); i < count; i++)
            if (VisualTreeHelper.GetChild(visual, i) is Visual child && CastsShadow(child, depth - 1)) return true;
        return false;
    }

    /// <summary>Removes the holes from a viewport clip. Holes are padded by a pixel so antialiased edges stay WPF's.</summary>
    internal static Geometry Cut(Geometry clip, IReadOnlyList<Rect> holes)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ArgumentNullException.ThrowIfNull(holes);
        if (holes.Count == 0 || clip.IsEmpty()) return clip;
        Geometry result = clip;
        foreach (Rect hole in holes)
        {
            if (hole.IsEmpty || hole.Width <= 0 || hole.Height <= 0) continue;
            Rect padded = hole;
            padded.Inflate(1, 1);
            result = Geometry.Combine(result, new RectangleGeometry(padded), GeometryCombineMode.Exclude, null);
        }
        if (!result.IsFrozen && result.CanFreeze) result.Freeze();
        return result;
    }
}
