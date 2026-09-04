namespace KillerPdf.Engine.Documents;

/// <summary>A pixel-space OCR region with a top-left origin.</summary>
public sealed record PdfOcrImageRegion(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the region width.</summary>
    public int Width => Right - Left;
    /// <summary>Gets the region height.</summary>
    public int Height => Bottom - Top;
}

/// <summary>A detected OCR text line and its ordered connected components.</summary>
public sealed record PdfOcrTextLine(
    PdfOcrImageRegion Bounds, IReadOnlyList<PdfOcrImageRegion> Components);

/// <summary>Connected-component and line analysis for a prepared OCR image.</summary>
public sealed class PdfOcrPageLayout
{
    internal PdfOcrPageLayout(IEnumerable<PdfOcrTextLine> lines)
    {
        Lines = Array.AsReadOnly(lines.ToArray());
        Components = Array.AsReadOnly(Lines.SelectMany(line => line.Components).ToArray());
    }

    /// <summary>Gets detected components in reading order.</summary>
    public IReadOnlyList<PdfOcrImageRegion> Components { get; }
    /// <summary>Gets detected text lines from top to bottom.</summary>
    public IReadOnlyList<PdfOcrTextLine> Lines { get; }
}

/// <summary>Detects bounded text candidates in engine-prepared OCR rasters.</summary>
public static class PdfOcrLayoutAnalyzer
{
    /// <summary>Finds dark connected components and groups them into text lines.</summary>
    public static PdfOcrPageLayout Analyze(PdfOcrPreparedImage image,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(image);
        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels.ToArray();
        var visited = new byte[pixels.Length];
        var components = new List<PdfOcrImageRegion>();
        var queue = new Queue<int>();
        for (int index = 0; index < pixels.Length; index++)
        {
            if ((index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[index] != 0 || pixels[index] >= 128) continue;
            visited[index] = 1;
            queue.Enqueue(index);
            int left = index % width, right = left + 1;
            int top = index / width, bottom = top + 1;
            int count = 0;
            while (queue.Count > 0)
            {
                int current = queue.Dequeue();
                int x = current % width, y = current / width;
                left = Math.Min(left, x); right = Math.Max(right, x + 1);
                top = Math.Min(top, y); bottom = Math.Max(bottom, y + 1);
                count++;
                Visit(x - 1, y); Visit(x + 1, y); Visit(x, y - 1); Visit(x, y + 1);
            }
            if (count >= 2 && right - left <= width * 3 / 4 && bottom - top <= height * 3 / 4)
                components.Add(new PdfOcrImageRegion(left, top, right, bottom));

            void Visit(int x, int y)
            {
                if (x < 0 || x >= width || y < 0 || y >= height) return;
                int neighbor = y * width + x;
                if (visited[neighbor] != 0 || pixels[neighbor] >= 128) return;
                visited[neighbor] = 1;
                queue.Enqueue(neighbor);
            }
        }

        var lines = new List<List<PdfOcrImageRegion>>();
        foreach (PdfOcrImageRegion component in components
            .OrderBy(item => item.Top).ThenBy(item => item.Left))
        {
            List<PdfOcrImageRegion>? line = lines.FirstOrDefault(candidate =>
            {
                int top = candidate.Min(item => item.Top);
                int bottom = candidate.Max(item => item.Bottom);
                int overlap = Math.Min(bottom, component.Bottom) - Math.Max(top, component.Top);
                return overlap > 0 && overlap * 2 >= Math.Min(bottom - top, component.Height);
            });
            if (line is null) lines.Add([component]);
            else line.Add(component);
        }
        return new PdfOcrPageLayout(lines.Select(line =>
        {
            PdfOcrImageRegion[] ordered = [.. line.OrderBy(item => item.Left)];
            return new PdfOcrTextLine(new PdfOcrImageRegion(
                ordered.Min(item => item.Left), ordered.Min(item => item.Top),
                ordered.Max(item => item.Right), ordered.Max(item => item.Bottom)),
                Array.AsReadOnly(ordered));
        }).OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left));
    }
}
