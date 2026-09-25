using System.Windows;

namespace KillerPDF.Services;

internal static class AnnotationNudge
{
    internal readonly record struct Constraint(Rect Bounds, Size PageSize);

    internal static Vector ClampDelta(IEnumerable<Constraint> constraints, Vector requested)
    {
        double minX = double.NegativeInfinity;
        double maxX = double.PositiveInfinity;
        double minY = double.NegativeInfinity;
        double maxY = double.PositiveInfinity;
        bool found = false;

        foreach (var constraint in constraints)
        {
            if (constraint.Bounds.IsEmpty || constraint.PageSize.Width <= 0 || constraint.PageSize.Height <= 0)
                continue;

            found = true;
            minX = Math.Max(minX, -constraint.Bounds.Left);
            maxX = Math.Min(maxX, Math.Max(0, constraint.PageSize.Width - constraint.Bounds.Right));
            minY = Math.Max(minY, -constraint.Bounds.Top);
            maxY = Math.Min(maxY, Math.Max(0, constraint.PageSize.Height - constraint.Bounds.Bottom));
        }

        if (!found) return default;

        return new Vector(
            Math.Clamp(requested.X, minX, maxX),
            Math.Clamp(requested.Y, minY, maxY));
    }
}
