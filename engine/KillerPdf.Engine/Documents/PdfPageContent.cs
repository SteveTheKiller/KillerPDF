using KillerPdf.Engine.Authoring;
using KillerPdf.Engine.Parsing;

namespace KillerPdf.Engine.Documents;

/// <summary>An axis-aligned rectangle in PDF points.</summary>
public readonly record struct PdfContentBounds(double Left, double Bottom, double Right, double Top)
{
    /// <summary>Gets the rectangle width.</summary>
    public double Width => Right - Left;
    /// <summary>Gets the rectangle height.</summary>
    public double Height => Top - Bottom;
    internal static PdfContentBounds Union(IEnumerable<PdfContentBounds> boxes)
    {
        var items = boxes.ToArray();
        return items.Length == 0 ? default : new(items.Min(b => b.Left), items.Min(b => b.Bottom),
            items.Max(b => b.Right), items.Max(b => b.Top));
    }
}

/// <summary>Decoded text and its font and page geometry.</summary>
public sealed record PdfExtractedLetter(string Value, PdfContentBounds BoundingBox,
    string FontName, double FontSize, double PointSize, PdfPoint StartBaseLine, PdfPoint EndBaseLine)
{
    /// <summary>Gets the glyph bounds in PDF points.</summary>
    public PdfContentBounds GlyphRectangle => BoundingBox;
    /// <summary>Gets the dominant direction of the transformed text advance.</summary>
    public PdfWritingDirection WritingDirection
    {
        get
        {
            double dx = EndBaseLine.X - StartBaseLine.X;
            double dy = EndBaseLine.Y - StartBaseLine.Y;
            return Math.Abs(dx) >= Math.Abs(dy)
                ? dx >= 0 ? PdfWritingDirection.LeftToRight : PdfWritingDirection.RightToLeft
                : dy >= 0 ? PdfWritingDirection.BottomToTop : PdfWritingDirection.TopToBottom;
        }
    }
}

/// <summary>The dominant direction of a transformed text advance.</summary>
public enum PdfWritingDirection
{
    /// <summary>Text advances toward increasing X coordinates.</summary>
    LeftToRight,
    /// <summary>Text advances toward decreasing X coordinates.</summary>
    RightToLeft,
    /// <summary>Text advances toward decreasing Y coordinates.</summary>
    TopToBottom,
    /// <summary>Text advances toward increasing Y coordinates.</summary>
    BottomToTop
}

/// <summary>A geometrically contiguous word.</summary>
public sealed class PdfExtractedWord
{
    internal PdfExtractedWord(IEnumerable<PdfExtractedLetter> letters)
    {
        Letters = Array.AsReadOnly(letters.ToArray());
        Text = string.Concat(Letters.Select(l => l.Value));
        BoundingBox = PdfContentBounds.Union(Letters.Select(l => l.BoundingBox));
    }
    /// <summary>Gets the word text.</summary>
    public string Text { get; }
    /// <summary>Gets the word bounds.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets the constituent characters in content order.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; }
}

/// <summary>An image placement in PDF coordinates.</summary>
public sealed record PdfExtractedImage(
    PdfContentBounds BoundingBox,
    string? ResourceName = null,
    bool IsInline = false,
    int PixelWidth = 0,
    int PixelHeight = 0,
    double RenderedWidth = 0,
    double RenderedHeight = 0)
{
    /// <summary>Gets the effective horizontal resolution in dots per inch.</summary>
    public double? HorizontalDpi => PixelWidth > 0 && RenderedWidth > 0
        ? PixelWidth * 72d / RenderedWidth : null;

    /// <summary>Gets the effective vertical resolution in dots per inch.</summary>
    public double? VerticalDpi => PixelHeight > 0 && RenderedHeight > 0
        ? PixelHeight * 72d / RenderedHeight : null;
}

/// <summary>One geometry-building operator in an extracted vector path.</summary>
public sealed record PdfExtractedPathSegment(string Operator, IReadOnlyList<PdfPoint> Points);

/// <summary>A vector path and the operator that paints, clips, or discards it.</summary>
public sealed record PdfExtractedPath(
    IReadOnlyList<PdfExtractedPathSegment> Segments,
    PdfContentBounds BoundingBox,
    string PaintOperator,
    bool IsClippingPath);

/// <summary>A contiguous sequence of extracted characters sharing font and writing direction.</summary>
public sealed class PdfExtractedTextRun
{
    internal PdfExtractedTextRun(IEnumerable<PdfExtractedLetter> letters)
    {
        Letters = Array.AsReadOnly(letters.ToArray());
        Text = string.Concat(Letters.Select(letter => letter.Value));
        BoundingBox = PdfContentBounds.Union(Letters.Select(letter => letter.BoundingBox));
        FontName = Letters[0].FontName;
        FontSize = Letters[0].FontSize;
        PointSize = Letters.Max(letter => letter.PointSize);
        WritingDirection = Letters[0].WritingDirection;
    }

    /// <summary>Gets the run text in content order.</summary>
    public string Text { get; }
    /// <summary>Gets the run bounds in PDF points.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets the source font face name.</summary>
    public string FontName { get; }
    /// <summary>Gets the source text-state font size.</summary>
    public double FontSize { get; }
    /// <summary>Gets the largest effective character size in the run.</summary>
    public double PointSize { get; }
    /// <summary>Gets the run's dominant writing direction.</summary>
    public PdfWritingDirection WritingDirection { get; }
    /// <summary>Gets the run's characters in content order.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; }
}

/// <summary>Text runs that occupy the same visual baseline.</summary>
public sealed class PdfExtractedLine
{
    internal PdfExtractedLine(IEnumerable<PdfExtractedTextRun> runs)
    {
        Runs = Array.AsReadOnly(runs.ToArray());
        Text = string.Concat(Runs.Select(run => run.Text));
        BoundingBox = PdfContentBounds.Union(Runs.Select(run => run.BoundingBox));
        WritingDirection = Runs[0].WritingDirection;
    }

    /// <summary>Gets the line text in content order.</summary>
    public string Text { get; }
    /// <summary>Gets the line bounds in PDF points.</summary>
    public PdfContentBounds BoundingBox { get; }
    /// <summary>Gets the line's dominant writing direction.</summary>
    public PdfWritingDirection WritingDirection { get; }
    /// <summary>Gets the line's text runs in content order.</summary>
    public IReadOnlyList<PdfExtractedTextRun> Runs { get; }
}

/// <summary>Text and image geometry extracted from an unrotated page.</summary>
public sealed class PdfPageContent
{
    internal PdfPageContent(double width, double height, IEnumerable<PdfExtractedLetter> letters,
        IEnumerable<PdfExtractedImage> images, IEnumerable<PdfContentInstruction> instructions,
        IEnumerable<PdfExtractedPath> paths, IEnumerable<string>? diagnostics = null)
    {
        Width = width;
        Height = height;
        Letters = Array.AsReadOnly(letters.ToArray());
        Images = Array.AsReadOnly(images.ToArray());
        Instructions = Array.AsReadOnly(instructions.ToArray());
        Paths = Array.AsReadOnly(paths.ToArray());
        Diagnostics = Array.AsReadOnly((diagnostics ?? []).ToArray());
        Words = GroupWords(Letters);
        TextRuns = GroupTextRuns(Letters);
        Lines = GroupLines(TextRuns);
        Text = string.Join(" ", Words.Select(w => w.Text));
    }
    /// <summary>Gets the page width in points.</summary>
    public double Width { get; }
    /// <summary>Gets the page height in points.</summary>
    public double Height { get; }
    /// <summary>Gets all decoded characters.</summary>
    public IReadOnlyList<PdfExtractedLetter> Letters { get; }
    /// <summary>Gets geometrically grouped words.</summary>
    public IReadOnlyList<PdfExtractedWord> Words { get; }
    /// <summary>Gets contiguous text runs with shared font and writing direction.</summary>
    public IReadOnlyList<PdfExtractedTextRun> TextRuns { get; }
    /// <summary>Gets text runs grouped by visual baseline.</summary>
    public IReadOnlyList<PdfExtractedLine> Lines { get; }
    /// <summary>Gets image placements.</summary>
    public IReadOnlyList<PdfExtractedImage> Images { get; }
    /// <summary>Gets the interpreted page instructions, including expanded Form XObjects.</summary>
    public IReadOnlyList<PdfContentInstruction> Instructions { get; }
    /// <summary>Gets vector paths in interpreted painting order.</summary>
    public IReadOnlyList<PdfExtractedPath> Paths { get; }
    /// <summary>Gets compatibility recoveries encountered while extracting this page.</summary>
    public IReadOnlyList<string> Diagnostics { get; }
    /// <summary>Gets words separated by spaces in content order.</summary>
    public string Text { get; }
    /// <summary>Enumerates extracted words.</summary>
    public IEnumerable<PdfExtractedWord> GetWords() => Words;
    /// <summary>Enumerates image placements.</summary>
    public IEnumerable<PdfExtractedImage> GetImages() => Images;

    private static List<PdfExtractedTextRun> GroupTextRuns(IReadOnlyList<PdfExtractedLetter> letters)
    {
        var runs = new List<PdfExtractedTextRun>();
        var current = new List<PdfExtractedLetter>();
        void Flush()
        {
            if (current.Count > 0) { runs.Add(new PdfExtractedTextRun(current)); current.Clear(); }
        }
        foreach (var letter in letters)
        {
            if (current.Count > 0)
            {
                var first = current[0];
                var previous = current[^1];
                bool sameFont = string.Equals(first.FontName, letter.FontName, StringComparison.Ordinal)
                    && Math.Abs(first.FontSize - letter.FontSize) <= 0.001;
                bool sameDirection = first.WritingDirection == letter.WritingDirection;
                if (!sameFont || !sameDirection || !SharesBaseline(previous, letter)) Flush();
            }
            current.Add(letter);
        }
        Flush();
        return runs;
    }

    private static List<PdfExtractedLine> GroupLines(IReadOnlyList<PdfExtractedTextRun> runs)
    {
        var lines = new List<PdfExtractedLine>();
        var current = new List<PdfExtractedTextRun>();
        void Flush()
        {
            if (current.Count > 0) { lines.Add(new PdfExtractedLine(current)); current.Clear(); }
        }
        foreach (var run in runs)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                if (previous.WritingDirection != run.WritingDirection
                    || !SharesBaseline(previous.Letters[^1], run.Letters[0])) Flush();
            }
            current.Add(run);
        }
        Flush();
        return lines;
    }

    private static bool SharesBaseline(PdfExtractedLetter first, PdfExtractedLetter second)
    {
        double dx = first.EndBaseLine.X - first.StartBaseLine.X;
        double dy = first.EndBaseLine.Y - first.StartBaseLine.Y;
        double length = Math.Sqrt(dx * dx + dy * dy);
        double ux = length > 0.001 ? dx / length : 1;
        double uy = length > 0.001 ? dy / length : 0;
        double offsetX = second.StartBaseLine.X - first.EndBaseLine.X;
        double offsetY = second.StartBaseLine.Y - first.EndBaseLine.Y;
        double across = Math.Abs(offsetY * ux - offsetX * uy);
        return across <= Math.Max(1, Math.Min(first.PointSize, second.PointSize) * 0.35);
    }

    private static List<PdfExtractedWord> GroupWords(IReadOnlyList<PdfExtractedLetter> letters)
    {
        var words = new List<PdfExtractedWord>();
        var current = new List<PdfExtractedLetter>();
        void Flush()
        {
            if (current.Count > 0) { words.Add(new PdfExtractedWord(current)); current.Clear(); }
        }
        foreach (var letter in letters)
        {
            if (string.IsNullOrWhiteSpace(letter.Value)) { Flush(); continue; }
            if (current.Count > 0)
            {
                var previous = current[^1];
                double dx = previous.EndBaseLine.X - previous.StartBaseLine.X;
                double dy = previous.EndBaseLine.Y - previous.StartBaseLine.Y;
                double length = Math.Sqrt(dx * dx + dy * dy);
                double ux = length > 0.001 ? dx / length : 1, uy = length > 0.001 ? dy / length : 0;
                double gapX = letter.StartBaseLine.X - previous.EndBaseLine.X;
                double gapY = letter.StartBaseLine.Y - previous.EndBaseLine.Y;
                double gap = gapX * ux + gapY * uy;
                double across = Math.Abs(gapY * ux - gapX * uy);
                double em = Math.Max(1, Math.Min(previous.PointSize, letter.PointSize));
                if (across > em * 0.35 || gap > em * 0.22 || gap < -Math.Max(length, em * 0.5)) Flush();
            }
            current.Add(letter);
        }
        Flush();
        return words;
    }
}
