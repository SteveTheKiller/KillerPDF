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
        ReadOnlyMemory<byte> pixels = image.Pixels;
        var visited = new byte[pixels.Length];
        var components = new List<PdfOcrImageRegion>();
        var queue = new Queue<int>();
        for (int index = 0; index < pixels.Length; index++)
        {
            if ((index & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (visited[index] != 0 || pixels.Span[index] >= 128) continue;
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
                if (visited[neighbor] != 0 || pixels.Span[neighbor] >= 128) return;
                visited[neighbor] = 1;
                queue.Enqueue(neighbor);
            }
        }

        components = SplitTouchingGlyphs(components, pixels, width);
        components = MergeDetachedMarks(components);
        IReadOnlyList<IReadOnlyList<PdfOcrImageRegion>> columns = detectPageSegments
            ? SplitColumns(components) : components.Count == 0 ? [] : [components];
        return new PdfOcrPageLayout(columns.Select(column => Segment(BuildLines(column))));
    }

    private static List<PdfOcrImageRegion> SplitTouchingGlyphs(
        IReadOnlyList<PdfOcrImageRegion> components, ReadOnlyMemory<byte> pixels,
        int imageWidth)
    {
        var split = new List<PdfOcrImageRegion>(components.Count);
        foreach (PdfOcrImageRegion component in components)
        {
            if (component.Height < 3
                || component.Width * 5 < component.Height * 6)
            {
                split.Add(component);
                continue;
            }
            int minimumPieceWidth = Math.Max(2, component.Height / 4);
            int maximumValleyInk = Math.Max(1, component.Height / 6);
            var cuts = new List<int>();
            int x = component.Left + minimumPieceWidth;
            while (x <= component.Right - minimumPieceWidth)
            {
                if (ColumnInk(x) > maximumValleyInk)
                {
                    x++;
                    continue;
                }
                int valleyStart = x;
                while (x < component.Right - minimumPieceWidth
                    && ColumnInk(x + 1) <= maximumValleyInk)
                    x++;
                int cut = (valleyStart + x + 1) / 2;
                int previous = cuts.Count == 0 ? component.Left : cuts[^1];
                if (cut - previous >= minimumPieceWidth
                    && component.Right - cut >= minimumPieceWidth)
                    cuts.Add(cut);
                x++;
            }
            if (cuts.Count == 0)
            {
                split.Add(component);
                continue;
            }
            int left = component.Left;
            foreach (int right in cuts.Append(component.Right))
            {
                PdfOcrImageRegion? piece = TightBounds(left, right);
                if (piece is not null) split.Add(piece);
                left = right;
            }

            int ColumnInk(int column)
            {
                int count = 0;
                for (int row = component.Top; row < component.Bottom; row++)
                    if (pixels.Span[row * imageWidth + column] < 128) count++;
                return count;
            }

            PdfOcrImageRegion? TightBounds(int columnStart, int columnEnd)
            {
                int tightLeft = columnEnd, tightTop = component.Bottom;
                int tightRight = columnStart, tightBottom = component.Top;
                for (int row = component.Top; row < component.Bottom; row++)
                    for (int column = columnStart; column < columnEnd; column++)
                        if (pixels.Span[row * imageWidth + column] < 128)
                        {
                            tightLeft = Math.Min(tightLeft, column);
                            tightTop = Math.Min(tightTop, row);
                            tightRight = Math.Max(tightRight, column + 1);
                            tightBottom = Math.Max(tightBottom, row + 1);
                        }
                return tightRight > tightLeft && tightBottom > tightTop
                    ? new PdfOcrImageRegion(
                        tightLeft, tightTop, tightRight, tightBottom) : null;
            }
        }
        return split;
    }

    private static List<PdfOcrImageRegion> MergeDetachedMarks(
        IReadOnlyList<PdfOcrImageRegion> components)
    {
        var merged = new List<PdfOcrImageRegion>(components.Count);
        var consumed = new bool[components.Count];
        int referenceHeight = components.Count == 0
            ? 0 : components.Max(component => component.Height);
        int[] order = [.. Enumerable.Range(0, components.Count)
            .OrderByDescending(index => components[index].Width * components[index].Height)];
        foreach (int baseIndex in order)
        {
            if (consumed[baseIndex]) continue;
            PdfOcrImageRegion bounds = components[baseIndex];
            int baseArea = bounds.Width * bounds.Height;
            for (int markIndex = 0; markIndex < components.Count; markIndex++)
            {
                if (markIndex == baseIndex || consumed[markIndex]) continue;
                PdfOcrImageRegion mark = components[markIndex];
                int markArea = mark.Width * mark.Height;
                bool subordinateMark = markArea * 2 <= baseArea;
                bool pairedStrokes = markArea <= baseArea * 2
                    && baseArea <= markArea * 2
                    && bounds.Height * 2 <= referenceHeight
                    && mark.Height * 2 <= referenceHeight
                    && Math.Max(bounds.Height, mark.Height)
                        <= Math.Max(bounds.Width, mark.Width);
                if (!subordinateMark && !pairedStrokes) continue;
                int horizontalOverlap = Math.Min(bounds.Right, mark.Right)
                    - Math.Max(bounds.Left, mark.Left);
                if (horizontalOverlap <= 0
                    || horizontalOverlap * 2 < Math.Min(bounds.Width, mark.Width))
                    continue;
                int verticalGap = mark.Bottom <= bounds.Top
                    ? bounds.Top - mark.Bottom
                    : bounds.Bottom <= mark.Top ? mark.Top - bounds.Bottom : 0;
                int maximumGap = subordinateMark
                    ? Math.Max(2, bounds.Height / 2)
                    : Math.Max(2, Math.Max(bounds.Width, mark.Width) * 2);
                if (verticalGap > maximumGap) continue;
                bounds = new PdfOcrImageRegion(
                    Math.Min(bounds.Left, mark.Left), Math.Min(bounds.Top, mark.Top),
                    Math.Max(bounds.Right, mark.Right), Math.Max(bounds.Bottom, mark.Bottom));
                consumed[markIndex] = true;
                baseArea = bounds.Width * bounds.Height;
            }
            consumed[baseIndex] = true;
            merged.Add(bounds);
        }
        return merged;
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
