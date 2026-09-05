namespace KillerPdf.Engine.Documents;

/// <summary>A pixel-space OCR region with a top-left origin.</summary>
public sealed record PdfOcrImageRegion(int Left, int Top, int Right, int Bottom)
{
    /// <summary>Gets the region width.</summary>
    public int Width => Right - Left;
    /// <summary>Gets the region height.</summary>
    public int Height => Bottom - Top;
}

/// <summary>A detected OCR word candidate and its ordered connected components.</summary>
public sealed record PdfOcrWordRegion(
    PdfOcrImageRegion Bounds, IReadOnlyList<PdfOcrImageRegion> Components);

/// <summary>A detected OCR text line with ordered words and connected components.</summary>
public sealed record PdfOcrTextLine(PdfOcrImageRegion Bounds,
    IReadOnlyList<PdfOcrWordRegion> Words, IReadOnlyList<PdfOcrImageRegion> Components);

/// <summary>A coherent page region containing text lines in reading order.</summary>
public sealed record PdfOcrPageSegment(
    PdfOcrImageRegion Bounds, IReadOnlyList<PdfOcrTextLine> Lines);

/// <summary>Connected-component and line analysis for a prepared OCR image.</summary>
public sealed class PdfOcrPageLayout
{
    internal PdfOcrPageLayout(IEnumerable<PdfOcrPageSegment> segments)
    {
        Segments = Array.AsReadOnly(segments.ToArray());
        Lines = Array.AsReadOnly(Segments.SelectMany(segment => segment.Lines).ToArray());
        Words = Array.AsReadOnly(Lines.SelectMany(line => line.Words).ToArray());
        Components = Array.AsReadOnly(Lines.SelectMany(line => line.Components).ToArray());
    }

    /// <summary>Gets detected page segments in reading order.</summary>
    public IReadOnlyList<PdfOcrPageSegment> Segments { get; }
    /// <summary>Gets detected components in reading order.</summary>
    public IReadOnlyList<PdfOcrImageRegion> Components { get; }
    /// <summary>Gets detected word candidates in reading order.</summary>
    public IReadOnlyList<PdfOcrWordRegion> Words { get; }
    /// <summary>Gets detected text lines from top to bottom.</summary>
    public IReadOnlyList<PdfOcrTextLine> Lines { get; }
}

/// <summary>Detects bounded text candidates in engine-prepared OCR rasters.</summary>
public static class PdfOcrLayoutAnalyzer
{
    /// <summary>Finds dark connected components and groups them into text lines.</summary>
    public static PdfOcrPageLayout Analyze(PdfOcrPreparedImage image,
        CancellationToken cancellationToken = default) =>
        Analyze(image, detectPageSegments: false, cancellationToken);

    /// <summary>Finds text candidates and optionally orders independent page columns.</summary>
    public static PdfOcrPageLayout Analyze(PdfOcrPreparedImage image,
        bool detectPageSegments, CancellationToken cancellationToken = default)
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

        IReadOnlyList<IReadOnlyList<PdfOcrImageRegion>> columns = detectPageSegments
            ? SplitColumns(components) : components.Count == 0 ? [] : [components];
        return new PdfOcrPageLayout(columns.Select(column => Segment(BuildLines(column))));
    }

    private static IReadOnlyList<PdfOcrTextLine> BuildLines(
        IReadOnlyList<PdfOcrImageRegion> components)
    {
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
        return Array.AsReadOnly(lines.Select(line =>
        {
            PdfOcrImageRegion[] ordered = [.. line.OrderBy(item => item.Left)];
            IReadOnlyList<PdfOcrWordRegion> words = GroupWords(ordered);
            return new PdfOcrTextLine(new PdfOcrImageRegion(
                ordered.Min(item => item.Left), ordered.Min(item => item.Top),
                ordered.Max(item => item.Right), ordered.Max(item => item.Bottom)),
                words, Array.AsReadOnly(ordered));
        }).OrderBy(line => line.Bounds.Top).ThenBy(line => line.Bounds.Left).ToArray());
    }

    private static IReadOnlyList<IReadOnlyList<PdfOcrImageRegion>> SplitColumns(
        IReadOnlyList<PdfOcrImageRegion> components)
    {
        if (components.Count < 4) return components.Count == 0 ? [] : [components];
        PdfOcrImageRegion[] ordered = [.. components.OrderBy(item => item.Left)];
        int medianHeight = ordered.Select(item => item.Height).OrderBy(value => value)
            .ElementAt(ordered.Length / 2);
        int bestGap = 0, split = 0, right = ordered[0].Right;
        for (int index = 1; index < ordered.Length; index++)
        {
            int gap = ordered[index].Left - right;
            if (gap > bestGap)
            {
                PdfOcrImageRegion[] left = ordered[..index];
                PdfOcrImageRegion[] rightSide = ordered[index..];
                int leftSpan = left.Max(item => item.Bottom) - left.Min(item => item.Top);
                int rightSpan = rightSide.Max(item => item.Bottom)
                    - rightSide.Min(item => item.Top);
                if (left.Length >= 2 && rightSide.Length >= 2
                    && leftSpan >= medianHeight * 2 && rightSpan >= medianHeight * 2)
                {
                    bestGap = gap;
                    split = index;
                }
            }
            right = Math.Max(right, ordered[index].Right);
        }
        if (split == 0 || bestGap < Math.Max(4, medianHeight)) return [components];
        return [ordered[..split], ordered[split..]];
    }

    private static PdfOcrPageSegment Segment(IReadOnlyList<PdfOcrTextLine> lines) =>
        new(new PdfOcrImageRegion(
            lines.Min(line => line.Bounds.Left), lines.Min(line => line.Bounds.Top),
            lines.Max(line => line.Bounds.Right), lines.Max(line => line.Bounds.Bottom)),
            Array.AsReadOnly(lines.ToArray()));

    private static IReadOnlyList<PdfOcrWordRegion> GroupWords(
        IReadOnlyList<PdfOcrImageRegion> components)
    {
        if (components.Count == 0) return [];
        int[] widths = [.. components.Select(item => item.Width).OrderBy(value => value)];
        double medianWidth = widths[widths.Length / 2];
        int wordGap = Math.Max(2, (int)Math.Ceiling(medianWidth * 1.5));
        var groups = new List<List<PdfOcrImageRegion>> { new() { components[0] } };
        for (int index = 1; index < components.Count; index++)
        {
            PdfOcrImageRegion component = components[index];
            List<PdfOcrImageRegion> current = groups[^1];
            if (component.Left - current[^1].Right > wordGap)
                groups.Add([component]);
            else
                current.Add(component);
        }
        return Array.AsReadOnly(groups.Select(group => new PdfOcrWordRegion(
            new PdfOcrImageRegion(group.Min(item => item.Left), group.Min(item => item.Top),
                group.Max(item => item.Right), group.Max(item => item.Bottom)),
            Array.AsReadOnly(group.ToArray()))).ToArray());
    }
}
