using System.Windows.Media;
using System.Windows;
using System.Runtime.InteropServices;

namespace KillerPDF.Presentation;

/// <summary>Applies layout-derived clipping to an owned native viewport container.</summary>
internal sealed class WindowsPdfViewportRegion
{
    bool updating;
    internal void Update(nint container, Geometry? clip = null)
    {
        ArgumentNullException.ThrowIfNull(clip);
        uint thread = GetWindowThreadProcessId(container, out uint process);
        if (container == 0 || process != Environment.ProcessId || thread != GetCurrentThreadId())
            throw new InvalidOperationException("Viewport clipping requires a window owned by the current process and thread.");
        if (updating) throw new InvalidOperationException("Viewport clipping cannot reenter an update.");
        updating = true;
        nint region = 0;
        try
        {
            if (!GetClientRect(container, out var client) || !GetWindowRect(container, out var window)
                || client.Right != window.Right - window.Left || client.Bottom != window.Bottom - window.Top)
                throw new NotSupportedException("Viewport clipping requires a borderless container.");
            double scale = GetDpiForWindow(container) / 96d;
            if (scale <= 0) throw new InvalidOperationException("Viewport DPI is unavailable.");
            var flat = clip.GetFlattenedPathGeometry(.1 / scale, ToleranceType.Absolute);
            if (flat.Figures.Count is < 1 or > 256)
                throw new NotSupportedException("Viewport clipping exceeds the contour limit.");
            var points = new List<Point>();
            var counts = new List<int>();
            foreach (var figure in flat.Figures)
            {
            if (!figure.IsClosed || !figure.IsFilled)
                throw new NotSupportedException("Viewport clipping requires closed filled contours.");
            int start = points.Count;
            points.Add(figure.StartPoint);
            foreach (var segment in figure.Segments)
                if (segment is PolyLineSegment poly) points.AddRange(poly.Points);
                else if (segment is LineSegment line) points.Add(line.Point);
                else throw new NotSupportedException("Unexpected flattened viewport segment.");
            int count = points.Count - start;
            if (count < 3 || points.Count > 4096) throw new NotSupportedException("Viewport contour exceeds the point limit.");
            counts.Add(count);
            }
            var native = points.Select(point =>
            {
                Point mapped = flat.Transform?.Transform(point) ?? point;
                double x = mapped.X * scale, y = mapped.Y * scale;
                if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 1_000_000 || Math.Abs(y) > 1_000_000)
                    throw new NotSupportedException("Viewport contour coordinates exceed the supported range.");
                return new PointI { X = (int)Math.Round(x), Y = (int)Math.Round(y) };
            }).ToArray();
            region = CreatePolyPolygonRgn(native, counts.ToArray(), counts.Count, flat.FillRule == FillRule.Nonzero ? 2 : 1);
            if (region == 0 || SetWindowRgn(container, region, true) == 0)
                throw new InvalidOperationException("The native viewport clip could not be installed.");
            region = 0;
        }
        catch { ShowWindow(container, 0); throw; }
        finally
        {
            if (region != 0) DeleteObject(region);
            updating = false;
        }
    }
    [StructLayout(LayoutKind.Sequential)] struct PointI { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct RectI { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint process);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool GetClientRect(nint window, out RectI rect);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint window, out RectI rect);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")] static extern int SetWindowRgn(nint window, nint region, bool redraw);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint window, int command);
    [DllImport("gdi32.dll")] static extern nint CreatePolyPolygonRgn(PointI[] points, int[] counts, int polygonCount, int mode);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint region);
}
